using System.Text.RegularExpressions;
using Mailserver.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Mailserver.Core.Storage;

/// <summary>One line of the mail list in conversation view: the newest message of the conversation in the folder.</summary>
/// <param name="Uids">All messages of the conversation in this folder (the actions of the list apply to all of them).</param>
/// <param name="Count">Messages of the conversation in all folders (without trash and spam), at least those in this folder.</param>
public sealed record ConversationRow(StoredMessage Newest, IReadOnlyList<long> Uids, int Count, bool Unread, bool Flagged);

/// <summary>A message of a conversation, wherever it is.</summary>
public sealed record ConversationMessage(Folder Folder, StoredMessage Message);

/// <summary>
/// Which messages belong together, as in Gmail or Outlook: replies point to earlier messages with "In-Reply-To" and
/// "References". Mail programs that leave those out are matched by subject ("AW: …" to a recent "…"). Messages are added in
/// the background (and before a list is shown, up to a limit); the data is keyed by message file, so moving a mail keeps it.
/// </summary>
public sealed partial class Conversations(Database database, DataPaths paths, ILogger<Conversations> logger)
{
    /// <summary>Folders whose list is never grouped: every draft or planned mail stands for itself, trash and spam are sorted out.</summary>
    public static readonly IReadOnlySet<string> FlatFolders = new HashSet<string> { "Drafts", "Trash", "Junk", FollowUps.ScheduledFolder };

    private static readonly TimeSpan SubjectWindow = TimeSpan.FromDays(60);
    private readonly SemaphoreSlim _indexing = new(1, 1);

    /// <summary>Adds up to <paramref name="max"/> new messages (of one account, or of all), oldest first; returns how many.</summary>
    public async Task<int> IndexPendingAsync(int max, long? accountId = null, CancellationToken cancellationToken = default)
    {
        await _indexing.WaitAsync(cancellationToken);
        try
        {
            List<(string File, long Account, DateTimeOffset Date)> pending;
            using (var connection = database.Open())
            {
                pending = connection.Query(
                    """
                    SELECT m.file_name, f.account_id, MIN(m.internal_date_utc) FROM messages m JOIN folders f ON f.id = m.folder_id
                    LEFT JOIN conversation_docs c ON c.file_name = m.file_name
                    WHERE c.file_name IS NULL AND ($account IS NULL OR f.account_id = $account)
                    GROUP BY m.file_name ORDER BY MIN(m.internal_date_utc) LIMIT $max
                    """, r => (r.GetString(0), r.GetInt64(1), r.GetDbTime(2)), ("$account", accountId), ("$max", max));
            }

            foreach (var (file, account, date) in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var headers = await ReadHeadersAsync(file, cancellationToken);
                Add(account, file, date, headers);
            }

            return pending.Count;
        }
        finally
        {
            _indexing.Release();
        }
    }

    /// <summary>Forgets the messages whose files are gone (the message ids stay, so a later reply still finds its conversation).</summary>
    public int RemoveStale()
    {
        using var connection = database.Open();
        return connection.Execute(
            "DELETE FROM conversation_docs WHERE NOT EXISTS (SELECT 1 FROM messages m WHERE m.file_name = conversation_docs.file_name)");
    }

    /// <summary>One page of a folder in conversation view, newest conversation first, and the number of conversations.</summary>
    public (IReadOnlyList<ConversationRow> Rows, int Total) List(Folder folder, int offset, int count)
    {
        using var connection = database.Open();
        // Messages not added yet count as a conversation of their own.
        const string grouped =
            """
            WITH x AS (
                SELECT m.id, m.folder_id, m.uid, m.flags, m.internal_date_utc, m.size, m.file_name, COALESCE(c.thread_id, -m.id) AS g
                FROM messages m LEFT JOIN conversation_docs c ON c.file_name = m.file_name WHERE m.folder_id = $folder),
            y AS (
                SELECT x.*,
                    ROW_NUMBER() OVER w AS rn,
                    group_concat(uid) OVER a AS uids,
                    COUNT(*) OVER a AS n,
                    SUM(flags NOT LIKE '%\Seen%') OVER a AS unseen,
                    SUM(flags LIKE '%\Flagged%') OVER a AS flagged
                FROM x
                WINDOW w AS (PARTITION BY g ORDER BY internal_date_utc DESC, uid DESC),
                       a AS (PARTITION BY g ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING))
            """;
        var total = Convert.ToInt32(connection.Scalar($"{grouped} SELECT COUNT(*) FROM y WHERE rn = 1", ("$folder", folder.Id)));
        var rows = connection.Query(
            $"""
            {grouped}
            SELECT id, folder_id, uid, flags, internal_date_utc, size, file_name, g, uids, n, unseen, flagged FROM y WHERE rn = 1
            ORDER BY internal_date_utc DESC, uid DESC LIMIT $count OFFSET $offset
            """,
            r => (Row: new ConversationRow(MailboxStore.ReadMessage(r), r.GetString(8).Split(',').Select(long.Parse).ToList(),
                    Convert.ToInt32(r.GetInt64(9)), r.GetInt64(10) > 0, r.GetInt64(11) > 0),
                Thread: r.GetInt64(7)),
            ("$folder", folder.Id), ("$count", count), ("$offset", offset));

        // Replies in "Sent" (and mails in other folders) count as well.
        var threads = rows.Where(r => r.Thread > 0).Select(r => r.Thread).Distinct().ToList();
        var counts = new Dictionary<long, int>();
        if (threads.Count > 0)
        {
            foreach (var (thread, n) in connection.Query(
                         $"""
                         SELECT c.thread_id, COUNT(DISTINCT COALESCE(c.message_id, c.file_name)) FROM conversation_docs c
                         JOIN messages m ON m.file_name = c.file_name JOIN folders f ON f.id = m.folder_id
                         WHERE c.thread_id IN ({string.Join(",", threads)}) AND f.name NOT IN ('Trash', 'Junk')
                         GROUP BY c.thread_id
                         """, r => (r.GetInt64(0), Convert.ToInt32(r.GetInt64(1)))))
            {
                counts[thread] = n;
            }
        }

        return (rows.Select(r => counts.TryGetValue(r.Thread, out var n) && n > r.Row.Count ? r.Row with { Count = n } : r.Row).ToList(),
            total);
    }

    /// <summary>
    /// The conversation of a message, oldest first: its messages in all folders except trash and spam (the message itself is
    /// always part of it). The same mail in two folders (e.g. sent to oneself) appears once. Just the message itself if it
    /// has not been added yet.
    /// </summary>
    public IReadOnlyList<ConversationMessage> Thread(long accountId, Folder folder, StoredMessage message)
    {
        using var connection = database.Open();
        if (connection.Scalar("SELECT thread_id FROM conversation_docs WHERE file_name = $file AND account_id = $account",
                ("$file", message.FileName), ("$account", accountId)) is not long thread)
        {
            return [new ConversationMessage(folder, message)];
        }

        var all = connection.Query(
            """
            SELECT m.id, m.folder_id, m.uid, m.flags, m.internal_date_utc, m.size, m.file_name,
                   f.id, f.account_id, f.name, f.uid_validity, f.uid_next, f.subscribed, c.message_id, c.date_utc
            FROM conversation_docs c JOIN messages m ON m.file_name = c.file_name JOIN folders f ON f.id = m.folder_id
            WHERE c.thread_id = $thread AND f.account_id = $account
            ORDER BY c.date_utc, m.internal_date_utc, m.id
            """,
            r => (Item: new ConversationMessage(new Folder(r.GetInt64(7), r.GetInt64(8), r.GetString(9), r.GetInt64(10), r.GetInt64(11),
                    r.GetInt64(12) != 0, 0), MailboxStore.ReadMessage(r)),
                MessageId: r.IsDBNull(13) ? null : r.GetString(13)),
            ("$thread", thread), ("$account", accountId));

        var current = all.FirstOrDefault(x => x.Item.Message.Id == message.Id).MessageId;
        var seen = new HashSet<string>();
        var result = new List<ConversationMessage>();
        foreach (var (item, messageId) in all)
        {
            var isCurrent = item.Message.Id == message.Id;
            if (!isCurrent && item.Folder.Name is "Trash" or "Junk")
            {
                continue;
            }

            // A copy of the current message elsewhere is left out; of other copies, the first one stays.
            if (messageId is not null && !isCurrent && (messageId == current || !seen.Add(messageId)))
            {
                continue;
            }

            if (isCurrent && messageId is not null)
            {
                seen.Add(messageId);
            }

            result.Add(item);
        }

        return result;
    }

    /// <summary>"AW: Re: [Liste] Angebot" and "Angebot" are the same subject.</summary>
    public static string SubjectKey(string? subject)
    {
        var text = subject ?? "";
        string previous;
        do
        {
            previous = text;
            text = ReplyPrefix().Replace(text, "").Trim();
        }
        while (text != previous);

        return WhiteSpace().Replace(text, " ").ToLowerInvariant();
    }

    /// <summary>Whether the subject marks a reply ("Re:", "AW:" …), for mail programs that do not send "References".</summary>
    public static bool IsReply(string? subject) => subject is not null && AnswerPrefix().IsMatch(subject);

    private void Add(long accountId, string file, DateTimeOffset date, HeaderList? headers)
    {
        var own = NormalizeId(headers?[HeaderId.MessageId]);
        var related = new List<string>();
        foreach (var field in new[] { HeaderId.References, HeaderId.InReplyTo })
        {
            if (headers?[field] is { } value)
            {
                related.AddRange(MimeKit.Utils.MimeUtils.EnumerateReferences(value).Select(NormalizeId).OfType<string>());
            }
        }

        var subject = headers?[HeaderId.Subject];
        var subjectKey = SubjectKey(subject);
        var ids = related.Prepend(own).OfType<string>().Distinct().ToList();

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var threads = new SortedSet<long>();
        foreach (var id in ids)
        {
            if (connection.Scalar("SELECT thread_id FROM conversation_ids WHERE account_id = $account AND message_id = $id", transaction,
                    ("$account", accountId), ("$id", id)) is long thread)
            {
                threads.Add(thread);
            }
        }

        if (threads.Count == 0 && related.Count == 0 && IsReply(subject) && subjectKey.Length > 0)
        {
            // No references: the most recent conversation with the same subject.
            if (connection.Scalar(
                    """
                    SELECT thread_id FROM conversation_docs WHERE account_id = $account AND subject_key = $key AND date_utc BETWEEN $from AND $to
                    ORDER BY date_utc DESC LIMIT 1
                    """, transaction, ("$account", accountId), ("$key", subjectKey), ("$from", (date - SubjectWindow).ToDbTime()),
                    ("$to", (date + TimeSpan.FromDays(1)).ToDbTime())) is long bySubject)
            {
                threads.Add(bySubject);
            }
        }

        long target;
        if (threads.Count == 0)
        {
            target = Convert.ToInt64(connection.Scalar(
                "SELECT MAX(COALESCE((SELECT MAX(thread_id) FROM conversation_ids), 0), COALESCE((SELECT MAX(thread_id) FROM conversation_docs), 0)) + 1",
                transaction));
        }
        else
        {
            // This message joins conversations that were separate so far (e.g. the reply arrived before the mail it answers).
            target = threads.Min;
            foreach (var other in threads.Skip(1))
            {
                connection.Execute("UPDATE conversation_ids SET thread_id = $target WHERE thread_id = $other", transaction,
                    ("$target", target), ("$other", other));
                connection.Execute("UPDATE conversation_docs SET thread_id = $target WHERE thread_id = $other", transaction,
                    ("$target", target), ("$other", other));
            }
        }

        foreach (var id in ids)
        {
            connection.Execute("INSERT OR IGNORE INTO conversation_ids (account_id, message_id, thread_id) VALUES ($account, $id, $thread)",
                transaction, ("$account", accountId), ("$id", id), ("$thread", target));
        }

        connection.Execute(
            """
            INSERT OR REPLACE INTO conversation_docs (file_name, account_id, thread_id, message_id, subject_key, date_utc)
            VALUES ($file, $account, $thread, $id, $key, $date)
            """,
            transaction, ("$file", file), ("$account", accountId), ("$thread", target), ("$id", own), ("$key", subjectKey),
            ("$date", date.ToDbTime()));
        transaction.Commit();
    }

    private async Task<HeaderList?> ReadHeadersAsync(string file, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(Path.Combine(paths.MailRoot, file));
            return await HeaderList.LoadAsync(stream, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
        {
            // Added without headers (a conversation of its own), so a broken file is not read again and again.
            logger.LogDebug(ex, "Message file {File} could not be read for conversations", file);
            return null;
        }
    }

    private static string? NormalizeId(string? id)
    {
        var text = id?.Trim().Trim('<', '>').Trim();
        return string.IsNullOrEmpty(text) || text.Length > 500 ? null : text.ToLowerInvariant();
    }

    [GeneratedRegex(@"^((re|aw|antw|wg|fw|fwd|sv|vs|tr|r)(\[\d+\])?\s*:\s*|\[[^\]]{1,40}\]\s*)", RegexOptions.IgnoreCase)]
    private static partial Regex ReplyPrefix();

    [GeneratedRegex(@"^\s*(\[[^\]]{1,40}\]\s*)?(re|aw|antw|sv|vs|r)(\[\d+\])?\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex AnswerPrefix();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}
