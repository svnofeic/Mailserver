using Mailserver.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Mailserver.Core.SpamLogging;

/// <summary>Where in the processing an entry was written.</summary>
public static class SpamLogStage
{
    /// <summary>Connection checks (blocklists).</summary>
    public const string Connect = "connect";
    /// <summary>MAIL FROM (SPF).</summary>
    public const string Sender = "sender";
    /// <summary>RCPT TO (greylisting).</summary>
    public const string Recipient = "recipient";
    /// <summary>Content checks and score, one entry per message.</summary>
    public const string Data = "data";
    /// <summary>Result per recipient mailbox: folder, rules, discarded.</summary>
    public const string Delivery = "delivery";
    /// <summary>A user moved a message into or out of Junk.</summary>
    public const string Feedback = "feedback";
    /// <summary>A signed-in user sent a message (SMTP submission).</summary>
    public const string Submission = "submission";
    /// <summary>Delivery to a remote server: sent, deferred or failed.</summary>
    public const string Outbound = "outbound";
    /// <summary>Failed sign-ins and lockouts (SMTP, IMAP, web).</summary>
    public const string Auth = "auth";
}

public static class SpamLogAction
{
    public const string Rejected = "rejected";
    public const string Deferred = "deferred";
    public const string Accepted = "accepted";
    public const string Spam = "spam";
    public const string Delivered = "delivered";
    public const string Discarded = "discarded";
    public const string NotForwarded = "not-forwarded";
    public const string Forwarded = "forwarded";
    public const string AutoReplied = "auto-replied";
    /// <summary>Moved into Junk by the user: the filter missed it (false negative) unless it was already scored as spam.</summary>
    public const string MarkedSpam = "marked-spam";
    /// <summary>Moved out of Junk by the user: the filter was wrong (false positive) unless a rule put it there.</summary>
    public const string MarkedHam = "marked-ham";
    public const string Sent = "sent";
    public const string Failed = "failed";
    public const string LoginFailed = "login-failed";
    public const string LockedOut = "locked-out";
    /// <summary>An administrator opened someone's mailbox in the web interface (Recipient: the mailbox, Detail: the administrator).</summary>
    public const string Impersonated = "impersonated";
}

public sealed record SpamLogEntry
{
    public long Id { get; init; }
    public DateTimeOffset Time { get; init; }
    public string? Session { get; init; }
    public required string Stage { get; init; }
    public required string Action { get; init; }
    public string? ClientIp { get; init; }
    public string? ReverseDns { get; init; }
    public string? Helo { get; init; }
    public string? MailFrom { get; init; }
    public string? Recipient { get; init; }
    public string? HeaderFrom { get; init; }
    public string? Subject { get; init; }
    public string? MessageId { get; init; }
    public double? Score { get; init; }
    /// <summary>"SPF_FAIL=3.5,NO_REVERSE_DNS=1.5"</summary>
    public string? Tests { get; init; }
    public string? Spf { get; init; }
    public string? Dkim { get; init; }
    public string? Dmarc { get; init; }
    public string? Folder { get; init; }
    public string? Rules { get; init; }
    public string? Detail { get; init; }
}

/// <summary>Counts of one day: received (incl. spam), spam, rejected (connection, sender, content), sent, failed, failed logins.</summary>
public sealed record DailyTraffic(DateOnly Day, int Received, int Spam, int Rejected, int Sent, int Failed, int LoginFailures);

public sealed record SpamLogQuery(
    DateTimeOffset? Since = null,
    string? Stage = null,
    string? Action = null,
    string? ClientIp = null,
    string? Session = null,
    string? Search = null,
    double? MinScore = null,
    int Limit = int.MaxValue,
    string? Recipient = null);

/// <summary>
/// Persistent log of spam decisions. Writing never throws: a full disk or locked database must not stop mail delivery.
/// </summary>
public sealed class SpamLog(Database database, IOptions<MailserverOptions> options, TimeProvider timeProvider)
{
    private const int MaxTextLength = 500;
    private DateTimeOffset _nextCleanup;

    public bool Enabled => options.Value.Spam.Log.Enabled;

    public void Write(SpamLogEntry entry)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            var now = timeProvider.GetUtcNow();
            using var connection = database.Open();
            connection.Execute(
                """
                INSERT INTO spam_log (time_utc, session, stage, action, client_ip, reverse_dns, helo, mail_from, recipient, header_from,
                                      subject, message_id, score, tests, spf, dkim, dmarc, folder, rules, detail)
                VALUES ($time, $session, $stage, $action, $ip, $ptr, $helo, $from, $rcpt, $hfrom, $subject, $mid, $score, $tests, $spf,
                        $dkim, $dmarc, $folder, $rules, $detail)
                """,
                ("$time", (entry.Time == default ? now : entry.Time).ToDbTime()), ("$session", entry.Session), ("$stage", entry.Stage),
                ("$action", entry.Action), ("$ip", entry.ClientIp), ("$ptr", Trim(entry.ReverseDns)), ("$helo", Trim(entry.Helo)),
                ("$from", Trim(entry.MailFrom)), ("$rcpt", Trim(entry.Recipient)), ("$hfrom", Trim(entry.HeaderFrom)),
                ("$subject", options.Value.Spam.Log.IncludeSubject ? Trim(entry.Subject) : null), ("$mid", Trim(entry.MessageId)),
                ("$score", entry.Score), ("$tests", entry.Tests), ("$spf", entry.Spf), ("$dkim", Trim(entry.Dkim)),
                ("$dmarc", entry.Dmarc), ("$folder", Trim(entry.Folder)), ("$rules", Trim(entry.Rules)), ("$detail", Trim(entry.Detail)));

            if (now >= _nextCleanup)
            {
                _nextCleanup = now.AddHours(1);
                Cleanup(connection, now);
            }
        }
        catch (SqliteException)
        {
            // Logging is best effort.
        }
    }

    /// <summary>Failed sign-in (or the lockout it caused) for SMTP, IMAP or the web interface.</summary>
    public void WriteAuthFailure(string protocol, string user, System.Net.IPAddress? ip, bool lockedOut) =>
        Write(new SpamLogEntry
        {
            Stage = SpamLogStage.Auth,
            Action = lockedOut ? SpamLogAction.LockedOut : SpamLogAction.LoginFailed,
            ClientIp = ip?.ToString(),
            Recipient = user,
            Detail = protocol,
        });

    /// <summary>
    /// Mail traffic per local calendar day for the last <paramref name="days"/> days (today included), oldest first. Counted in
    /// the database per hour, so this stays fast with a large log.
    /// </summary>
    public IReadOnlyList<DailyTraffic> Daily(int days)
    {
        var today = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
        var first = today.AddDays(1 - days);
        var since = new DateTimeOffset(first.ToDateTime(TimeOnly.MinValue), timeProvider.GetLocalNow().Offset).AddHours(-14);
        var result = Enumerable.Range(0, days).ToDictionary(i => first.AddDays(i), _ => new int[6]);
        using var connection = database.Open();
        var rows = connection.Query(
            "SELECT substr(time_utc, 1, 13), stage, action, COUNT(*) FROM spam_log WHERE time_utc >= $since GROUP BY 1, 2, 3",
            r => (Hour: r.GetString(0), Stage: r.GetString(1), Action: r.GetString(2), Count: r.GetInt32(3)), ("$since", since.ToDbTime()));
        foreach (var row in rows)
        {
            var utc = DateTime.SpecifyKind(DateTime.ParseExact(row.Hour, "yyyy-MM-dd'T'HH", System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);
            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, timeProvider.LocalTimeZone));
            if (!result.TryGetValue(day, out var counts))
            {
                continue;
            }

            var index = (row.Stage, row.Action) switch
            {
                (SpamLogStage.Data, SpamLogAction.Accepted) => 0,
                (SpamLogStage.Data, SpamLogAction.Spam) => 1,
                (SpamLogStage.Connect or SpamLogStage.Sender or SpamLogStage.Data, SpamLogAction.Rejected) => 2,
                (SpamLogStage.Outbound, SpamLogAction.Sent) => 3,
                (SpamLogStage.Outbound, SpamLogAction.Failed) => 4,
                (SpamLogStage.Auth, SpamLogAction.LoginFailed or SpamLogAction.LockedOut) => 5,
                _ => -1,
            };
            if (index >= 0)
            {
                counts[index] += row.Count;
            }
        }

        return result.OrderBy(d => d.Key)
            .Select(d => new DailyTraffic(d.Key, d.Value[0] + d.Value[1], d.Value[1], d.Value[2], d.Value[3], d.Value[4], d.Value[5]))
            .ToList();
    }

    public IReadOnlyList<SpamLogEntry> Query(SpamLogQuery query)
    {
        var where = new List<string>();
        var parameters = new List<(string, object?)>();
        void Add(string condition, string name, object? value)
        {
            where.Add(condition);
            parameters.Add((name, value));
        }

        if (query.Since is { } since) Add("time_utc >= $since", "$since", since.ToDbTime());
        if (query.Stage is { } stage) Add("stage = $stage", "$stage", stage);
        if (query.Action is { } action) Add("action = $action", "$action", action);
        if (query.ClientIp is { } ip) Add("client_ip = $ip", "$ip", ip);
        if (query.Session is { } session) Add("session = $session", "$session", session);
        if (query.MinScore is { } min) Add("score >= $min", "$min", min);
        if (query.Recipient is { } recipient) Add("(recipient = $rcpt OR recipient LIKE $rcptlike)", "$rcpt", recipient);
        if (query.Recipient is { } r2) parameters.Add(("$rcptlike", $"%{r2}%"));
        if (query.Search is { Length: > 0 } search)
        {
            Add("(mail_from LIKE $search OR header_from LIKE $search OR recipient LIKE $search OR subject LIKE $search OR message_id LIKE $search)",
                "$search", $"%{search}%");
        }

        var sql = $"""
            SELECT {Columns}
            FROM spam_log {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")}
            ORDER BY time_utc DESC, id DESC LIMIT $limit
            """;
        parameters.Add(("$limit", query.Limit));

        using var connection = database.Open();
        return connection.Query(sql, Read, parameters.ToArray());
    }

    /// <summary>
    /// Received messages with their checks: delivery entries (optionally for one mailbox) joined with the "data" entry of the
    /// same session, which carries sender, subject, score and tests.
    /// </summary>
    public IReadOnlyList<(SpamLogEntry Delivery, SpamLogEntry? Message)> Deliveries(string? recipient, DateTimeOffset since, int limit)
    {
        var deliveries = Query(new SpamLogQuery(Since: since, Stage: SpamLogStage.Delivery, Limit: limit, Recipient: recipient))
            .Where(d => recipient is null || string.Equals(d.Recipient, recipient, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var sessions = deliveries.Select(d => d.Session).OfType<string>().Distinct().ToList();
        var messages = new Dictionary<string, SpamLogEntry>();
        using (var connection = database.Open())
        {
            foreach (var chunk in sessions.Chunk(200))
            {
                var names = chunk.Select((_, i) => $"$s{i}").ToList();
                var rows = connection.Query(
                    $"SELECT {Columns} FROM spam_log WHERE stage = 'data' AND session IN ({string.Join(',', names)})",
                    Read, chunk.Select((value, i) => ($"$s{i}", (object?)value)).ToArray());
                foreach (var row in rows)
                {
                    messages[row.Session!] = row;
                }
            }
        }

        return deliveries.Select(d => (d, d.Session is null ? null : messages.GetValueOrDefault(d.Session))).ToList();
    }

    /// <summary>Deletes entries older than the retention period; returns the number removed.</summary>
    public int Cleanup()
    {
        using var connection = database.Open();
        return Cleanup(connection, timeProvider.GetUtcNow());
    }

    private int Cleanup(SqliteConnection connection, DateTimeOffset now) =>
        connection.Execute("DELETE FROM spam_log WHERE time_utc < $cutoff",
            ("$cutoff", now.AddDays(-Math.Max(1, options.Value.Spam.Log.RetentionDays)).ToDbTime()));

    private const string Columns =
        "id, time_utc, session, stage, action, client_ip, reverse_dns, helo, mail_from, recipient, header_from, subject, message_id, " +
        "score, tests, spf, dkim, dmarc, folder, rules, detail";

    private static string? Trim(string? value) =>
        value is null ? null : value.Length <= MaxTextLength ? value : value[..MaxTextLength];

    private static SpamLogEntry Read(SqliteDataReader r)
    {
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        return new SpamLogEntry
        {
            Id = r.GetInt64(0), Time = r.GetDbTime(1), Session = S(2), Stage = r.GetString(3), Action = r.GetString(4),
            ClientIp = S(5), ReverseDns = S(6), Helo = S(7), MailFrom = S(8), Recipient = S(9), HeaderFrom = S(10), Subject = S(11),
            MessageId = S(12), Score = r.IsDBNull(13) ? null : r.GetDouble(13), Tests = S(14), Spf = S(15), Dkim = S(16),
            Dmarc = S(17), Folder = S(18), Rules = S(19), Detail = S(20),
        };
    }
}
