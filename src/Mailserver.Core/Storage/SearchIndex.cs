using System.Text;
using Mailserver.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Mailserver.Core.Storage;

/// <summary>What to look for; every part is optional.</summary>
/// <param name="Text">Words that must all occur (in subject, sender, recipients, text or attachment names).</param>
/// <param name="FolderId">Only this folder; null searches all folders of the account.</param>
/// <param name="InBody">Also search the text and attachment names, not only subject, sender and recipients.</param>
public sealed record MailSearch(
    string? Text,
    long? FolderId = null,
    bool InBody = true,
    bool Unread = false,
    bool Flagged = false,
    bool WithAttachment = false,
    DateTimeOffset? Since = null,
    DateTimeOffset? Before = null)
{
    /// <summary>The trigram index needs at least three characters per word.</summary>
    public const int MinWordLength = 3;

    public IReadOnlyList<string> Words => (Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(w => w.Trim('"')).Where(w => w.Length >= MinWordLength).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public bool IsEmpty => Words.Count == 0 && !Unread && !Flagged && !WithAttachment && Since is null && Before is null;
}

public sealed record SearchHit(StoredMessage Message, string Folder);

/// <summary>
/// Full-text index of all messages for the webmail search. Messages are indexed by the SearchIndexer service in the
/// background (and before a search, up to a limit); the index is keyed by message file, so moved or copied mails are found
/// without being indexed again.
/// </summary>
public sealed class SearchIndex(Database database, DataPaths paths, ILogger<SearchIndex> logger)
{
    private const int MaxBodyChars = 100_000;
    private readonly SemaphoreSlim _indexing = new(1, 1);

    /// <summary>Messages of the account (all, if null) that are not in the index yet.</summary>
    public int Pending(long? accountId = null)
    {
        using var connection = database.Open();
        return Convert.ToInt32(connection.Scalar(
            """
            SELECT COUNT(DISTINCT m.file_name) FROM messages m JOIN folders f ON f.id = m.folder_id
            LEFT JOIN search_docs d ON d.file_name = m.file_name
            WHERE d.id IS NULL AND ($account IS NULL OR f.account_id = $account)
            """, ("$account", accountId)));
    }

    /// <summary>Indexes up to <paramref name="max"/> new messages (of one account, or of all); returns how many.</summary>
    public async Task<int> IndexPendingAsync(int max, long? accountId = null, CancellationToken cancellationToken = default)
    {
        await _indexing.WaitAsync(cancellationToken);
        try
        {
            List<string> files;
            using (var connection = database.Open())
            {
                files = connection.Query(
                    """
                    SELECT DISTINCT m.file_name FROM messages m JOIN folders f ON f.id = m.folder_id
                    LEFT JOIN search_docs d ON d.file_name = m.file_name
                    WHERE d.id IS NULL AND ($account IS NULL OR f.account_id = $account)
                    LIMIT $max
                    """, r => r.GetString(0), ("$account", accountId), ("$max", max));
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var document = await ReadAsync(file, cancellationToken);
                using var connection = database.Open();
                using var transaction = connection.BeginTransaction();
                connection.Execute("INSERT OR IGNORE INTO search_docs (file_name, has_attachment) VALUES ($file, $attachment)", transaction,
                    ("$file", file), ("$attachment", document.HasAttachment ? 1 : 0));
                var id = connection.Scalar("SELECT id FROM search_docs WHERE file_name = $file", transaction, ("$file", file));
                connection.Execute("DELETE FROM search_text WHERE rowid = $id", transaction, ("$id", id));
                connection.Execute(
                    "INSERT INTO search_text (rowid, subject, sender, recipients, body, attachments) VALUES ($id, $subject, $sender, $recipients, $body, $attachments)",
                    transaction, ("$id", id), ("$subject", document.Subject), ("$sender", document.Sender), ("$recipients", document.Recipients),
                    ("$body", document.Body), ("$attachments", document.Attachments));
                transaction.Commit();
            }

            return files.Count;
        }
        finally
        {
            _indexing.Release();
        }
    }

    /// <summary>Removes entries whose message files are gone (deleted mails).</summary>
    public int RemoveStale()
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        connection.Execute(
            "DELETE FROM search_text WHERE rowid IN (SELECT d.id FROM search_docs d WHERE NOT EXISTS (SELECT 1 FROM messages m WHERE m.file_name = d.file_name))",
            transaction);
        var removed = connection.Execute("DELETE FROM search_docs WHERE NOT EXISTS (SELECT 1 FROM messages m WHERE m.file_name = search_docs.file_name)",
            transaction);
        transaction.Commit();
        return removed;
    }

    /// <summary>Matching messages of the account, newest first.</summary>
    public (IReadOnlyList<SearchHit> Hits, int Total) Search(long accountId, MailSearch search, int offset, int count)
    {
        var where = new StringBuilder("f.account_id = $account");
        var parameters = new List<(string, object?)> { ("$account", accountId) };
        if (search.FolderId is { } folderId)
        {
            where.Append(" AND m.folder_id = $folder");
            parameters.Add(("$folder", folderId));
        }

        if (search.Unread)
        {
            where.Append(" AND m.flags NOT LIKE '%\\Seen%'");
        }

        if (search.Flagged)
        {
            where.Append(" AND m.flags LIKE '%\\Flagged%'");
        }

        if (search.WithAttachment)
        {
            where.Append(" AND d.has_attachment = 1");
        }

        if (search.Since is { } since)
        {
            where.Append(" AND m.internal_date_utc >= $since");
            parameters.Add(("$since", since.ToDbTime()));
        }

        if (search.Before is { } before)
        {
            where.Append(" AND m.internal_date_utc < $before");
            parameters.Add(("$before", before.ToDbTime()));
        }

        if (search.Words.Count > 0)
        {
            // Each word as a quoted phrase: no FTS syntax from the user gets through. Without "in the text", only the
            // header columns count.
            var columns = search.InBody ? "" : "{subject sender recipients} : ";
            var match = string.Join(" AND ", search.Words.Select(w => $"{columns}\"{w.Replace("\"", "\"\"")}\""));
            where.Append(" AND d.id IN (SELECT rowid FROM search_text WHERE search_text MATCH $match)");
            parameters.Add(("$match", match));
        }

        var from = $"messages m JOIN folders f ON f.id = m.folder_id {(search.Words.Count > 0 || search.WithAttachment ? "" : "LEFT ")}JOIN search_docs d ON d.file_name = m.file_name WHERE {where}";
        using var connection = database.Open();
        var total = Convert.ToInt32(connection.Scalar($"SELECT COUNT(*) FROM {from}", [.. parameters]));
        var hits = connection.Query(
            $"SELECT m.id, m.folder_id, m.uid, m.flags, m.internal_date_utc, m.size, m.file_name, f.name FROM {from} " +
            "ORDER BY m.internal_date_utc DESC, m.id DESC LIMIT $count OFFSET $offset",
            r => new SearchHit(MailboxStore.ReadMessage(r), r.GetString(7)),
            [.. parameters, ("$count", count), ("$offset", offset)]);
        return (hits, total);
    }

    private sealed record Document(string Subject, string Sender, string Recipients, string Body, string Attachments, bool HasAttachment);

    private async Task<Document> ReadAsync(string file, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(Path.Combine(paths.MailRoot, file));
            var message = await MimeMessage.LoadAsync(stream, cancellationToken);
            var body = message.TextBody;
            if (string.IsNullOrWhiteSpace(body) && message.HtmlBody is { } html)
            {
                body = HtmlText(html);
            }

            body ??= "";
            var attachments = message.Attachments.Select(a => a.ContentDisposition?.FileName ?? (a as MimePart)?.FileName)
                .Where(n => !string.IsNullOrEmpty(n)).ToList();
            return new Document(
                message.Subject ?? "",
                message.From.ToString(),
                string.Join(", ", message.To.Concat(message.Cc)),
                body.Length > MaxBodyChars ? body[..MaxBodyChars] : body,
                string.Join(" ", attachments),
                attachments.Count > 0);
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
        {
            // Indexed empty, so a broken or missing file is not tried again and again.
            logger.LogDebug(ex, "Message file {File} could not be indexed", file);
            return new Document("", "", "", "", "", false);
        }
    }

    /// <summary>Rough text of an HTML body: tags and entities removed, enough for searching.</summary>
    private static string HtmlText(string html)
    {
        var withoutBlocks = System.Text.RegularExpressions.Regex.Replace(html, "<(script|style)[^>]*>.*?</\\1>", " ",
            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        var text = System.Text.RegularExpressions.Regex.Replace(withoutBlocks, "<[^>]+>", " ", System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromSeconds(2));
        return System.Net.WebUtility.HtmlDecode(text);
    }
}
