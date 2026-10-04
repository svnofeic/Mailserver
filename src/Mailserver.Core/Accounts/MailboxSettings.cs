using System.Globalization;
using Mailserver.Core.Data;

namespace Mailserver.Core.Accounts;

/// <summary>Per-mailbox automatic forwarding and out-of-office reply.</summary>
public sealed record MailboxSettings(Forwarding Forwarding, AutoReply AutoReply)
{
    public static readonly MailboxSettings Default = new(Forwarding.None, AutoReply.Off);
}

/// <param name="Targets">Addresses every incoming message is forwarded to (spam excluded).</param>
/// <param name="KeepCopy">Also keep the message in this mailbox.</param>
public sealed record Forwarding(IReadOnlyList<EmailAddress> Targets, bool KeepCopy)
{
    public const int MaxTargets = 10;

    public static readonly Forwarding None = new([], true);

    public bool IsActive => Targets.Count > 0;
}

/// <param name="From">First day the reply is sent (inclusive, server time); null = immediately.</param>
/// <param name="Until">Last day the reply is sent (inclusive); null = until switched off.</param>
/// <param name="IntervalDays">Each sender gets the reply at most once in this many days.</param>
public sealed record AutoReply(bool Enabled, string Subject, string Body, DateOnly? From, DateOnly? Until, int IntervalDays)
{
    public const int MaxBodyLength = 10_000;
    public const int MaxSubjectLength = 200;

    public static readonly AutoReply Off = new(false, "", "", null, null, 7);

    public bool IsActiveOn(DateOnly day) => Enabled && (From is null || day >= From) && (Until is null || day <= Until);
}

public sealed class MailboxSettingsStore(Database database, AccountStore accounts, TimeProvider timeProvider)
{
    private DateOnly Today => DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);

    /// <summary>
    /// The settings of a mailbox. An out-of-office reply whose end date has passed is switched off here (and stays off),
    /// so it neither answers nor shows up as active anywhere.
    /// </summary>
    public MailboxSettings Get(long accountId)
    {
        var settings = Read(accountId);
        if (settings.AutoReply is { Enabled: true, Until: { } until } && until < Today)
        {
            SwitchOff(accountId);
            settings = settings with { AutoReply = settings.AutoReply with { Enabled = false } };
        }

        return settings;
    }

    private MailboxSettings Read(long accountId)
    {
        using var connection = database.Open();
        return connection.Query(
            """
            SELECT forward_to, forward_keep_copy, autoreply_enabled, autoreply_subject, autoreply_body, autoreply_from,
                   autoreply_until, autoreply_interval_days
            FROM mailbox_settings WHERE account_id = $account
            """,
            r => new MailboxSettings(
                new Forwarding(ParseTargets(r.GetString(0)), r.GetInt64(1) != 0),
                new AutoReply(r.GetInt64(2) != 0, r.GetString(3), r.GetString(4), ParseDay(r, 5), ParseDay(r, 6), (int)r.GetInt64(7))),
            ("$account", accountId)).SingleOrDefault() ?? MailboxSettings.Default;
    }

    /// <summary>Validates and stores the forwarding of a mailbox. Throws <see cref="ArgumentException"/> with a German message.</summary>
    public void SetForwarding(Account account, Forwarding forwarding)
    {
        ValidateForwarding(account, forwarding);
        var targets = forwarding.Targets.Distinct().ToList();
        Upsert(account.Id, "forward_to = $to, forward_keep_copy = $keep",
            ("$to", string.Join(", ", targets)), ("$keep", forwarding.KeepCopy || targets.Count == 0 ? 1 : 0));
    }

    public void ValidateForwarding(Account account, Forwarding forwarding)
    {
        var targets = forwarding.Targets.Distinct().ToList();
        if (targets.Count > Forwarding.MaxTargets)
        {
            throw new ArgumentException($"Höchstens {Forwarding.MaxTargets} Weiterleitungsadressen.");
        }

        if (targets.Contains(account.Address))
        {
            throw new ArgumentException("Ein Postfach kann nicht an sich selbst weiterleiten.");
        }

        if (targets.FirstOrDefault(t => accounts.IsLocalDomain(t.Domain) && accounts.Resolve(t).IsEmpty) is { Domain: not null } unknown)
        {
            throw new ArgumentException($"Das Postfach {unknown} gibt es nicht.");
        }
    }

    /// <summary>Validates and stores the out-of-office reply. Throws <see cref="ArgumentException"/> with a German message.</summary>
    public void SetAutoReply(long accountId, AutoReply reply)
    {
        ValidateAutoReply(reply);
        var previous = Read(accountId).AutoReply;
        Upsert(accountId,
            """
            autoreply_enabled = $enabled, autoreply_subject = $subject, autoreply_body = $body, autoreply_from = $from,
            autoreply_until = $until, autoreply_interval_days = $interval
            """,
            ("$enabled", reply.Enabled ? 1 : 0), ("$subject", reply.Subject.Trim()), ("$body", reply.Body.Replace("\r\n", "\n").TrimEnd()),
            ("$from", FormatDay(reply.From)), ("$until", FormatDay(reply.Until)), ("$interval", reply.IntervalDays));

        // A new absence starts with a clean slate: everyone gets the next notice again. That is the case when the reply
        // is switched off, switched on again, or gets a different period.
        if (!reply.Enabled || !previous.Enabled || reply.From != previous.From || reply.Until != previous.Until)
        {
            ClearReplyLog(accountId);
        }
    }

    private void SwitchOff(long accountId)
    {
        using (var connection = database.Open())
        {
            connection.Execute("UPDATE mailbox_settings SET autoreply_enabled = 0 WHERE account_id = $account", ("$account", accountId));
        }

        ClearReplyLog(accountId);
    }

    private void ClearReplyLog(long accountId)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM autoreply_log WHERE account_id = $account", ("$account", accountId));
    }

    public void ValidateAutoReply(AutoReply reply)
    {
        if (reply.Enabled && reply.Until is { } end && end < Today)
        {
            throw new ArgumentException("Das Bis-Datum liegt in der Vergangenheit.");
        }

        if (reply.Enabled && string.IsNullOrWhiteSpace(reply.Body))
        {
            throw new ArgumentException("Bitte einen Text für die Abwesenheitsnotiz eingeben.");
        }

        if (reply.Body.Length > AutoReply.MaxBodyLength || reply.Subject.Length > AutoReply.MaxSubjectLength)
        {
            throw new ArgumentException("Betreff oder Text der Abwesenheitsnotiz ist zu lang.");
        }

        if (reply.Subject.Any(char.IsControl))
        {
            throw new ArgumentException("Der Betreff darf keine Zeilenumbrüche enthalten.");
        }

        if (reply.From is { } from && reply.Until is { } until && until < from)
        {
            throw new ArgumentException("Das Enddatum liegt vor dem Startdatum.");
        }

        if (reply.IntervalDays is < 1 or > 30)
        {
            throw new ArgumentException("Pro Absender höchstens alle 1 bis 30 Tage antworten.");
        }
    }

    /// <summary>
    /// Records that <paramref name="sender"/> gets a reply now, unless one was sent within the interval.
    /// Returns false if the sender was answered recently.
    /// </summary>
    public bool TryRecordReply(long accountId, EmailAddress sender, int intervalDays)
    {
        var now = timeProvider.GetUtcNow();
        using var connection = database.Open();
        var last = connection.Query("SELECT sent_utc FROM autoreply_log WHERE account_id = $account AND sender = $sender",
            r => r.GetDbTime(0), ("$account", accountId), ("$sender", sender.ToString())).FirstOrDefault();
        if (last != default && now - last < TimeSpan.FromDays(intervalDays))
        {
            return false;
        }

        connection.Execute("INSERT OR REPLACE INTO autoreply_log (account_id, sender, sent_utc) VALUES ($account, $sender, $now)",
            ("$account", accountId), ("$sender", sender.ToString()), ("$now", now.ToDbTime()));
        return true;
    }

    /// <summary>Accounts with active forwarding or out-of-office reply, for the admin overview.</summary>
    public IReadOnlyDictionary<long, MailboxSettings> ListActive()
    {
        using var connection = database.Open();
        var ids = connection.Query("SELECT account_id FROM mailbox_settings WHERE forward_to <> '' OR autoreply_enabled = 1", r => r.GetInt64(0));
        return ids.ToDictionary(id => id, Get);
    }

    public static IReadOnlyList<EmailAddress> ParseTargets(string? text)
    {
        var result = new List<EmailAddress>();
        foreach (var part in (text ?? "").Split([',', ';', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!EmailAddress.TryParse(part, out var address))
            {
                throw new ArgumentException($"„{part}“ ist keine gültige E-Mail-Adresse.");
            }

            result.Add(address);
        }

        return result;
    }

    private void Upsert(long accountId, string assignments, params (string Name, object? Value)[] parameters)
    {
        using var connection = database.Open();
        connection.Execute("INSERT OR IGNORE INTO mailbox_settings (account_id) VALUES ($account)", ("$account", accountId));
        connection.Execute($"UPDATE mailbox_settings SET {assignments} WHERE account_id = $account",
            [.. parameters, ("$account", accountId)]);
    }

    private static DateOnly? ParseDay(Microsoft.Data.Sqlite.SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : DateOnly.ParseExact(reader.GetString(ordinal), "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? FormatDay(DateOnly? day) => day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
