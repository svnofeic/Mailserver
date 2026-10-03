using Mailserver.Core.Data;

namespace Mailserver.Core.Migration;

/// <summary>Remembers which source messages were imported, identified by folder, UIDVALIDITY and UID.</summary>
public sealed class ImportLog(Database database)
{
    public HashSet<long> GetImportedUids(long accountId, string sourceFolder, long uidValidity)
    {
        using var connection = database.Open();
        return connection.Query(
            "SELECT uid FROM import_log WHERE account_id = $account AND source_folder = $folder AND uid_validity = $validity",
            r => r.GetInt64(0), ("$account", accountId), ("$folder", sourceFolder), ("$validity", uidValidity)).ToHashSet();
    }

    public void Record(long accountId, string sourceFolder, long uidValidity, long uid)
    {
        using var connection = database.Open();
        connection.Execute(
            """
            INSERT OR IGNORE INTO import_log (account_id, source_folder, uid_validity, uid, imported_utc)
            VALUES ($account, $folder, $validity, $uid, $now)
            """,
            ("$account", accountId), ("$folder", sourceFolder), ("$validity", uidValidity), ("$uid", uid),
            ("$now", DateTimeOffset.UtcNow.ToDbTime()));
    }
}
