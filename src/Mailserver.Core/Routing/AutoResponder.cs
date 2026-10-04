using Mailserver.Core.Accounts;
using MimeKit;

namespace Mailserver.Core.Routing;

/// <summary>
/// Out-of-office replies following RFC 3834: only to real people who wrote to the mailbox directly, never to mailing
/// lists, newsletters, bounces or other automatic mail, and at most once per sender and interval. Replies go to the
/// envelope sender with an empty envelope sender of their own, so they can never cause a loop.
/// </summary>
public sealed class AutoResponder(AccountStore accounts, MailboxSettingsStore settings, TimeProvider timeProvider)
{
    private static readonly string[] IgnoredLocalParts =
        ["mailer-daemon", "postmaster", "noreply", "no-reply", "no_reply", "donotreply", "do-not-reply", "do_not_reply", "listserv", "majordomo"];

    private static readonly string[] ListHeaders = ["List-Id", "List-Unsubscribe", "List-Post", "List-Help", "Mailing-List"];

    /// <summary>The reply to send (unprepared, without Message-ID/DKIM) and its recipient, or null if none is due.</summary>
    public (MimeMessage Reply, EmailAddress To)? CreateReply(Account account, AutoReply reply, MimeMessage original, string envelopeSender)
    {
        if (!reply.IsActiveOn(DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime)) ||
            !EmailAddress.TryParse(envelopeSender, out var sender) || sender == account.Address ||
            IsAutomaticSender(sender) || IsAutomaticMessage(original) || !IsAddressedTo(account, original) ||
            !settings.TryRecordReply(account.Id, sender, reply.IntervalDays))
        {
            return null;
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(original.To.Mailboxes.Concat(original.Cc.Mailboxes)
            .FirstOrDefault(m => EmailAddress.TryParse(m.Address, out var a) && a == account.Address)?.Name, account.Address.ToString()));
        message.To.Add(new MailboxAddress(null, sender.ToString()));
        message.Subject = reply.Subject.Length > 0 ? reply.Subject : $"Automatische Antwort: {original.Subject}".TrimEnd(' ', ':');
        message.Headers.Add("Auto-Submitted", "auto-replied");
        message.Headers.Add("X-Auto-Response-Suppress", "All");
        if (!string.IsNullOrEmpty(original.MessageId))
        {
            message.InReplyTo = original.MessageId;
            message.References.AddRange(original.References);
            message.References.Add(original.MessageId);
        }

        message.Body = new TextPart("plain") { Text = reply.Body };
        return (message, sender);
    }

    private static bool IsAutomaticSender(EmailAddress sender)
    {
        var local = sender.LocalPart.ToLowerInvariant();
        return IgnoredLocalParts.Contains(local) || local.StartsWith("owner-", StringComparison.Ordinal) ||
               local.EndsWith("-request", StringComparison.Ordinal) || local.StartsWith("bounce", StringComparison.Ordinal);
    }

    private static bool IsAutomaticMessage(MimeMessage message)
    {
        var headers = message.Headers;
        if (headers["Auto-Submitted"] is { } autoSubmitted && !autoSubmitted.Trim().Equals("no", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (headers["Precedence"]?.Trim().ToLowerInvariant() is "bulk" or "list" or "junk")
        {
            return true;
        }

        if (headers["X-Auto-Response-Suppress"] is { } suppress &&
            (suppress.Contains("All", StringComparison.OrdinalIgnoreCase) || suppress.Contains("OOF", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return ListHeaders.Any(h => headers.Contains(h)) || headers.Contains("X-Autoreply") || headers.Contains("X-Autorespond");
    }

    /// <summary>The mailbox (or an alias of it) is in To or Cc — not only in Bcc, as with lists and mass mailings.</summary>
    private bool IsAddressedTo(Account account, MimeMessage message)
    {
        foreach (var mailbox in message.To.Mailboxes.Concat(message.Cc.Mailboxes))
        {
            if (!EmailAddress.TryParse(mailbox.Address, out var address))
            {
                continue;
            }

            if (address == account.Address ||
                (accounts.IsLocalDomain(address.Domain) && accounts.Resolve(address).LocalAccounts.Any(a => a.Id == account.Id)))
            {
                return true;
            }
        }

        return false;
    }
}
