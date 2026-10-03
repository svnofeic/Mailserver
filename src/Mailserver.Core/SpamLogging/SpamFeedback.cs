using System.Globalization;
using Mailserver.Core.Accounts;
using Mailserver.Core.Storage;
using MimeKit;

namespace Mailserver.Core.SpamLogging;

/// <summary>
/// Moving a message into or out of Junk – over IMAP or in webmail – is the user's verdict on the spam filter. It is logged with the
/// score the message received, so false positives and false negatives can be analysed later.
/// </summary>
public sealed class SpamFeedback(MailboxStore mailboxes, SpamLog spamLog)
{
    public const string JunkFolder = "Junk";

    public void Record(Account account, Folder source, IEnumerable<StoredMessage> messages, Folder target)
    {
        var intoJunk = target.Name == JunkFolder && source.Name != JunkFolder;
        var outOfJunk = source.Name == JunkFolder && target.Name is not (JunkFolder or "Trash");
        if (!spamLog.Enabled || !(intoJunk || outOfJunk))
        {
            return;
        }

        foreach (var message in messages)
        {
            HeaderList headers;
            try
            {
                using var stream = File.OpenRead(mailboxes.GetMessagePath(message));
                headers = HeaderList.Load(stream);
            }
            catch (Exception ex) when (ex is IOException or FormatException)
            {
                continue;
            }

            var status = headers["X-Spam-Status"];
            var testsIndex = status?.IndexOf("tests=", StringComparison.Ordinal) ?? -1;
            spamLog.Write(new SpamLogEntry
            {
                Stage = SpamLogStage.Feedback,
                Action = intoJunk ? SpamLogAction.MarkedSpam : SpamLogAction.MarkedHam,
                Recipient = account.Address.ToString(),
                HeaderFrom = headers[HeaderId.From],
                Subject = headers[HeaderId.Subject],
                MessageId = MimeKit.Utils.MimeUtils.EnumerateReferences(headers[HeaderId.MessageId] ?? "").FirstOrDefault(),
                Score = double.TryParse(headers["X-Spam-Score"], NumberStyles.Float, CultureInfo.InvariantCulture, out var score) ? score : null,
                Tests = testsIndex < 0 ? null : string.Concat(status![(testsIndex + 6)..].Where(c => !char.IsWhiteSpace(c))),
                Folder = target.Name,
                Detail = $"{source.Name} -> {target.Name}",
            });
        }
    }
}
