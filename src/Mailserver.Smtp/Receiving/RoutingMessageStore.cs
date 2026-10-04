using System.Buffers;
using System.Text;
using Mailserver.AntiSpam;
using Mailserver.Core;
using Mailserver.Core.Routing;
using Mailserver.Core.SpamLogging;
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
    Mailserver.Core.Antivirus.MalwareFilter malwareFilter,
    Mailserver.Core.Security.SendingLimiter sendingLimiter,
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
                var account = relay ? null
                    : EmailAddress.TryParse(context.Authentication.User, out var user) ? accounts.FindAccount(user) : null;
                var limit = await sendingLimiter.CheckAsync(account, relay ? SessionInfo.GetRemoteAddress(context) : null, recipients, cancellationToken);
                switch (limit.Decision)
                {
                    case Mailserver.Core.Security.SendingDecision.TooManyRecipients:
                        return new SmtpResponse((SmtpReplyCode)550, $"5.5.3 {Ascii(limit.Reason)}");
                    case Mailserver.Core.Security.SendingDecision.Limited:
                        return new SmtpResponse(SmtpReplyCode.Aborted, $"4.7.1 {Ascii(limit.Reason)}");
                    case Mailserver.Core.Security.SendingDecision.Blocked:
                        return new SmtpResponse((SmtpReplyCode)550, $"5.7.1 {Ascii(limit.Reason)}");
                }

                // Users may send any file type, but no viruses.
                var outgoing = await malwareFilter.CheckAsync(body, incoming: false, cancellationToken);
                if (Refuse(context, sender, recipients, body, outgoing, SpamLogStage.Submission) is { } refused)
                {
                    return refused;
                }

                var submitted = await preparer.PrepareAsync(
                    Concat(Concat(BuildReceivedHeader(context, recipients), Encoding.ASCII.GetBytes(outgoing.Header)), body), cancellationToken);
                await router.RouteAsync(submitted, sender, recipients, allowRelay: true, cancellationToken);
                LogSubmission(context, sender, recipients, submitted,
                    relay ? "Relay ohne Anmeldung (Smtp:RelayNetworks)" : $"angemeldet als {context.Authentication.User}");
                if (!relay && options.Value.Smtp.SaveSentCopies)
                {
                    await SaveSentCopyAsync(context, submitted, cancellationToken);
                }
                return SmtpResponse.Ok;
            }

            // Viruses and dangerous attachments are refused during the SMTP dialogue: the sender learns about it, and no
            // bounce goes to a forged address.
            var malware = await malwareFilter.CheckAsync(body, incoming: true, cancellationToken);
            if (Refuse(context, sender, recipients, body, malware, SpamLogStage.Data) is { } rejected)
            {
                return rejected;
            }

            // DKIM is verified on the message exactly as received, before headers are added or removed.
            var session = SessionInfo.GetInbound(context);
            var result = await spamFilter.CheckMessageAsync(session, body, recipients.Select(r => r.ToString()).ToList(), cancellationToken);
            if (result.Rejection is not null)
            {
                return new SmtpResponse(SmtpReplyCode.MailboxUnavailable, result.Rejection);
            }

            var verdict = result.Verdict;
            if (malware.Action == Mailserver.Core.Antivirus.MalwareAction.Junk)
            {
                // Suspicious attachment (macros, encrypted archive): delivered, but into the spam folder.
                verdict = verdict with
                {
                    IsSpam = true,
                    Tests = [.. verdict.Tests, new SpamTest(malware.Code!, 0)],
                    TraceId = verdict.TraceId ?? context.SessionId.ToString("N"),
                };
                LogMalware(context, sender, recipients, body, malware, SpamLogStage.Data, SpamLogAction.Spam);
            }

            var cleaned = session.Trusted ? body : HeaderEditor.RemoveFields(body, spamFilter.IsSpoofableHeader);
            var message = Concat(Concat(BuildReceivedHeader(context, recipients), Encoding.ASCII.GetBytes(result.Headers + malware.Header)), cleaned);
            await router.RouteAsync(message, sender, recipients, allowRelay: false, cancellationToken, verdict);
            return SmtpResponse.Ok;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to store message in session {Session}", context.SessionId);
            return new SmtpResponse(SmtpReplyCode.Aborted, "4.3.0 Temporary server error, please try again later");
        }
    }

    /// <summary>The SMTP reply for a rejected or deferred message (and its log entry), or null if it may pass.</summary>
    private SmtpResponse? Refuse(ISessionContext context, string sender, IReadOnlyList<EmailAddress> recipients, byte[] body,
        Mailserver.Core.Antivirus.MalwareVerdict verdict, string stage)
    {
        switch (verdict.Action)
        {
            case Mailserver.Core.Antivirus.MalwareAction.Reject:
                LogMalware(context, sender, recipients, body, verdict, stage, SpamLogAction.Rejected);
                return new SmtpResponse((SmtpReplyCode)554, $"5.7.1 Message rejected: {Ascii(verdict.Reason)}");
            case Mailserver.Core.Antivirus.MalwareAction.Defer:
                LogMalware(context, sender, recipients, body, verdict, stage, SpamLogAction.Deferred);
                return new SmtpResponse(SmtpReplyCode.Aborted, "4.7.0 Virus scan temporarily unavailable, please try again later");
            default:
                return null;
        }
    }

    private void LogMalware(ISessionContext context, string sender, IReadOnlyList<EmailAddress> recipients, byte[] body,
        Mailserver.Core.Antivirus.MalwareVerdict verdict, string stage, string action)
    {
        MimeKit.HeaderList headers;
        using (var stream = new MemoryStream(body, writable: false))
        {
            headers = MimeKit.HeaderList.Load(stream);
        }

        spamLog.Write(new Mailserver.Core.SpamLogging.SpamLogEntry
        {
            Session = context.SessionId.ToString("N"),
            Stage = stage,
            Action = action,
            ClientIp = SessionInfo.GetRemoteAddress(context)?.ToString(),
            MailFrom = sender,
            Recipient = string.Join(", ", recipients),
            HeaderFrom = headers[MimeKit.HeaderId.From],
            Subject = headers[MimeKit.HeaderId.Subject],
            MessageId = MimeKit.Utils.MimeUtils.EnumerateReferences(headers[MimeKit.HeaderId.MessageId] ?? "").FirstOrDefault(),
            Tests = verdict.Code,
            Detail = verdict.Reason,
        });
    }

    /// <summary>SMTP replies are ASCII; umlauts and quotes of the German reason are transliterated.</summary>
    private static string Ascii(string? text) => new((text ?? "")
        .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("Ä", "Ae").Replace("Ö", "Oe").Replace("Ü", "Ue").Replace("ß", "ss")
        .Replace('„', '"').Replace('“', '"').Replace('→', '>')
        .Where(c => c is >= ' ' and < (char)127).ToArray());

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
