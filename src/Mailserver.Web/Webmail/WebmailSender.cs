using System.Net;
using System.Text;
using Mailserver.Core;
using Mailserver.Core.Accounts;
using Mailserver.Core.Routing;
using Mailserver.Core.SpamLogging;
using Mailserver.Core.Storage;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Utils;

namespace Mailserver.Web.Webmail;

public sealed class OutgoingAttachment(string fileName, string contentType, byte[] content)
{
    public string FileName { get; } = fileName;
    public string ContentType { get; } = contentType;
    public byte[] Content { get; } = content;
}

/// <summary>A message written in webmail.</summary>
public sealed record Draft(
    string From,
    string To,
    string? Cc,
    string? Bcc,
    string Subject,
    string Body,
    IReadOnlyList<OutgoingAttachment> Attachments,
    string? InReplyTo = null,
    IReadOnlyList<string>? References = null);

public sealed class ComposeException(string message) : Exception(message);

/// <summary>
/// Sends webmail messages through the same path as SMTP submission: sender check, DKIM signing, local delivery or queue,
/// copy in Sent, history entry.
/// </summary>
public sealed class WebmailSender(
    AccountStore accounts,
    MailboxStore mailboxes,
    OutgoingMessagePreparer preparer,
    MessageRouter router,
    SpamLog spamLog,
    IOptions<MailserverOptions> options)
{
    public const int MaxRecipients = 100;

    /// <summary>Addresses the account may send as: its own and aliases that deliver to it.</summary>
    public IReadOnlyList<string> SenderAddresses(Account account) =>
        new[] { account.Address.ToString() }
            .Concat(accounts.ListAliases().Where(a => accounts.MaySendAs(account, a.Address)).Select(a => a.Address.ToString()))
            .ToList();

    public async Task SendAsync(Account account, Draft draft, IPAddress? clientIp, CancellationToken cancellationToken)
    {
        var (message, recipients) = Build(account, draft, requireRecipients: true);
        var envelopeSender = message.From.Mailboxes.Single().Address;

        // Bcc must not appear in the transmitted message.
        message.Bcc.Clear();
        var received = Encoding.ASCII.GetBytes(
            $"Received: from webmail ([{clientIp}])\r\n\tby {options.Value.Hostname} with HTTPS (webmail) for {account.Address};\r\n\t{DateUtils.FormatDate(DateTimeOffset.Now)}\r\n");
        using var raw = new MemoryStream();
        raw.Write(received);
        await message.WriteToAsync(raw, cancellationToken);
        var prepared = await preparer.PrepareAsync(raw.ToArray(), cancellationToken);

        await router.RouteAsync(prepared, envelopeSender, recipients, allowRelay: true, cancellationToken);
        await mailboxes.AppendAsync(account, prepared, "Sent", $"{MessageFlags.Seen}", cancellationToken: cancellationToken);

        spamLog.Write(new SpamLogEntry
        {
            Stage = SpamLogStage.Submission,
            Action = SpamLogAction.Accepted,
            ClientIp = clientIp?.ToString(),
            MailFrom = envelopeSender,
            Recipient = string.Join(", ", recipients),
            HeaderFrom = envelopeSender,
            Subject = message.Subject,
            MessageId = message.MessageId,
            Detail = $"Webmail, angemeldet als {account.Address}",
        });
    }

    public async Task<StoredMessage> SaveDraftAsync(Account account, Draft draft, CancellationToken cancellationToken)
    {
        var (message, _) = Build(account, draft, requireRecipients: false);
        using var raw = new MemoryStream();
        await message.WriteToAsync(raw, cancellationToken);
        return await mailboxes.AppendAsync(account, raw.ToArray(), "Drafts", $"{MessageFlags.Seen} {MessageFlags.Draft}", cancellationToken: cancellationToken);
    }

    private (MimeMessage Message, List<EmailAddress> Recipients) Build(Account account, Draft draft, bool requireRecipients)
    {
        if (!EmailAddress.TryParse(draft.From, out var from) || !accounts.MaySendAs(account, from))
        {
            throw new ComposeException("Diese Absenderadresse darf nicht verwendet werden.");
        }

        var message = new MimeMessage
        {
            Subject = draft.Subject.Trim(),
            Date = DateTimeOffset.Now,
            MessageId = MimeUtils.GenerateMessageId(options.Value.Hostname),
        };
        message.From.Add(new MailboxAddress(null, from.ToString()));
        AddAddresses(message.To, draft.To, "An");
        AddAddresses(message.Cc, draft.Cc, "Cc");
        AddAddresses(message.Bcc, draft.Bcc, "Bcc");

        if (draft.InReplyTo is { Length: > 0 } inReplyTo)
        {
            message.InReplyTo = inReplyTo;
            foreach (var reference in draft.References ?? [])
            {
                message.References.Add(reference);
            }
        }

        var recipients = message.To.Mailboxes.Concat(message.Cc.Mailboxes).Concat(message.Bcc.Mailboxes)
            .Select(m => EmailAddress.Parse(m.Address)).Distinct().ToList();
        if (requireRecipients && recipients.Count == 0)
        {
            throw new ComposeException("Bitte mindestens einen Empfänger angeben.");
        }

        if (recipients.Count > MaxRecipients)
        {
            throw new ComposeException($"Höchstens {MaxRecipients} Empfänger pro Nachricht.");
        }

        var builder = new BodyBuilder { TextBody = draft.Body.Replace("\r\n", "\n").Replace("\n", "\r\n") };
        foreach (var attachment in draft.Attachments)
        {
            builder.Attachments.Add(attachment.FileName, attachment.Content, ContentType.Parse(attachment.ContentType));
        }

        message.Body = builder.ToMessageBody();
        var size = draft.Attachments.Sum(a => (long)a.Content.Length) + draft.Body.Length;
        if (size > options.Value.MaxMessageSizeBytes * 3 / 4)
        {
            throw new ComposeException($"Die Nachricht ist zu groß (höchstens {Format.Size(options.Value.MaxMessageSizeBytes * 3 / 4)} inkl. Anhängen).");
        }

        return (message, recipients);
    }

    private static void AddAddresses(InternetAddressList list, string? text, string field)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // Accept ";" as separator too (Outlook habit).
        if (!InternetAddressList.TryParse(text.Replace(';', ','), out var parsed) ||
            parsed.Mailboxes.Any(m => !EmailAddress.TryParse(m.Address, out _)))
        {
            throw new ComposeException($"Ungültige Adresse im Feld „{field}“.");
        }

        list.AddRange(parsed.Mailboxes);
    }
}
