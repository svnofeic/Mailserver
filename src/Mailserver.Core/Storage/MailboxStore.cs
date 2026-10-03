using Mailserver.Core.Accounts;
using Mailserver.Core.Data;
using Microsoft.Data.Sqlite;

namespace Mailserver.Core.Storage;

public sealed record Folder(long Id, long AccountId, string Name, long UidValidity, long UidNext, bool Subscribed, long ChangeCounter);

public sealed record StoredMessage(long Id, long FolderId, long Uid, string Flags, DateTimeOffset InternalDate, long Size, string FileName)
{
    public IReadOnlyList<string> FlagList => MessageFlags.Parse(Flags);

    public bool HasFlag(string flag) => MessageFlags.Parse(Flags).Contains(flag, StringComparer.OrdinalIgnoreCase);
}

public sealed record FolderStatus(long Messages, long Unseen, long UidNext, long UidValidity);

public enum FlagOperation
{
    Replace,
    Add,
    Remove,
}

/// <summary>System flags as defined by IMAP plus helpers for the space-separated storage format.</summary>
public static class MessageFlags
{
    public const string Seen = @"\Seen";
    public const string Answered = @"\Answered";
    public const string Flagged = @"\Flagged";
    public const string Deleted = @"\Deleted";
    public const string Draft = @"\Draft";

    public static readonly IReadOnlyList<string> System = [Answered, Flagged, Deleted, Seen, Draft];

    public static IReadOnlyList<string> Parse(string flags) =>
        flags.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Canonical spelling for system flags, de-duplicated, stable order.</summary>
    public static string Format(IEnumerable<string> flags) =>
        string.Join(' ', flags
            .Select(f => System.FirstOrDefault(s => s.Equals(f, StringComparison.OrdinalIgnoreCase)) ?? f)
            .Where(f => !f.Equals(@"\Recent", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Mailbox storage: message bodies are .eml files below data/mail/{accountId}/, metadata (folders, UIDs, flags) lives in SQLite.
/// The UID scheme follows IMAP (UIDVALIDITY per folder, strictly ascending UIDs).
/// </summary>
public sealed class MailboxStore(Database database, DataPaths paths)
{
    public const string Inbox = "INBOX";
    public const char HierarchyDelimiter = '/';

    public static readonly IReadOnlyList<string> DefaultFolders = [Inbox, "Sent", "Drafts", "Trash", "Junk"];

    /// <summary>
    /// Names other mail programs (Outlook, older Apple Mail, German clients) use for the special folders. Programs that
    /// ignore SPECIAL-USE would otherwise create a second "Sent", "Trash" … next to the real one, and webmail and the
    /// other devices would no longer show the same content.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> SpecialFolderAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Sent Items"] = "Sent", ["Sent Messages"] = "Sent", ["Sent Mail"] = "Sent", ["Gesendet"] = "Sent",
        ["Gesendete Elemente"] = "Sent", ["Gesendete Objekte"] = "Sent", ["Gesendete Nachrichten"] = "Sent",
        ["Deleted Items"] = "Trash", ["Deleted Messages"] = "Trash", ["Gelöschte Elemente"] = "Trash",
        ["Gelöschte Objekte"] = "Trash", ["Gelöschte Nachrichten"] = "Trash", ["Papierkorb"] = "Trash", ["Bin"] = "Trash",
        ["Junk E-Mail"] = "Junk", ["Junk-E-Mail"] = "Junk", ["Junk Email"] = "Junk", ["Spam"] = "Junk", ["Werbung"] = "Junk",
        ["Bulk Mail"] = "Junk",
        ["Entwürfe"] = "Drafts", ["Draft"] = "Drafts",
        ["Archiv"] = "Archive", ["Archives"] = "Archive",
    };

    /// <summary>
    /// The folder a client means: the folder itself if it exists, otherwise the special folder an alias such as
    /// "Gesendete Elemente" stands for (if that one exists), otherwise the name unchanged.
    /// </summary>
    public string ResolveFolderName(long accountId, string name) =>
        GetFolder(accountId, name) is null && AliasTarget(accountId, name) is { } target ? target : name;

    /// <summary>The existing special folder <paramref name="name"/> is an alias for, or null.</summary>
    public string? AliasTarget(long accountId, string name) =>
        SpecialFolderAliases.TryGetValue(name, out var target) && GetFolder(accountId, target) is not null ? target : null;

    /// <summary>Folders the server and mail clients rely on; they cannot be renamed or deleted.</summary>
    public static bool IsSystemFolder(string name) => DefaultFolders.Contains(NormalizeFolderName(name));

    /// <summary>German error text for an unusable folder name, or null if it is fine.</summary>
    public static string? ValidateFolderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Bitte einen Ordnernamen angeben.";
        }

        if (name.Length > 200)
        {
            return "Der Ordnername ist zu lang.";
        }

        var segments = name.Split(HierarchyDelimiter);
        if (segments.Any(segment => segment.Trim().Length == 0 || segment != segment.Trim()))
        {
            return "Ordnernamen dürfen nicht leer sein und nicht mit Leerzeichen beginnen oder enden.";
        }

        if (name.Any(c => char.IsControl(c) || c is '*' or '%' or '\\' or '"'))
        {
            return "Ordnernamen dürfen die Zeichen * % \\ \" nicht enthalten.";
        }

        return null;
    }

    private const string FolderColumns = "id, account_id, name, uid_validity, uid_next, subscribed, change_counter";
    private const string MessageColumns = "id, folder_id, uid, flags, internal_date_utc, size, file_name";

    /// <summary>Raised after messages in a folder were added, removed or changed. The argument is the folder id.</summary>
    public event Action<long>? FolderChanged;

    // ---- Folders ----

    public Folder GetOrCreateFolder(long accountId, string name)
    {
        name = NormalizeFolderName(name);
        using var connection = database.Open();
        connection.Execute(
            $"INSERT OR IGNORE INTO folders (account_id, name, uid_validity) VALUES ($account, $name, {NewUidValidity})",
            ("$account", accountId), ("$name", name), ("$now", Now()));
        return GetFolder(connection, accountId, name)!;
    }

    /// <summary>Creates a folder; returns null if it already exists.</summary>
    public Folder? CreateFolder(long accountId, string name)
    {
        name = NormalizeFolderName(name);
        using var connection = database.Open();
        var created = connection.Execute(
            $"INSERT OR IGNORE INTO folders (account_id, name, uid_validity) VALUES ($account, $name, {NewUidValidity})",
            ("$account", accountId), ("$name", name), ("$now", Now())) > 0;
        return created ? GetFolder(connection, accountId, name) : null;
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

    public Folder? GetFolder(long folderId)
    {
        using var connection = database.Open();
        return connection.Query($"SELECT {FolderColumns} FROM folders WHERE id = $id", ReadFolder, ("$id", folderId)).SingleOrDefault();
    }

    public IReadOnlyList<Folder> ListFolders(long accountId)
    {
        using var connection = database.Open();
        return connection.Query($"SELECT {FolderColumns} FROM folders WHERE account_id = $account ORDER BY name", ReadFolder,
            ("$account", accountId));
    }

    /// <summary>Deletes a folder and all its messages. Sub-folders are kept.</summary>
    public bool DeleteFolder(long accountId, string name)
    {
        name = NormalizeFolderName(name);
        List<string> files;
        using (var connection = database.Open())
        using (var transaction = connection.BeginTransaction())
        {
            var folder = GetFolder(connection, accountId, name, transaction);
            if (folder is null)
            {
                return false;
            }

            files = connection.Query("SELECT file_name FROM messages WHERE folder_id = $folder", transaction, r => r.GetString(0),
                ("$folder", folder.Id));
            connection.Execute("DELETE FROM folders WHERE id = $folder", transaction, ("$folder", folder.Id));
            transaction.Commit();
        }

        DeleteFiles(files);
        return true;
    }

    /// <summary>Renames a folder and all of its sub-folders. Returns false if the source is missing or the target exists.</summary>
    public bool RenameFolder(long accountId, string oldName, string newName)
    {
        oldName = NormalizeFolderName(oldName);
        newName = NormalizeFolderName(newName);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        if (GetFolder(connection, accountId, oldName, transaction) is null || GetFolder(connection, accountId, newName, transaction) is not null)
        {
            return false;
        }

        var prefix = oldName + HierarchyDelimiter;
        var renames = connection.Query($"SELECT {FolderColumns} FROM folders WHERE account_id = $account", transaction, ReadFolder,
                ("$account", accountId))
            .Where(f => f.Name == oldName || f.Name.StartsWith(prefix, StringComparison.Ordinal))
            .Select(f => (f.Id, Name: newName + f.Name[oldName.Length..]))
            .ToList();
        foreach (var (id, name) in renames)
        {
            // A new UIDVALIDITY tells clients that cached UIDs under the old name do not apply to the new one.
            connection.Execute($"UPDATE folders SET name = $name, uid_validity = {NewUidValidity} WHERE id = $id", transaction,
                ("$name", name), ("$now", Now()), ("$id", id));
        }

        transaction.Commit();
        return true;
    }

    public bool SetSubscribed(long accountId, string name, bool subscribed)
    {
        using var connection = database.Open();
        return connection.Execute("UPDATE folders SET subscribed = $value WHERE account_id = $account AND name = $name",
            ("$value", subscribed ? 1 : 0), ("$account", accountId), ("$name", NormalizeFolderName(name))) > 0;
    }

    public FolderStatus GetStatus(long folderId)
    {
        using var connection = database.Open();
        var folder = connection.Query($"SELECT {FolderColumns} FROM folders WHERE id = $id", ReadFolder, ("$id", folderId)).Single();
        var (total, seen) = connection.Query(
            "SELECT COUNT(*), COALESCE(SUM(CASE WHEN (' ' || flags || ' ') LIKE '% \\Seen %' THEN 1 ELSE 0 END), 0) FROM messages WHERE folder_id = $folder",
            r => (r.GetInt64(0), r.GetInt64(1)), ("$folder", folderId)).Single();
        return new FolderStatus(total, total - seen, folder.UidNext, folder.UidValidity);
    }

    // ---- Messages ----

    /// <summary>Stores a message in a folder (created on demand) and assigns the next UID.</summary>
    public Task<StoredMessage> AppendAsync(Account account, ReadOnlyMemory<byte> message, string folderName = Inbox, string flags = "",
        DateTimeOffset? internalDate = null, CancellationToken cancellationToken = default) =>
        AppendAsync(GetOrCreateFolder(account.Id, folderName), message, flags, internalDate, cancellationToken);

    public async Task<StoredMessage> AppendAsync(Folder folder, ReadOnlyMemory<byte> message, string flags = "",
        DateTimeOffset? internalDate = null, CancellationToken cancellationToken = default)
    {
        var date = internalDate ?? DateTimeOffset.UtcNow;
        var relativePath = NewRelativePath(folder.AccountId, date);
        var fullPath = Path.Combine(paths.MailRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        // Write the file first, then the index entry. A crash in between leaves an orphaned file, never a dangling entry.
        await WriteFileAsync(fullPath, message, cancellationToken);

        StoredMessage stored;
        try
        {
            using var connection = database.Open();
            using var transaction = connection.BeginTransaction();
            var uid = NextUid(connection, transaction, folder.Id);
            flags = MessageFlags.Format(MessageFlags.Parse(flags));
            var id = (long)connection.Scalar(
                $"INSERT INTO messages (folder_id, uid, flags, internal_date_utc, size, file_name) VALUES ($folder, $uid, $flags, $date, $size, $file) RETURNING id",
                transaction, ("$folder", folder.Id), ("$uid", uid), ("$flags", flags), ("$date", date.ToDbTime()),
                ("$size", message.Length), ("$file", relativePath))!;
            transaction.Commit();
            stored = new StoredMessage(id, folder.Id, uid, flags, date, message.Length, relativePath);
        }
        catch
        {
            File.Delete(fullPath);
            throw;
        }

        FolderChanged?.Invoke(folder.Id);
        return stored;
    }

    public IReadOnlyList<StoredMessage> ListMessages(long folderId)
    {
        using var connection = database.Open();
        return connection.Query($"SELECT {MessageColumns} FROM messages WHERE folder_id = $folder ORDER BY uid", ReadMessage,
            ("$folder", folderId));
    }

    public Stream OpenMessage(StoredMessage message) =>
        new FileStream(GetMessagePath(message), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, useAsync: true);

    public string GetMessagePath(StoredMessage message) => Path.Combine(paths.MailRoot, message.FileName);

    /// <summary>Changes flags; returns the messages whose flags actually changed, with their new flags.</summary>
    public IReadOnlyList<StoredMessage> UpdateFlags(long folderId, IReadOnlyCollection<long> uids, FlagOperation operation,
        IReadOnlyCollection<string> flags)
    {
        var changed = new List<StoredMessage>();
        using (var connection = database.Open())
        using (var transaction = connection.BeginTransaction())
        {
            foreach (var message in SelectByUid(connection, transaction, folderId, uids))
            {
                var current = MessageFlags.Parse(message.Flags);
                var updated = MessageFlags.Format(operation switch
                {
                    FlagOperation.Replace => flags,
                    FlagOperation.Add => current.Concat(flags),
                    _ => current.Where(f => !flags.Contains(f, StringComparer.OrdinalIgnoreCase)),
                });
                if (updated == message.Flags)
                {
                    continue;
                }

                connection.Execute("UPDATE messages SET flags = $flags WHERE id = $id", transaction, ("$flags", updated), ("$id", message.Id));
                changed.Add(message with { Flags = updated });
            }

            if (changed.Count > 0)
            {
                Touch(connection, transaction, folderId);
            }

            transaction.Commit();
        }

        if (changed.Count > 0)
        {
            FolderChanged?.Invoke(folderId);
        }

        return changed;
    }

    /// <summary>Permanently removes messages flagged \Deleted (optionally limited to <paramref name="uids"/>). Returns the removed UIDs.</summary>
    public IReadOnlyList<long> Expunge(long folderId, IReadOnlyCollection<long>? uids = null)
    {
        List<StoredMessage> removed;
        using (var connection = database.Open())
        using (var transaction = connection.BeginTransaction())
        {
            var candidates = uids is null
                ? connection.Query($"SELECT {MessageColumns} FROM messages WHERE folder_id = $folder", transaction, ReadMessage, ("$folder", folderId))
                : SelectByUid(connection, transaction, folderId, uids);
            removed = candidates.Where(m => m.HasFlag(MessageFlags.Deleted)).OrderBy(m => m.Uid).ToList();
            foreach (var message in removed)
            {
                connection.Execute("DELETE FROM messages WHERE id = $id", transaction, ("$id", message.Id));
            }

            if (removed.Count > 0)
            {
                Touch(connection, transaction, folderId);
            }

            transaction.Commit();
        }

        DeleteFiles(removed.Select(m => m.FileName));
        if (removed.Count > 0)
        {
            FolderChanged?.Invoke(folderId);
        }

        return removed.Select(m => m.Uid).ToList();
    }

    /// <summary>Copies messages into another folder. Returns (source UID, new UID) pairs in ascending source order.</summary>
    public IReadOnlyList<(long SourceUid, long TargetUid)> Copy(long folderId, IReadOnlyCollection<long> uids, long targetFolderId)
    {
        var target = GetFolder(targetFolderId) ?? throw new InvalidOperationException("Target folder does not exist.");
        var messages = ListMessagesByUid(folderId, uids);

        // Copy the files outside the transaction; each copy is an independent file so expunging one never affects the other.
        var copies = messages.Select(m =>
        {
            var relativePath = NewRelativePath(target.AccountId, m.InternalDate);
            var fullPath = Path.Combine(paths.MailRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.Copy(GetMessagePath(m), fullPath);
            return (Message: m, RelativePath: relativePath);
        }).ToList();

        var result = new List<(long, long)>();
        try
        {
            using var connection = database.Open();
            using var transaction = connection.BeginTransaction();
            foreach (var (message, relativePath) in copies)
            {
                var uid = NextUid(connection, transaction, targetFolderId);
                connection.Execute(
                    "INSERT INTO messages (folder_id, uid, flags, internal_date_utc, size, file_name) VALUES ($folder, $uid, $flags, $date, $size, $file)",
                    transaction, ("$folder", targetFolderId), ("$uid", uid), ("$flags", message.Flags), ("$date", message.InternalDate.ToDbTime()),
                    ("$size", message.Size), ("$file", relativePath));
                result.Add((message.Uid, uid));
            }

            transaction.Commit();
        }
        catch
        {
            DeleteFiles(copies.Select(c => c.RelativePath));
            throw;
        }

        if (result.Count > 0)
        {
            FolderChanged?.Invoke(targetFolderId);
        }

        return result;
    }

    /// <summary>Moves messages into another folder (no file copy). Returns (source UID, new UID) pairs in ascending source order.</summary>
    public IReadOnlyList<(long SourceUid, long TargetUid)> Move(long folderId, IReadOnlyCollection<long> uids, long targetFolderId)
    {
        var result = new List<(long, long)>();
        using (var connection = database.Open())
        using (var transaction = connection.BeginTransaction())
        {
            foreach (var message in SelectByUid(connection, transaction, folderId, uids).OrderBy(m => m.Uid))
            {
                var uid = NextUid(connection, transaction, targetFolderId);
                connection.Execute("UPDATE messages SET folder_id = $folder, uid = $uid WHERE id = $id", transaction,
                    ("$folder", targetFolderId), ("$uid", uid), ("$id", message.Id));
                result.Add((message.Uid, uid));
            }

            if (result.Count > 0)
            {
                Touch(connection, transaction, folderId);
            }

            transaction.Commit();
        }

        if (result.Count > 0)
        {
            FolderChanged?.Invoke(folderId);
            FolderChanged?.Invoke(targetFolderId);
        }

        return result;
    }

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

    private List<StoredMessage> ListMessagesByUid(long folderId, IReadOnlyCollection<long> uids)
    {
        using var connection = database.Open();
        return SelectByUid(connection, null, folderId, uids).OrderBy(m => m.Uid).ToList();
    }

    private static List<StoredMessage> SelectByUid(SqliteConnection connection, SqliteTransaction? transaction, long folderId,
        IReadOnlyCollection<long> uids)
    {
        var wanted = uids as IReadOnlySet<long> ?? uids.ToHashSet();
        return connection.Query($"SELECT {MessageColumns} FROM messages WHERE folder_id = $folder ORDER BY uid", transaction, ReadMessage,
                ("$folder", folderId))
            .Where(m => wanted.Contains(m.Uid))
            .ToList();
    }

    private static long NextUid(SqliteConnection connection, SqliteTransaction transaction, long folderId) =>
        (long)connection.Scalar(
            "UPDATE folders SET uid_next = uid_next + 1, change_counter = change_counter + 1 WHERE id = $folder RETURNING uid_next - 1",
            transaction, ("$folder", folderId))!;

    private static void Touch(SqliteConnection connection, SqliteTransaction transaction, long folderId) =>
        connection.Execute("UPDATE folders SET change_counter = change_counter + 1 WHERE id = $folder", transaction, ("$folder", folderId));

    private void DeleteFiles(IEnumerable<string> relativePaths)
    {
        foreach (var relativePath in relativePaths)
        {
            try
            {
                File.Delete(Path.Combine(paths.MailRoot, relativePath));
            }
            catch (IOException)
            {
                // A reader still has it open on a platform without delete sharing; the orphan is harmless.
            }
        }
    }

    private static string NewRelativePath(long accountId, DateTimeOffset date) =>
        Path.Combine(accountId.ToString(), date.UtcDateTime.ToString("yyyyMM"), $"{Guid.NewGuid():N}.eml");

    private static async Task WriteFileAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await stream.WriteAsync(content, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private static Folder? GetFolder(SqliteConnection connection, long accountId, string name, SqliteTransaction? transaction = null) =>
        connection.Query($"SELECT {FolderColumns} FROM folders WHERE account_id = $account AND name = $name", transaction, ReadFolder,
            ("$account", accountId), ("$name", name)).SingleOrDefault();

    private static Folder ReadFolder(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5) != 0, r.GetInt64(6));

    private static StoredMessage ReadMessage(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetDbTime(4), r.GetInt64(5), r.GetString(6));

    // IMAP treats INBOX case-insensitively; every other name is case-sensitive.
    public static string NormalizeFolderName(string name) =>
        string.Equals(name, Inbox, StringComparison.OrdinalIgnoreCase) ? Inbox :
        name.StartsWith(Inbox + HierarchyDelimiter, StringComparison.OrdinalIgnoreCase) ? Inbox + name[Inbox.Length..] : name;

    // UIDVALIDITY must never repeat for a folder name, even if a folder is deleted and recreated within the same second.
    private const string NewUidValidity = "MAX($now, (SELECT COALESCE(MAX(uid_validity), 0) + 1 FROM folders))";

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
