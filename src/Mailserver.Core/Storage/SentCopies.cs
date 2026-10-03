using System.Globalization;
using Mailserver.Core.Accounts;
using Mailserver.Core.Data;

namespace Mailserver.Core.Storage;

/// <summary>
/// Copies of messages sent through SMTP submission in the sender's "Sent" folder, as Gmail and others do it, so mail
/// programs that do not store sent mail themselves (or store it only on the device) still leave a copy on the server.
/// Programs that do store a copy via IMAP would cause duplicates; when such a copy with the same Message-ID arrives
/// within <see cref="Window"/>, the server copy is removed and the program's copy (which keeps Bcc) remains.
/// </summary>
public sealed class SentCopies(Database database, MailboxStore mailboxes, TimeProvider timeProvider)
{
    public const string SentFolder = "Sent";
    public static readonly TimeSpan Window = TimeSpan.FromDays(1);

    public async Task SaveAsync(Account account, byte[] message, CancellationToken cancellationToken = default)
    {
        var stored = await mailboxes.AppendAsync(account, message, SentFolder, MessageFlags.Seen, cancellationToken: cancellationToken);
        if (ReadMessageId(message) is not { } messageId)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        using var connection = database.Open();
        connection.Execute("DELETE FROM sent_copies WHERE created_utc < $cutoff", ("$cutoff", Format(now - Window)));
        connection.Execute(
            "INSERT OR REPLACE INTO sent_copies (account_id, message_id, folder_id, uid, created_utc) VALUES ($account, $id, $folder, $uid, $now)",
            ("$account", account.Id), ("$id", messageId), ("$folder", stored.FolderId), ("$uid", stored.Uid), ("$now", Format(now)));
    }

    /// <summary>
    /// Called when the mail program stores a message itself (IMAP APPEND). Removes the server copy of the same message,
    /// unless it is a draft. Returns true if a copy was removed.
    /// </summary>
    public bool RemoveServerCopy(long accountId, ReadOnlySpan<byte> appended, string flags)
    {
        if (MessageFlags.Parse(flags).Contains(MessageFlags.Draft, StringComparer.OrdinalIgnoreCase) || ReadMessageId(appended) is not { } messageId)
        {
            return false;
        }

        long folderId, uid;
        using (var connection = database.Open())
        {
            var row = connection.Query("SELECT folder_id, uid FROM sent_copies WHERE account_id = $account AND message_id = $id AND created_utc >= $cutoff",
                r => (Folder: r.GetInt64(0), Uid: r.GetInt64(1)),
                ("$account", accountId), ("$id", messageId), ("$cutoff", Format(timeProvider.GetUtcNow() - Window))).FirstOrDefault();
            connection.Execute("DELETE FROM sent_copies WHERE account_id = $account AND message_id = $id", ("$account", accountId), ("$id", messageId));
            if (row == default)
            {
                return false;
            }

            (folderId, uid) = row;
        }

        // Moved or deleted by the user in the meantime: nothing to do.
        mailboxes.UpdateFlags(folderId, [uid], FlagOperation.Add, [MessageFlags.Deleted]);
        return mailboxes.Expunge(folderId, [uid]).Count > 0;
    }

    /// <summary>The Message-ID from the header block (without angle brackets), or null.</summary>
    public static string? ReadMessageId(ReadOnlySpan<byte> message)
    {
        // Header lines are ASCII; only the header block (up to the first empty line) is read.
        var end = message.IndexOf("\r\n\r\n"u8);
        if (end < 0) end = message.IndexOf("\n\n"u8);
        var header = System.Text.Encoding.Latin1.GetString(end < 0 ? message : message[..end]).Replace("\r\n", "\n");
        foreach (var line in Unfold(header))
        {
            if (line.StartsWith("Message-ID:", StringComparison.OrdinalIgnoreCase))
            {
                var value = line["Message-ID:".Length..].Trim();
                var open = value.IndexOf('<');
                var close = value.IndexOf('>', open + 1);
                value = open >= 0 && close > open ? value[(open + 1)..close] : value;
                return value.Length is > 0 and <= 500 ? value : null;
            }
        }

        return null;
    }

    private static IEnumerable<string> Unfold(string header)
    {
        string? current = null;
        foreach (var line in header.Split('\n'))
        {
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t') && current is not null)
            {
                current += " " + line.Trim();
                continue;
            }

            if (current is not null)
            {
                yield return current;
            }

            current = line;
        }

        if (current is not null)
        {
            yield return current;
        }
    }

    private static string Format(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
