using System.Net;
using System.Text;
using Mailserver.Core;
using Mailserver.Core.Accounts;
using Mailserver.Core.Security;
using Mailserver.Core.SpamLogging;
using Mailserver.Core.Storage;
using Mailserver.Imap.Protocol;
using Microsoft.Extensions.Logging;

namespace Mailserver.Imap.Session;

/// <summary>
/// One IMAP4rev1 connection (RFC 3501) with the extensions LITERAL+, SASL-IR, ID, ENABLE, IDLE, NAMESPACE, UNSELECT,
/// UIDPLUS, MOVE, CHILDREN and SPECIAL-USE.
/// </summary>
public sealed partial class ImapSession(
    ImapConnection connection,
    IPAddress? remoteAddress,
    bool isSecure,
    Func<Stream, CancellationToken, Task<Stream>>? startTls,
    AccountStore accounts,
    MailboxStore mailboxes,
    AuthThrottle throttle,
    FolderWatcher watcher,
    SpamLog spamLog,
    MailserverOptions options,
    ILogger logger)
{
    private Account? _account;
    private SelectedFolder? _selected;
    private bool _secure = isSecure;
    private bool _logout;

    private bool LoginAllowed => _secure || options.Imap.AllowInsecureAuthentication;

    private string Capabilities
    {
        get
        {
            var capabilities = new StringBuilder("IMAP4rev1 LITERAL+ SASL-IR ID ENABLE IDLE NAMESPACE UNSELECT UIDPLUS MOVE CHILDREN SPECIAL-USE");
            if (_account is null)
            {
                if (!_secure && startTls is not null)
                {
                    capabilities.Append(" STARTTLS");
                }

                capabilities.Append(LoginAllowed ? " AUTH=PLAIN" : " LOGINDISABLED");
            }

            return capabilities.ToString();
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await connection.WriteAsync($"* OK [CAPABILITY {Capabilities}] {options.Hostname} IMAP ready\r\n", cancellationToken);

        while (!_logout && !cancellationToken.IsCancellationRequested)
        {
            ImapCommand? command;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                idle.CancelAfter(options.Imap.IdleTimeout);
                try
                {
                    command = await connection.ReadCommandAsync(idle.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    await connection.WriteAsync("* BYE Autologout; idle for too long\r\n", CancellationToken.None);
                    return;
                }
                catch (TaggedParseException ex)
                {
                    await connection.WriteAsync($"{ex.Tag} BAD {ex.Message}\r\n", cancellationToken);
                    continue;
                }
                catch (ImapParseException ex)
                {
                    await connection.WriteAsync($"* BAD {ex.Message}\r\n", cancellationToken);
                    continue;
                }
            }

            if (command is null)
            {
                return;
            }

            try
            {
                await ExecuteAsync(command, cancellationToken);
            }
            catch (ImapParseException ex)
            {
                await connection.WriteAsync($"{command.Tag} BAD {ex.Message}\r\n", cancellationToken);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or IOException or ImapProtocolException))
            {
                logger.LogError(ex, "IMAP command {Command} failed for {User}", command.Name, _account?.Address);
                await connection.WriteAsync($"{command.Tag} NO [SERVERBUG] Internal error\r\n", cancellationToken);
            }
        }
    }

    private Task ExecuteAsync(ImapCommand command, CancellationToken cancellationToken)
    {
        var args = command.Arguments;
        switch (command.Name)
        {
            case "CAPABILITY":
                return Respond(command, $"* CAPABILITY {Capabilities}\r\n", "OK CAPABILITY completed", cancellationToken);
            case "NOOP":
                return NoopAsync(command, cancellationToken);
            case "LOGOUT":
                _logout = true;
                return Respond(command, "* BYE Logging out\r\n", "OK LOGOUT completed", cancellationToken);
            case "ID":
                return Respond(command, "* ID (\"name\" \"Mailserver\")\r\n", "OK ID completed", cancellationToken);
        }

        if (_account is null)
        {
            return command.Name switch
            {
                "STARTTLS" => StartTlsAsync(command, cancellationToken),
                "LOGIN" => LoginAsync(command, args, cancellationToken),
                "AUTHENTICATE" => AuthenticateAsync(command, args, cancellationToken),
                _ => Respond(command, null, "BAD Command not allowed before login", cancellationToken),
            };
        }

        switch (command.Name)
        {
            case "ENABLE":
                return Respond(command, "* ENABLED\r\n", "OK ENABLE completed", cancellationToken);
            case "NAMESPACE":
                return Respond(command, "* NAMESPACE ((\"\" \"/\")) NIL NIL\r\n", "OK NAMESPACE completed", cancellationToken);
            case "SELECT":
            case "EXAMINE":
                return SelectAsync(command, args, readOnly: command.Name == "EXAMINE", cancellationToken);
            case "CREATE":
                return CreateAsync(command, args, cancellationToken);
            case "DELETE":
                return DeleteAsync(command, args, cancellationToken);
            case "RENAME":
                return RenameAsync(command, args, cancellationToken);
            case "SUBSCRIBE":
            case "UNSUBSCRIBE":
                return SubscribeAsync(command, args, command.Name == "SUBSCRIBE", cancellationToken);
            case "LIST":
            case "LSUB":
                return ListAsync(command, args, lsub: command.Name == "LSUB", cancellationToken);
            case "STATUS":
                return StatusAsync(command, args, cancellationToken);
            case "APPEND":
                return AppendAsync(command, args, cancellationToken);
            case "IDLE":
                return IdleAsync(command, cancellationToken);
        }

        if (_selected is null)
        {
            return Respond(command, null, "BAD No folder selected", cancellationToken);
        }

        var byUid = command.Name == "UID";
        var name = command.Name;
        if (byUid)
        {
            if (args.Count == 0)
            {
                throw new ImapParseException("Missing UID command");
            }

            name = args[0].AsAtom().ToUpperInvariant();
            args = args.Skip(1).ToList();
        }

        return name switch
        {
            "CHECK" when !byUid => NoopAsync(command, cancellationToken),
            "CLOSE" when !byUid => CloseAsync(command, expunge: true, cancellationToken),
            "UNSELECT" when !byUid => CloseAsync(command, expunge: false, cancellationToken),
            "EXPUNGE" => ExpungeAsync(command, args, byUid, cancellationToken),
            "FETCH" => FetchAsync(command, args, byUid, cancellationToken),
            "STORE" => StoreAsync(command, args, byUid, cancellationToken),
            "SEARCH" => SearchAsync(command, args, byUid, cancellationToken),
            "COPY" => CopyAsync(command, args, byUid, move: false, cancellationToken),
            "MOVE" => CopyAsync(command, args, byUid, move: true, cancellationToken),
            _ => Respond(command, null, "BAD Unknown command", cancellationToken),
        };
    }

    private async Task NoopAsync(ImapCommand command, CancellationToken cancellationToken)
    {
        var response = new ImapResponse();
        _selected?.Sync(response, allowExpunge: true);
        response.Raw($"{command.Tag} OK {command.Name} completed\r\n");
        await connection.WriteAsync(response.ToMemory(), cancellationToken);
    }

    // ---- Authentication ----

    private async Task StartTlsAsync(ImapCommand command, CancellationToken cancellationToken)
    {
        if (_secure || startTls is null)
        {
            await Respond(command, null, "BAD TLS is already active or not available", cancellationToken);
            return;
        }

        if (connection.HasBufferedInput)
        {
            // Plaintext pipelined after STARTTLS would be processed as if it came over TLS (CVE-2011-0411 class).
            throw new ImapProtocolException("Data received after STARTTLS");
        }

        await connection.WriteAsync($"{command.Tag} OK Begin TLS negotiation now\r\n", cancellationToken);
        connection.UpgradeStream(await startTls(connection.Stream, cancellationToken));
        _secure = true;
    }

    private async Task LoginAsync(ImapCommand command, IReadOnlyList<ImapToken> args, CancellationToken cancellationToken)
    {
        if (args.Count != 2)
        {
            throw new ImapParseException("LOGIN expects user name and password");
        }

        await CompleteLoginAsync(command, args[0].AsString(), args[1].AsString(), cancellationToken);
    }

    private async Task AuthenticateAsync(ImapCommand command, IReadOnlyList<ImapToken> args, CancellationToken cancellationToken)
    {
        if (args.Count is < 1 or > 2 || !args[0].AsAtom().Equals("PLAIN", StringComparison.OrdinalIgnoreCase))
        {
            await Respond(command, null, "NO Unsupported authentication mechanism", cancellationToken);
            return;
        }

        if (!LoginAllowed)
        {
            await Respond(command, null, "NO [PRIVACYREQUIRED] Use STARTTLS first", cancellationToken);
            return;
        }

        string? initial = args.Count == 2 ? args[1].AsString() : null;
        if (initial is null)
        {
            await connection.WriteAsync("+ \r\n", cancellationToken);
            initial = await connection.ReadLineAsync(cancellationToken) ?? throw new ImapProtocolException("Connection closed");
        }

        if (initial == "*")
        {
            await Respond(command, null, "BAD Authentication cancelled", cancellationToken);
            return;
        }

        string[] parts;
        try
        {
            parts = Encoding.UTF8.GetString(Convert.FromBase64String(initial == "=" ? "" : initial)).Split('\0');
        }
        catch (FormatException)
        {
            await Respond(command, null, "BAD Invalid base64", cancellationToken);
            return;
        }

        // authzid \0 authcid \0 password; acting as another user is not supported.
        if (parts.Length != 3 || (parts[0].Length > 0 && !parts[0].Equals(parts[1], StringComparison.OrdinalIgnoreCase)))
        {
            await Respond(command, null, "NO [AUTHENTICATIONFAILED] Invalid credentials", cancellationToken);
            return;
        }

        await CompleteLoginAsync(command, parts[1], parts[2], cancellationToken);
    }

    private async Task CompleteLoginAsync(ImapCommand command, string user, string password, CancellationToken cancellationToken)
    {
        if (!LoginAllowed)
        {
            await Respond(command, null, "NO [PRIVACYREQUIRED] Use STARTTLS first", cancellationToken);
            return;
        }

        if (throttle.IsLockedOut(remoteAddress))
        {
            logger.LogWarning("IMAP login for {User} from locked-out address {Ip} rejected", user, remoteAddress);
            await Respond(command, null, "NO [UNAVAILABLE] Too many failed logins, try again later", cancellationToken);
            return;
        }

        var account = accounts.Authenticate(user, password);
        if (account is null)
        {
            throttle.RecordFailure(remoteAddress);
            logger.LogWarning("Failed IMAP login for {User} from {Ip}", user, remoteAddress);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            await Respond(command, null, "NO [AUTHENTICATIONFAILED] Invalid credentials", cancellationToken);
            return;
        }

        throttle.RecordSuccess(remoteAddress);
        _account = account;
        mailboxes.EnsureDefaultFolders(account.Id);
        connection.MaxLiteralSize = options.MaxMessageSizeBytes;
        logger.LogInformation("IMAP login {User} from {Ip}", account.Address, remoteAddress);
        await Respond(command, null, $"OK [CAPABILITY {Capabilities}] Logged in", cancellationToken);
    }

    // ---- IDLE ----

    private async Task IdleAsync(ImapCommand command, CancellationToken cancellationToken)
    {
        await connection.WriteAsync("+ idling\r\n", cancellationToken);
        var readDone = connection.ReadLineAsync(cancellationToken);
        var timeout = Task.Delay(options.Imap.IdleTimeout, cancellationToken);

        while (true)
        {
            var changed = _selected is null ? Task.Delay(Timeout.Infinite, cancellationToken) : watcher.WaitAsync(_selected.Folder.Id);
            if (_selected is not null)
            {
                var updates = new ImapResponse();
                _selected.Sync(updates, allowExpunge: true);
                if (!updates.IsEmpty)
                {
                    await connection.WriteAsync(updates.ToMemory(), cancellationToken);
                }
            }

            var completed = await Task.WhenAny(readDone, changed, timeout);
            if (completed == readDone)
            {
                var line = await readDone;
                if (line is null)
                {
                    _logout = true;
                    return;
                }

                await Respond(command, null, line.Trim().Equals("DONE", StringComparison.OrdinalIgnoreCase)
                    ? "OK IDLE terminated"
                    : "BAD Expected DONE", cancellationToken);
                return;
            }

            if (completed == timeout)
            {
                await connection.WriteAsync("* BYE Autologout; idle for too long\r\n", cancellationToken);
                _logout = true;
                return;
            }
        }
    }

    // ---- Helpers ----

    private async Task Respond(ImapCommand command, string? untagged, string status, CancellationToken cancellationToken)
    {
        var response = new ImapResponse();
        if (untagged is not null)
        {
            response.Raw(untagged);
        }

        response.Raw($"{command.Tag} {status}\r\n");
        await connection.WriteAsync(response.ToMemory(), cancellationToken);
    }

    private static string DecodeFolderName(ImapToken token)
    {
        var name = token.AsString();
        try
        {
            name = ModifiedUtf7.Decode(name);
        }
        catch (FormatException)
        {
            throw new ImapParseException("Invalid folder name encoding");
        }

        return MailboxStore.NormalizeFolderName(name.TrimEnd(MailboxStore.HierarchyDelimiter));
    }
}
