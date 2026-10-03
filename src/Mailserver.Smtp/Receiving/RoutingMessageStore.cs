using System.Buffers;
using System.Text;
using Mailserver.AntiSpam;
using Mailserver.Core;
using Mailserver.Core.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit.Utils;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Protocol;
using SmtpServer.Storage;

namespace Mailserver.Smtp.Receiving;

/// <summary>
/// Receives the DATA of an accepted transaction, adds a Received header and hands it to the router.
/// </summary>
internal sealed class RoutingMessageStore(
    MessageRouter router,
    OutgoingMessagePreparer preparer,
    SpamFilter spamFilter,
    Mailserver.Core.SpamLogging.SpamLog spamLog,
    Mailserver.Core.Accounts.AccountStore accounts,
    Mailserver.Core.Storage.SentCopies sentCopies,
    IOptions<MailserverOptions> options,
    ILogger<RoutingMessageStore> logger,
    bool isSubmission) : IMessageStore
{
    public async Task<SmtpResponse> SaveAsync(ISessionContext context, IMessageTransaction transaction, ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = buffer.ToArray();
            if (CountReceivedHeaders(body) >= options.Value.Security.MaxHopCount)
            {
                return new SmtpResponse(SmtpReplyCode.TransactionFailed, "5.4.6 Too many hops, possible mail loop");
            }

            var recipients = transaction.To.Select(m => EmailAddress.Parse(m.AsAddress())).ToList();
            var sender = string.IsNullOrEmpty(transaction.From?.User) ? "" : transaction.From.AsAddress();
            var relay = !isSubmission && options.Value.Smtp.IsRelayClient(SessionInfo.GetRemoteAddress(context));
            if (isSubmission || relay)
            {
                var submitted = await preparer.PrepareAsync(Concat(BuildReceivedHeader(context, recipients), body), cancellationToken);
                await router.RouteAsync(submitted, sender, recipients, allowRelay: true, cancellationToken);
                LogSubmission(context, sender, recipients, submitted,
                    relay ? "Relay ohne Anmeldung (Smtp:RelayNetworks)" : $"angemeldet als {context.Authentication.User}");
                if (!relay && options.Value.Smtp.SaveSentCopies)
                {
                    await SaveSentCopyAsync(context, submitted, cancellationToken);
                }
                return SmtpResponse.Ok;
            }

            // DKIM is verified on the message exactly as received, before headers are added or removed.
            var session = SessionInfo.GetInbound(context);
            var result = await spamFilter.CheckMessageAsync(session, body, recipients.Select(r => r.ToString()).ToList(), cancellationToken);
            if (result.Rejection is not null)
            {
                return new SmtpResponse(SmtpReplyCode.MailboxUnavailable, result.Rejection);
            }

            var cleaned = session.Trusted ? body : HeaderEditor.RemoveFields(body, spamFilter.IsSpoofableHeader);
            var message = Concat(Concat(BuildReceivedHeader(context, recipients), Encoding.ASCII.GetBytes(result.Headers)), cleaned);
            await router.RouteAsync(message, sender, recipients, allowRelay: false, cancellationToken, result.Verdict);
            return SmtpResponse.Ok;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to store message in session {Session}", context.SessionId);
            return new SmtpResponse(SmtpReplyCode.Aborted, "4.3.0 Temporary server error, please try again later");
        }
    }

    /// <summary>Copy in the sender's Sent folder. The message is already on its way, so a failure here must not fail the send.</summary>
    private async Task SaveSentCopyAsync(ISessionContext context, byte[] message, CancellationToken cancellationToken)
    {
        try
        {
            if (EmailAddress.TryParse(context.Authentication.User, out var address) && accounts.FindAccount(address) is { } account)
            {
                await sentCopies.SaveAsync(account, message, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not store the sent copy for {User}", context.Authentication.User);
        }
    }

    private void LogSubmission(ISessionContext context, string sender, IReadOnlyList<EmailAddress> recipients, byte[] message, string detail)
    {
        MimeKit.HeaderList headers;
        using (var stream = new MemoryStream(message, writable: false))
        {
            headers = MimeKit.HeaderList.Load(stream);
        }

        spamLog.Write(new Mailserver.Core.SpamLogging.SpamLogEntry
        {
            Session = context.SessionId.ToString("N"),
            Stage = Mailserver.Core.SpamLogging.SpamLogStage.Submission,
            Action = Mailserver.Core.SpamLogging.SpamLogAction.Accepted,
            ClientIp = SessionInfo.GetRemoteAddress(context)?.ToString(),
            MailFrom = sender,
            Recipient = string.Join(", ", recipients),
            HeaderFrom = headers[MimeKit.HeaderId.From],
            Subject = headers[MimeKit.HeaderId.Subject],
            MessageId = MimeKit.Utils.MimeUtils.EnumerateReferences(headers[MimeKit.HeaderId.MessageId] ?? "").FirstOrDefault(),
            Detail = detail,
        });
    }

    private byte[] BuildReceivedHeader(ISessionContext context, IReadOnlyList<EmailAddress> recipients)
    {
        var protocol = "ESMTP" + (context.Pipe.IsSecure ? "S" : "") + (context.Authentication.IsAuthenticated ? "A" : "");
        var helo = SanitizeHeaderValue(SessionInfo.GetHelo(context) ?? "unknown");
        var forClause = recipients.Count == 1 ? $"\r\n\tfor <{recipients[0]}>" : "";
        var header =
            $"Received: from {helo} ([{SessionInfo.GetRemoteAddress(context)}])\r\n" +
            $"\tby {options.Value.Hostname} with {protocol} id {context.SessionId:N}{forClause};\r\n" +
            $"\t{DateUtils.FormatDate(DateTimeOffset.Now)}\r\n";
        return Encoding.ASCII.GetBytes(header);
    }

    private static string SanitizeHeaderValue(string value) =>
        new(value.Where(c => c > 32 && c < 127 && c != '(' && c != ')').Take(255).ToArray());

    private static byte[] Concat(byte[] first, byte[] second)
    {
        var result = new byte[first.Length + second.Length];
        first.CopyTo(result, 0);
        second.CopyTo(result, first.Length);
        return result;
    }

    private static int CountReceivedHeaders(byte[] message)
    {
        // Only the header block is scanned; it ends at the first empty line.
        var count = 0;
        var lineStart = 0;
        for (var i = 0; i < message.Length; i++)
        {
            if (message[i] != '\n')
            {
                continue;
            }

            var lineLength = i - lineStart;
            if (lineLength == 0 || (lineLength == 1 && message[lineStart] == '\r'))
            {
                break;
            }

            if (lineLength >= 9 && Encoding.ASCII.GetString(message, lineStart, 9).Equals("Received:", StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }

            lineStart = i + 1;
        }

        return count;
    }
}
