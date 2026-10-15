using Mailserver.Core.Data;

namespace Mailserver.Core.Storage;

public sealed record ScheduledSend(long Id, long AccountId, string FileName, DateTimeOffset SendAt, string? LastError);

public sealed record FollowUp(long Id, long AccountId, string FileName, string ReturnFolder, DateTimeOffset Due);

/// <summary>
/// "Später senden" and "Wiedervorlage": messages waiting in a folder of their own until their time has come. The webmail
/// fills both lists; a background service sends or brings the messages back.
/// </summary>
public sealed class FollowUps(Database database, TimeProvider timeProvider)
{
    public const string ScheduledFolder = "Geplant";
    public const string FollowUpFolder = "Wiedervorlage";

    public long Schedule(long accountId, string fileName, DateTimeOffset sendAt)
    {
        using var connection = database.Open();
        return Convert.ToInt64(connection.Scalar(
            "INSERT INTO scheduled_sends (account_id, file_name, send_utc, created_utc) VALUES ($account, $file, $at, $now) RETURNING id",
            ("$account", accountId), ("$file", fileName), ("$at", sendAt.ToDbTime()), ("$now", timeProvider.GetUtcNow().ToDbTime())));
    }

    public IReadOnlyList<ScheduledSend> Scheduled(long accountId)
    {
        using var connection = database.Open();
        return connection.Query("SELECT id, account_id, file_name, send_utc, last_error FROM scheduled_sends WHERE account_id = $account ORDER BY send_utc",
            ReadScheduled, ("$account", accountId));
    }

    /// <summary>Messages whose time has come (and that have not failed already).</summary>
    public IReadOnlyList<ScheduledSend> DueSends()
    {
        using var connection = database.Open();
        return connection.Query("SELECT id, account_id, file_name, send_utc, last_error FROM scheduled_sends WHERE send_utc <= $now AND last_error IS NULL ORDER BY send_utc",
            ReadScheduled, ("$now", timeProvider.GetUtcNow().ToDbTime()));
    }

    public ScheduledSend? GetScheduled(long accountId, long id)
    {
        using var connection = database.Open();
        return connection.Query("SELECT id, account_id, file_name, send_utc, last_error FROM scheduled_sends WHERE account_id = $account AND id = $id",
            ReadScheduled, ("$account", accountId), ("$id", id)).SingleOrDefault();
    }

    public void SetSendTime(long id, DateTimeOffset sendAt)
    {
        using var connection = database.Open();
        connection.Execute("UPDATE scheduled_sends SET send_utc = $at, last_error = NULL WHERE id = $id", ("$at", sendAt.ToDbTime()), ("$id", id));
    }

    public void SendFailed(long id, string error)
    {
        using var connection = database.Open();
        connection.Execute("UPDATE scheduled_sends SET last_error = $error WHERE id = $id", ("$error", error), ("$id", id));
    }

    public void RemoveScheduled(long id)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM scheduled_sends WHERE id = $id", ("$id", id));
    }

    /// <summary>Removes the schedule of a message (it was edited, sent or deleted).</summary>
    public void RemoveScheduledFile(long accountId, string fileName)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM scheduled_sends WHERE account_id = $account AND file_name = $file", ("$account", accountId), ("$file", fileName));
    }

    public long AddFollowUp(long accountId, string fileName, string returnFolder, DateTimeOffset due)
    {
        using var connection = database.Open();
        // One reminder per message: setting it again moves the time.
        connection.Execute("DELETE FROM follow_ups WHERE account_id = $account AND file_name = $file", ("$account", accountId), ("$file", fileName));
        return Convert.ToInt64(connection.Scalar(
            "INSERT INTO follow_ups (account_id, file_name, return_folder, due_utc, created_utc) VALUES ($account, $file, $folder, $due, $now) RETURNING id",
            ("$account", accountId), ("$file", fileName), ("$folder", returnFolder), ("$due", due.ToDbTime()), ("$now", timeProvider.GetUtcNow().ToDbTime())));
    }

    public IReadOnlyList<FollowUp> FollowUpsOf(long accountId)
    {
        using var connection = database.Open();
        return connection.Query("SELECT id, account_id, file_name, return_folder, due_utc FROM follow_ups WHERE account_id = $account ORDER BY due_utc",
            ReadFollowUp, ("$account", accountId));
    }

    public FollowUp? GetFollowUp(long accountId, long id)
    {
        using var connection = database.Open();
        return connection.Query("SELECT id, account_id, file_name, return_folder, due_utc FROM follow_ups WHERE account_id = $account AND id = $id",
            ReadFollowUp, ("$account", accountId), ("$id", id)).SingleOrDefault();
    }

    public IReadOnlyList<FollowUp> DueFollowUps()
    {
        using var connection = database.Open();
        return connection.Query("SELECT id, account_id, file_name, return_folder, due_utc FROM follow_ups WHERE due_utc <= $now ORDER BY due_utc",
            ReadFollowUp, ("$now", timeProvider.GetUtcNow().ToDbTime()));
    }

    public void RemoveFollowUp(long id)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM follow_ups WHERE id = $id", ("$id", id));
    }

    /// <summary>Where a message file of the account is now (it may have been moved), or null if it was deleted.</summary>
    public (Folder Folder, StoredMessage Message)? Locate(long accountId, string fileName)
    {
        using var connection = database.Open();
        return connection.Query(
            """
            SELECT m.id, m.folder_id, m.uid, m.flags, m.internal_date_utc, m.size, m.file_name,
                   f.id, f.account_id, f.name, f.uid_validity, f.uid_next, f.subscribed
            FROM messages m JOIN folders f ON f.id = m.folder_id WHERE f.account_id = $account AND m.file_name = $file
            ORDER BY m.id LIMIT 1
            """,
            r => (new Folder(r.GetInt64(7), r.GetInt64(8), r.GetString(9), r.GetInt64(10), r.GetInt64(11), r.GetInt64(12) != 0, 0),
                MailboxStore.ReadMessage(r)),
            ("$account", accountId), ("$file", fileName)).Select(x => ((Folder, StoredMessage)?)x).SingleOrDefault();
    }

    private static ScheduledSend ReadScheduled(Microsoft.Data.Sqlite.SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetDbTime(3), r.IsDBNull(4) ? null : r.GetString(4));

    private static FollowUp ReadFollowUp(Microsoft.Data.Sqlite.SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetDbTime(4));
}
