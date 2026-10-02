using Mailserver.Core.Data;

namespace Mailserver.Core.Queue;

public sealed record QueueEntry(long Id, string MessageFile, string Sender, EmailAddress Recipient, DateTimeOffset Created,
    DateTimeOffset NextAttempt, int Attempts, string? LastError);

/// <summary>All recipients of one message at one destination domain, delivered in a single SMTP transaction.</summary>
public sealed record DeliveryBatch(string MessageFile, string Sender, string Domain, IReadOnlyList<QueueEntry> Entries);

/// <summary>
/// Persistent outbound queue. Message bodies are stored once in data/queue/, one row per recipient.
/// </summary>
public sealed class OutboundQueue(Database database, DataPaths paths)
{
    private static readonly TimeSpan[] RetrySchedule =
    [
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(4),
    ];

    private readonly SemaphoreSlim _workAvailable = new(0, 1);

    /// <summary>Completes when new messages were queued or the timeout elapsed.</summary>
    public async Task WaitForWorkAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        await _workAvailable.WaitAsync(timeout, cancellationToken);

    /// <param name="sender">Envelope sender; an empty string is the null sender used for bounces.</param>
    public async Task EnqueueAsync(ReadOnlyMemory<byte> message, string sender, IEnumerable<EmailAddress> recipients,
        CancellationToken cancellationToken = default)
    {
        var recipientList = recipients.Distinct().ToList();
        if (recipientList.Count == 0)
        {
            return;
        }

        var fileName = $"{Guid.NewGuid():N}.eml";
        var path = Path.Combine(paths.QueueRoot, fileName);
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await stream.WriteAsync(message, cancellationToken);
            stream.Flush(flushToDisk: true);
        }

        var now = DateTimeOffset.UtcNow.ToDbTime();
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var recipient in recipientList)
        {
            connection.Execute(
                """
                INSERT INTO queue (message_file, sender, recipient, recipient_domain, created_utc, next_attempt_utc)
                VALUES ($file, $sender, $recipient, $domain, $now, $now)
                """,
                transaction, ("$file", fileName), ("$sender", sender), ("$recipient", recipient.ToString()),
                ("$domain", recipient.Domain), ("$now", now));
        }

        transaction.Commit();
        SignalWork();
    }

    public IReadOnlyList<DeliveryBatch> GetDueBatches(DateTimeOffset now, int maxEntries = 500)
    {
        using var connection = database.Open();
        var entries = connection.Query(
            $"{SelectEntries} WHERE next_attempt_utc <= $now ORDER BY next_attempt_utc LIMIT $limit",
            ReadEntry, ("$now", now.ToDbTime()), ("$limit", maxEntries));

        return entries
            .GroupBy(e => (e.MessageFile, e.Sender, e.Recipient.Domain))
            .Select(g => new DeliveryBatch(g.Key.MessageFile, g.Key.Sender, g.Key.Domain, g.ToList()))
            .ToList();
    }

    public IReadOnlyList<QueueEntry> List()
    {
        using var connection = database.Open();
        return connection.Query($"{SelectEntries} ORDER BY created_utc", ReadEntry);
    }

    public Stream OpenMessage(string messageFile) =>
        new FileStream(Path.Combine(paths.QueueRoot, messageFile), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

    /// <summary>Removes entries after successful delivery or a final failure.</summary>
    public void Complete(IEnumerable<QueueEntry> entries)
    {
        var files = new HashSet<string>();
        using (var connection = database.Open())
        using (var transaction = connection.BeginTransaction())
        {
            foreach (var entry in entries)
            {
                connection.Execute("DELETE FROM queue WHERE id = $id", transaction, ("$id", entry.Id));
                files.Add(entry.MessageFile);
            }

            transaction.Commit();
        }

        DeleteUnreferencedFiles(files);
    }

    /// <summary>Schedules the next attempt according to the retry schedule.</summary>
    public void Defer(IEnumerable<QueueEntry> entries, string error)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var entry in entries)
        {
            var delay = RetrySchedule[Math.Min(entry.Attempts, RetrySchedule.Length - 1)];
            connection.Execute(
                "UPDATE queue SET attempts = attempts + 1, last_error = $error, next_attempt_utc = $next WHERE id = $id",
                transaction, ("$error", error), ("$next", DateTimeOffset.UtcNow.Add(delay).ToDbTime()), ("$id", entry.Id));
        }

        transaction.Commit();
    }

    /// <summary>Makes all entries due immediately (admin "queue retry").</summary>
    public int RetryAll()
    {
        using var connection = database.Open();
        var count = connection.Execute("UPDATE queue SET next_attempt_utc = $now", ("$now", DateTimeOffset.UtcNow.ToDbTime()));
        SignalWork();
        return count;
    }

    private void SignalWork()
    {
        try
        {
            _workAvailable.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled.
        }
    }

    private void DeleteUnreferencedFiles(IEnumerable<string> files)
    {
        using var connection = database.Open();
        foreach (var file in files)
        {
            if (connection.Scalar("SELECT 1 FROM queue WHERE message_file = $file LIMIT 1", ("$file", file)) is null)
            {
                File.Delete(Path.Combine(paths.QueueRoot, file));
            }
        }
    }

    private const string SelectEntries =
        "SELECT id, message_file, sender, recipient, created_utc, next_attempt_utc, attempts, last_error FROM queue";

    private static QueueEntry ReadEntry(Microsoft.Data.Sqlite.SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetString(2), EmailAddress.Parse(r.GetString(3)), r.GetDbTime(4), r.GetDbTime(5),
            r.GetInt32(6), r.IsDBNull(7) ? null : r.GetString(7));
}
