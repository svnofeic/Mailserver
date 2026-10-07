using Mailserver.Core;
using Mailserver.Core.Accounts;
using Mailserver.Core.Queue;
using Mailserver.Core.Security;
using Mailserver.Core.SpamLogging;
using Mailserver.Core.Storage;
using Microsoft.Extensions.Options;

namespace Mailserver.Web.Pages.Admin;

public sealed class IndexModel(
    AccountStore accounts,
    MailboxStore mailboxes,
    OutboundQueue queue,
    SpamLog log,
    CertificateProvider certificates,
    DataPaths paths,
    Mailserver.Core.Backup.BackupManager backups,
    Mailserver.Core.Antivirus.MalwareFilter malware,
    AuthThrottle throttle,
    IpRules ipRules,
    IOptions<MailserverOptions> options) : MailPageModel
{
    /// <summary>A key figure: value of the last 24 hours, daily values for the sparkline, change of the last 7 days against the 7 before.</summary>
    public sealed record Kpi(string Label, int Value, IReadOnlyList<int> Daily, double? Change, bool UpIsGood, int Color);

    public sealed record HealthItem(string Label, string State, string Detail, string? Link);

    public IReadOnlyList<DailyTraffic> Daily { get; private set; } = [];
    public IReadOnlyList<string> DayLabels => Daily.Select(d => d.Day.ToString("dd.MM.", System.Globalization.CultureInfo.InvariantCulture)).ToList();
    public IReadOnlyList<Kpi> Inbound { get; private set; } = [];
    public IReadOnlyList<Kpi> Outbound { get; private set; } = [];
    public IReadOnlyList<Kpi> Security { get; private set; } = [];
    public IReadOnlyList<SpamLogEntry> Activity { get; private set; } = [];
    public IReadOnlyList<(string Domain, int Count)> TopSpamDomains { get; private set; } = [];
    public IReadOnlyList<(string Ip, int Count)> TopRejectedIps { get; private set; } = [];
    public IReadOnlyList<HealthItem> Health { get; private set; } = [];
    public int HealthPercent => Health.Count == 0 ? 100 : 100 * Health.Count(h => h.State == "ok") / Health.Count;

    public MailserverOptions Options => options.Value;
    public int Domains { get; private set; }
    public int Mailboxes { get; private set; }
    public int Aliases { get; private set; }
    public int QueueCount { get; private set; }
    public DateTimeOffset? OldestQueued { get; private set; }
    public SpamLogStatistics Stats { get; private set; } = null!;
    public int Sent { get; private set; }
    public int OutboundFailed { get; private set; }
    public int LoginFailures { get; private set; }
    public IReadOnlyList<string> Hints { get; private set; } = [];
    public string CertificateInfo { get; private set; } = "";
    public string? CertificateWarning { get; private set; }
    public long TotalUsage { get; private set; }
    public string DataDirectory => paths.Root;
    public string? BackupWarning { get; private set; }

    public void OnGet()
    {
        Domains = accounts.ListDomains().Count;
        var all = accounts.ListAccounts();
        Mailboxes = all.Count;
        TotalUsage = all.Sum(a => mailboxes.GetUsage(a.Id));
        Aliases = accounts.ListAliases().Count;
        var queued = queue.List();
        QueueCount = queued.Count;
        OldestQueued = queued.Count == 0 ? null : queued.Min(e => e.Created);

        var day = DateTimeOffset.UtcNow.AddDays(-1);
        var entries = log.Query(new SpamLogQuery(Since: day));
        Stats = SpamLogReport.Build(entries, day, Options.Spam.JunkThreshold);
        Sent = entries.Count(e => e.Stage == SpamLogStage.Outbound && e.Action == SpamLogAction.Sent);
        OutboundFailed = entries.Count(e => e.Stage == SpamLogStage.Outbound && e.Action == SpamLogAction.Failed);
        LoginFailures = entries.Count(e => e.Stage == SpamLogStage.Auth && e.Action != SpamLogAction.Impersonated);

        var week = DateTimeOffset.UtcNow.AddDays(-7);
        var weekStats = SpamLogReport.Build(log.Query(new SpamLogQuery(Since: week)), week, Options.Spam.JunkThreshold);
        Hints = weekStats.Hints;
        TopSpamDomains = weekStats.TopSpamSenders.Take(5).ToList();
        TopRejectedIps = weekStats.TopRejectedIps.Take(5).ToList();

        Daily = log.Daily(14);
        Kpi Make(string label, int value, Func<DailyTraffic, int> select, bool upIsGood, int color)
        {
            var values = Daily.Select(select).ToList();
            var last = values.TakeLast(7).Sum();
            var before = values.SkipLast(7).TakeLast(7).Sum();
            return new Kpi(label, value, values, before == 0 ? null : 100.0 * (last - before) / before, upIsGood, color);
        }

        Inbound =
        [
            Make("Mails", Stats.Messages, d => d.Received, true, 1),
            Make("Spam", Stats.Spam, d => d.Spam, false, 4),
            Make("Spam-Quote %", Stats.Messages == 0 ? 0 : (int)Math.Round(100.0 * Stats.Spam / Stats.Messages),
                d => d.Received == 0 ? 0 : (int)Math.Round(100.0 * d.Spam / d.Received), false, 5),
        ];
        Outbound =
        [
            Make("Versendet", Sent, d => d.Sent, true, 2),
            Make("Zustellfehler", OutboundFailed, d => d.Failed, false, 4),
            new Kpi("Warteschlange", QueueCount, [], null, false, 3),
        ];
        var lockouts = throttle.ListLockouts().Count;
        var blocked = ipRules.List().Count(r => r.Kind == IpRuleKind.Block);
        Security =
        [
            Make("Abgelehnt", Stats.RejectedConnections + Stats.RejectedSenders + Stats.RejectedMessages, d => d.Rejected, true, 3),
            Make("Fehl-Logins", LoginFailures, d => d.LoginFailures, false, 4),
            new Kpi("IP-Sperren", lockouts + blocked, [], null, true, 1),
        ];

        Activity = entries.Where(e => e.Stage is SpamLogStage.Data or SpamLogStage.Connect or SpamLogStage.Sender or SpamLogStage.Outbound or SpamLogStage.Auth)
            .Take(6).ToList();

        var lastBackup = backups.LastSuccess();
        BackupWarning = !Options.Backup.Enabled ? "Die automatische Datensicherung ist ausgeschaltet."
            : lastBackup is null ? "Es gibt noch keine erfolgreiche Datensicherung."
            : DateTimeOffset.UtcNow - lastBackup.Started > TimeSpan.FromHours(50) ? $"Die letzte erfolgreiche Datensicherung ist vom {Format.Time(lastBackup.Started)}."
            : null;

        var health = new List<HealthItem>();
        var certificate = certificates.GetCertificate();
        if (certificate is null)
        {
            CertificateInfo = "keines gefunden";
            CertificateWarning = "Kein TLS-Zertifikat: Mailprogramme können sich nicht anmelden (IMAP/SMTP-Versand sind abgeschaltet).";
            health.Add(new("Zertifikat", "bad", "fehlt", "/Admin/Certificate"));
        }
        else
        {
            var daysLeft = (certificate.NotAfter.ToUniversalTime() - DateTime.UtcNow).TotalDays;
            CertificateInfo = $"{certificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.DnsName, false)}, gültig bis {Format.Time(certificate.NotAfter)} ({daysLeft:0} Tage)";
            if (daysLeft < 14)
            {
                CertificateWarning = $"Das TLS-Zertifikat läuft in {daysLeft:0} Tagen ab. Verwaltung → Zertifikat prüfen.";
            }

            health.Add(new("Zertifikat", daysLeft < 14 ? "bad" : daysLeft < 30 ? "warn" : "ok", $"noch {daysLeft:0} Tage", "/Admin/Certificate"));
        }

        health.Add(new("Sicherung", BackupWarning is null ? "ok" : lastBackup is null || !Options.Backup.Enabled ? "warn" : "bad",
            lastBackup is null ? (Options.Backup.Enabled ? "noch keine" : "aus") : Format.Time(lastBackup.Started), "/Admin/Backup"));
        health.Add(new("Virenschutz", !Options.Antivirus.Enabled ? "bad" : malware.Scanner is null ? "warn" : "ok",
            !Options.Antivirus.Enabled ? "aus" : malware.Scanner?.Name ?? "nur Anhangfilter", "/Admin/Settings"));
        var queueAge = OldestQueued is { } oldest ? DateTimeOffset.UtcNow - oldest : TimeSpan.Zero;
        health.Add(new("Warteschlange", queueAge > TimeSpan.FromHours(6) ? "bad" : queueAge > TimeSpan.FromHours(1) ? "warn" : "ok",
            QueueCount == 0 ? "leer" : $"{QueueCount} wartend", "/Admin/Queue"));
        Health = health;
    }

    /// <summary>Icon, colour and text of a log entry in the activity list.</summary>
    public static (string Icon, string Tone, string Text) Describe(SpamLogEntry e) => (e.Stage, e.Action) switch
    {
        (SpamLogStage.Data, SpamLogAction.Spam) => ("shield-alert", "warn", $"Spam von {e.HeaderFrom ?? e.MailFrom} an {e.Recipient}"),
        (SpamLogStage.Data, SpamLogAction.Rejected) => ("x-circle", "bad", $"Abgelehnt: {e.HeaderFrom ?? e.MailFrom ?? e.ClientIp} ({e.Tests ?? e.Detail})"),
        (SpamLogStage.Data, _) => ("inbox", "good", $"Mail von {e.HeaderFrom ?? e.MailFrom} an {e.Recipient}"),
        (SpamLogStage.Outbound, SpamLogAction.Sent) => ("send", "", $"Gesendet an {e.Recipient}"),
        (SpamLogStage.Outbound, _) => ("alert", "bad", $"Zustellung an {e.Recipient} fehlgeschlagen"),
        (SpamLogStage.Auth, SpamLogAction.Impersonated) => ("eye", "warn", $"Postfach {e.Recipient} von Admin geöffnet ({e.Detail})"),
        (SpamLogStage.Auth, _) => ("key", "bad", $"Fehl-Login {e.Recipient} von {e.ClientIp}"),
        _ => ("ban", "bad", $"Verbindung von {e.ClientIp} abgelehnt"),
    };

    public static string Ago(DateTimeOffset time)
    {
        var age = DateTimeOffset.UtcNow - time;
        return age.TotalMinutes < 1 ? "gerade eben"
            : age.TotalMinutes < 60 ? $"vor {(int)age.TotalMinutes} Min."
            : age.TotalHours < 24 ? $"vor {(int)age.TotalHours} Std."
            : $"vor {(int)age.TotalDays} T.";
    }
}
