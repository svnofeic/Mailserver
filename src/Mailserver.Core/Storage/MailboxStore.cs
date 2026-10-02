using Mailserver.Core.Accounts;
using Mailserver.Core.Data;
using Microsoft.Data.Sqlite;

namespace Mailserver.Core.Storage;

public sealed record Folder(long Id, long AccountId, string Name, long UidValidity, long UidNext, bool Subscribed);

public sealed record StoredMessage(long Id, long FolderId, long Uid, string Flags, DateTimeOffset InternalDate, long Size, string FileName);

/// <summary>
/// Mailbox storage: message bodies are .eml files below data/mail/{accountId}/, metadata (folders, UIDs, flags) lives in SQLite.
/// The UID scheme follows IMAP (UIDVALIDITY per folder, strictly ascending UIDs).
/// </summary>
public sealed class MailboxStore(Database database, DataPaths paths)
{
    public const string Inbox = "INBOX";

    public static readonly IReadOnlyList<string> DefaultFolders = [Inbox, "Sent", "Drafts", "Trash", "Junk"];

    public Folder GetOrCreateFolder(long accountId, string name)
    {
        name = NormalizeFolderName(name);
        using var connection = database.Open();
        connection.Execute(
            "INSERT OR IGNORE INTO folders (account_id, name, uid_validity) VALUES ($account, $name, $validity)",
            ("$account", accountId), ("$name", name), ("$validity", NewUidValidity()));
        return GetFolder(connection, accountId, name)!;
    }

    public void EnsureDefaultFolders(long accountId)
    {
        foreach (var name in DefaultFolders)
        {
            GetOrCreateFolder(accountId, name);
        }
    }

    public Folder? GetFolder(long accountId, string name)
    {
        using var connection = database.Open();
        return GetFolder(connection, accountId, NormalizeFolderName(name));
    }

    public IReadOnlyList<Folder> ListFolders(long accountId)
    {
        using var connection = database.Open();
        return connection.Query(
            "SELECT id, account_id, name, uid_validity, uid_next, subscribed FROM folders WHERE account_id = $account ORDER BY name",
            ReadFolder, ("$account", accountId));
    }

    /// <summary>Stores a message in a folder (created on demand) and assigns the next UID.</summary>
    public async Task<StoredMessage> AppendAsync(Account account, ReadOnlyMemory<byte> message, string folderName = Inbox, string flags = "",
        DateTimeOffset? internalDate = null, CancellationToken cancellationToken = default)
    {
        var folder = GetOrCreateFolder(account.Id, folderName);
        var date = internalDate ?? DateTimeOffset.UtcNow;
        var relativePath = Path.Combine(account.Id.ToString(), date.UtcDateTime.ToString("yyyyMM"), $"{Guid.NewGuid():N}.eml");
        var fullPath = Path.Combine(paths.MailRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        // Write the file first, then the index entry. A crash in between leaves an orphaned file, never a dangling entry.
        await WriteFileAsync(fullPath, message, cancellationToken);

        try
        {
            using var connection = database.Open();
            using var transaction = connection.BeginTransaction();
            var uid = (long)connection.Scalar(
                "UPDATE folders SET uid_next = uid_next + 1 WHERE id = $folder RETURNING uid_next - 1",
                transaction, ("$folder", folder.Id))!;
            var id = (long)connection.Scalar(
                """
                INSERT INTO messages (folder_id, uid, flags, internal_date_utc, size, file_name)
                VALUES ($folder, $uid, $flags, $date, $size, $file) RETURNING id
                """,
                transaction, ("$folder", folder.Id), ("$uid", uid), ("$flags", flags), ("$date", date.ToDbTime()),
                ("$size", message.Length), ("$file", relativePath))!;
            transaction.Commit();
            return new StoredMessage(id, folder.Id, uid, flags, date, message.Length, relativePath);
        }
        catch
        {
            File.Delete(fullPath);
            throw;
        }
    }

    public IReadOnlyList<StoredMessage> ListMessages(long folderId)
    {
        using var connection = database.Open();
        return connection.Query(
            "SELECT id, folder_id, uid, flags, internal_date_utc, size, file_name FROM messages WHERE folder_id = $folder ORDER BY uid",
            r => new StoredMessage(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetDbTime(4), r.GetInt64(5), r.GetString(6)),
            ("$folder", folderId));
    }

    public Stream OpenMessage(StoredMessage message) =>
        new FileStream(Path.Combine(paths.MailRoot, message.FileName), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

    /// <summary>Total size of all messages of an account in bytes.</summary>
    public long GetUsage(long accountId)
    {
        using var connection = database.Open();
        return Convert.ToInt64(connection.Scalar(
            "SELECT COALESCE(SUM(m.size), 0) FROM messages m JOIN folders f ON f.id = m.folder_id WHERE f.account_id = $account",
            ("$account", accountId)));
    }

    public bool IsOverQuota(Account account, long additionalBytes = 0) =>
        account.QuotaBytes > 0 && GetUsage(account.Id) + additionalBytes > account.QuotaBytes;

    private static async Task WriteFileAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await stream.WriteAsync(content, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private static Folder? GetFolder(SqliteConnection connection, long accountId, string name) =>
        connection.Query(
            "SELECT id, account_id, name, uid_validity, uid_next, subscribed FROM folders WHERE account_id = $account AND name = $name",
            ReadFolder, ("$account", accountId), ("$name", name)).SingleOrDefault();

    private static Folder ReadFolder(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5) != 0);

    // IMAP treats INBOX case-insensitively; every other name is case-sensitive.
    private static string NormalizeFolderName(string name) =>
        string.Equals(name, Inbox, StringComparison.OrdinalIgnoreCase) ? Inbox : name;

    private static long NewUidValidity() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
