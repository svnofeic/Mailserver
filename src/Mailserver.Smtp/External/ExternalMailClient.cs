using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using Mailserver.Core;
using Mailserver.Core.Accounts;
using Mailserver.Core.Antivirus;
using Mailserver.Core.External;
using Mailserver.Core.Push;
using Mailserver.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Utils;
using StoredFlags = Mailserver.Core.Storage.MessageFlags;

namespace Mailserver.Smtp.External;

/// <summary>
/// Fetches mail of addresses at other providers by IMAP into a folder of the mailbox, and sends through their SMTP server.
/// The provider keeps its copy; only messages that arrived after the last fetch (by UID) are taken.
/// </summary>
public sealed class ExternalMailClient(
    ExternalAccountStore store,
    AccountStore accounts,
    MailboxStore mailboxes,
    MalwareFilter malware,
    IOptions<MailserverOptions> options,
    ILogger<ExternalMailClient> logger,
    PushNotifier? push = null) : IExternalMail
{
    /// <summary>Messages per run; a full inbox taken over with "fetch existing mail" arrives over several runs.</summary>
    public const int MaxPerRun = 200;

    private const int TimeoutMilliseconds = 60_000;
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _running = new();

    /// <summary>Only for tests against servers with a self-signed certificate.</summary>
    public RemoteCertificateValidationCallback? CertificateValidation { get; set; }

    public async Task<string?> TestAsync(ExternalAccountSettings settings, string password, CancellationToken cancellationToken)
    {
        try
        {
            using var imap = new ImapClient { Timeout = TimeoutMilliseconds, ServerCertificateValidationCallback = CertificateValidation };
            await imap.ConnectAsync(settings.Imap.Host, settings.Imap.Port, Socket(settings.Imap.Security), cancellationToken);
            await imap.AuthenticateAsync(settings.UserName, password, cancellationToken);
            await imap.DisconnectAsync(true, cancellationToken);
        }
        catch (Exception ex) when (Expected(ex, cancellationToken))
        {
            return $"IMAP ({settings.Imap}): {Describe(ex)}";
        }

        if (settings.Smtp is { } smtpServer)
        {
            try
            {
                using var smtp = await ConnectSmtpAsync(smtpServer, settings.UserName, password, cancellationToken);
                await smtp.DisconnectAsync(true, cancellationToken);
            }
            catch (Exception ex) when (Expected(ex, cancellationToken))
            {
                return $"SMTP ({smtpServer}): {Describe(ex)}";
            }
        }

        return null;
    }

    public async Task SendAsync(ExternalAccount account, byte[] message, IReadOnlyList<EmailAddress> recipients, CancellationToken cancellationToken)
    {
        var server = account.Settings.Smtp ?? throw new ExternalMailException($"Für {account.Address} ist kein Postausgangsserver eingetragen.");
        var password = store.Password(account.Id)
                       ?? throw new ExternalMailException($"Das Passwort für {account.Address} ist nicht mehr lesbar – bitte unter Fremde Konten neu eingeben.");
        try
        {
            using var stream = new MemoryStream(message, writable: false);
            var mime = await MimeMessage.LoadAsync(stream, cancellationToken);
            using var smtp = await ConnectSmtpAsync(server, account.Settings.UserName, password, cancellationToken);
            await smtp.SendAsync(mime, MailboxAddress.Parse(account.Address), recipients.Select(r => new MailboxAddress(null, r.ToString())),
                cancellationToken);
            await smtp.DisconnectAsync(true, cancellationToken);
        }
        catch (Exception ex) when (Expected(ex, cancellationToken))
        {
            logger.LogWarning("Sending as {Address} through {Server} failed: {Error}", account.Address, server, ex.Message);
            throw new ExternalMailException($"Versand über {server.Host} fehlgeschlagen: {Describe(ex)}");
        }
    }

    public async Task<FetchResult> FetchAsync(ExternalAccount account, CancellationToken cancellationToken)
    {
        var gate = _running.GetOrAdd(account.Id, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken))
        {
            return new FetchResult(0, null); // the background fetch is just at it
        }

        var fetched = 0;
        try
        {
            // The caller's copy may be older than the last run (e.g. a page loaded a while ago).
            account = store.Find(account.Id) ?? throw new ExternalMailException("Dieses Konto gibt es nicht mehr.");
            var owner = accounts.FindAccount(account.AccountId) ?? throw new ExternalMailException("Das Postfach gibt es nicht mehr.");
            var password = store.Password(account.Id)
                           ?? throw new ExternalMailException("Das gespeicherte Passwort ist nicht mehr lesbar – bitte neu eingeben.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            fetched = await FetchCoreAsync(account, owner, password, n => fetched = n, timeout.Token);
            store.RecordAttempt(account.Id, null);
            if (fetched > 0)
            {
                logger.LogInformation("Fetched {Count} messages for {Owner} from {Address}", fetched, owner.Address, account.Address);
            }

            return new FetchResult(fetched, null);
        }
        catch (Exception ex) when (Expected(ex, cancellationToken))
        {
            var error = Describe(ex);
            logger.LogWarning("Fetching {Address} failed: {Error}", account.Address, ex.Message);
            store.RecordAttempt(account.Id, error);
            return new FetchResult(fetched, error);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<int> FetchCoreAsync(ExternalAccount account, Account owner, string password, Action<int> progress,
        CancellationToken cancellationToken)
    {
        var server = account.Settings.Imap;
        using var client = new ImapClient { Timeout = TimeoutMilliseconds, ServerCertificateValidationCallback = CertificateValidation };
        await client.ConnectAsync(server.Host, server.Port, Socket(server.Security), cancellationToken);
        await client.AuthenticateAsync(account.Settings.UserName, password, cancellationToken);
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
        long validity = inbox.UidValidity;

        if (account.LastUid is null || (account.UidValidity is { } known && known != validity))
        {
            // First run (or the provider renumbered its inbox): start after what is there now.
            if (account.UidValidity is not null)
            {
                logger.LogWarning("The inbox of {Address} was renumbered by the provider; fetching continues with new mail only", account.Address);
            }

            store.SavePosition(account.Id, validity, await HighestUidAsync(inbox, cancellationToken), 0);
            await client.DisconnectAsync(true, cancellationToken);
            return 0;
        }

        var last = account.LastUid.Value;
        // "n:*" also returns the newest message when n is beyond it, hence the filter.
        var uids = (await inbox.SearchAsync(SearchQuery.Uids(new UniqueIdRange(new UniqueId((uint)Math.Min(last + 1, uint.MaxValue)), UniqueId.MaxValue)),
                cancellationToken))
            .Where(u => u.Id > last).OrderBy(u => u.Id).Take(MaxPerRun).ToList();
        if (uids.Count == 0)
        {
            await client.DisconnectAsync(true, cancellationToken);
            return 0;
        }

        var summaries = await inbox.FetchAsync(uids,
            MessageSummaryItems.UniqueId | MessageSummaryItems.InternalDate | MessageSummaryItems.Flags | MessageSummaryItems.Size, cancellationToken);
        var target = mailboxes.GetOrCreateFolder(owner.Id, account.Settings.Folder);
        var count = 0;
        foreach (var summary in summaries.OrderBy(s => s.UniqueId.Id))
        {
            var uid = summary.UniqueId.Id;
            var size = (long)(summary.Size ?? 0);
            if (size > options.Value.MaxMessageSizeBytes)
            {
                logger.LogWarning("Skipped message {Uid} of {Address}: {Size} bytes is above the size limit", uid, account.Address, size);
                store.SavePosition(account.Id, validity, uid, 0);
                continue;
            }

            if (mailboxes.IsOverQuota(owner, size))
            {
                throw new ExternalMailException("Das Postfach ist voll – neue Mails werden abgerufen, sobald wieder Platz ist.");
            }

            byte[] raw;
            await using (var stream = await inbox.GetStreamAsync(summary.UniqueId, "", cancellationToken))
            using (var buffer = new MemoryStream())
            {
                await stream.CopyToAsync(buffer, cancellationToken);
                raw = buffer.ToArray();
            }

            var verdict = await malware.CheckAsync(raw, incoming: true, cancellationToken);
            if (verdict.Action == MalwareAction.Defer)
            {
                throw new ExternalMailException("Der Virenscan ist gerade nicht verfügbar – der nächste Abruf versucht es erneut.");
            }

            if (verdict.Action == MalwareAction.Reject)
            {
                // The provider keeps its copy; it is just not taken over.
                logger.LogWarning("Message {Uid} of {Address} not taken over: {Reason}", uid, account.Address, verdict.Reason);
                store.SavePosition(account.Id, validity, uid, 0);
                continue;
            }

            var received = Encoding.ASCII.GetBytes(
                $"Received: from {server.Host} by {options.Value.Hostname} with IMAP id {uid}\r\n\tfor <{account.Address}>; {DateUtils.FormatDate(DateTimeOffset.Now)}\r\n");
            byte[] message = [.. received, .. Encoding.ASCII.GetBytes(verdict.Header), .. raw];
            var seen = summary.Flags?.HasFlag(MailKit.MessageFlags.Seen) == true;
            var folder = verdict.Action == MalwareAction.Junk ? mailboxes.GetOrCreateFolder(owner.Id, "Junk") : target;
            var stored = await mailboxes.AppendAsync(folder, message, seen ? StoredFlags.Seen : "", summary.InternalDate, cancellationToken);
            store.SavePosition(account.Id, validity, uid, 1);
            progress(++count);
            if (!seen && folder.Id == target.Id)
            {
                push?.NewMail(owner, stored.Uid, message, target.Name);
            }
        }

        await client.DisconnectAsync(true, cancellationToken);
        return count;
    }

    private static async Task<long> HighestUidAsync(IMailFolder inbox, CancellationToken cancellationToken)
    {
        if (inbox.Count == 0)
        {
            return inbox.UidNext is { } next ? next.Id - 1 : 0;
        }

        var newest = await inbox.FetchAsync(inbox.Count - 1, inbox.Count - 1, MessageSummaryItems.UniqueId, cancellationToken);
        return newest.Count > 0 ? newest[0].UniqueId.Id : 0;
    }

    private async Task<SmtpClient> ConnectSmtpAsync(MailServerAddress server, string userName, string password, CancellationToken cancellationToken)
    {
        var smtp = new SmtpClient { Timeout = TimeoutMilliseconds, ServerCertificateValidationCallback = CertificateValidation };
        try
        {
            await smtp.ConnectAsync(server.Host, server.Port, Socket(server.Security), cancellationToken);
            await smtp.AuthenticateAsync(userName, password, cancellationToken);
            return smtp;
        }
        catch
        {
            smtp.Dispose();
            throw;
        }
    }

    private static SecureSocketOptions Socket(MailSecurity security) => security switch
    {
        MailSecurity.Ssl => SecureSocketOptions.SslOnConnect,
        MailSecurity.StartTls => SecureSocketOptions.StartTls,
        _ => SecureSocketOptions.None,
    };

    private static bool Expected(Exception ex, CancellationToken cancellationToken) =>
        ex is OperationCanceledException ? !cancellationToken.IsCancellationRequested : ex is
            ExternalMailException or AuthenticationException or SslHandshakeException or SocketException or IOException or TimeoutException or
            ImapProtocolException or ImapCommandException or SmtpProtocolException or SmtpCommandException or ProtocolException or
            NotSupportedException or FormatException or ServiceNotConnectedException or ServiceNotAuthenticatedException;

    /// <summary>German text for the user: what went wrong and what to check.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        ExternalMailException => ex.Message,
        AuthenticationException => "Anmeldung abgelehnt – Benutzername oder Passwort falsch, oder beim Anbieter ist der Zugriff per IMAP/SMTP " +
                                   "nicht freigeschaltet (manche Anbieter verlangen dafür ein eigenes App-Passwort).",
        SslHandshakeException => "Die verschlüsselte Verbindung kam nicht zustande – passen Port und Verschlüsselung zusammen " +
                                 "(993/465 = SSL/TLS, 143/587 = STARTTLS)?",
        SocketException socket => $"Server nicht erreichbar ({socket.SocketErrorCode}) – stimmen Servername und Port?",
        OperationCanceledException or TimeoutException => "Zeitüberschreitung – der Server antwortet nicht.",
        SmtpCommandException smtp => $"Der Server hat abgelehnt: {smtp.Message.Trim()}",
        ImapCommandException imap => $"Der Server hat abgelehnt: {imap.Message.Trim()}",
        NotSupportedException => "Der Server bietet die gewählte Verschlüsselung oder Anmeldung nicht an.",
        _ => $"Verbindung fehlgeschlagen: {ex.Message.Trim()}",
    };
}
