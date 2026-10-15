using System.Text;
using Mailserver.Core;
using Mailserver.Core.Accounts;
using Mailserver.Core.Backup;
using Mailserver.Core.Dmarc;
using Mailserver.Core.Queue;
using Mailserver.Core.Security;
using Mailserver.Core.SpamLogging;
using Mailserver.Core.Storage;
using Microsoft.Extensions.Options;

namespace Mailserver.AntiSpam.Diagnostics;

/// <summary>
/// The weekly status mail for the administrators (traffic, problems found by the diagnosis, DMARC, storage) and the quick
/// check behind the status address for outside monitoring.
/// </summary>
public sealed class StatusReport(
    ServerDiagnostics diagnostics,
    SpamLog log,
    OutboundQueue queue,
    AccountStore accounts,
    MailboxStore mailboxes,
    DmarcReports dmarc,
    BackupManager backups,
    CertificateProvider certificates,
    DataPaths paths,
    IOptions<MailserverOptions> options,
    TimeProvider timeProvider)
{
    public sealed record Weekly(string Subject, string Text, int Problems);

    public async Task<Weekly> BuildWeeklyAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var since = now.AddDays(-7);
        var settings = options.Value;
        var text = new StringBuilder();
        var de = System.Globalization.CultureInfo.InvariantCulture;
        string Day(DateTimeOffset value) => value.ToLocalTime().ToString("dd.MM.yyyy", de);

        // Problems first: they are what the report is for.
        var checks = await diagnostics.RunAsync(cancellationToken);
        var problems = checks.Where(c => c.Status is CheckStatus.Error or CheckStatus.Warning).OrderByDescending(c => c.Status).ToList();
        text.AppendLine($"Statusbericht für {settings.Hostname}, {Day(since)} bis {Day(now)}").AppendLine();
        if (problems.Count == 0)
        {
            text.AppendLine("✔ Keine Probleme: Die Diagnose (DNS, Sperrlisten, Zertifikat, Ports, Sicherung, Speicherplatz) ist in Ordnung.");
        }
        else
        {
            text.AppendLine($"Zu prüfen ({problems.Count}):");
            foreach (var check in problems)
            {
                text.AppendLine($"  {(check.Status == CheckStatus.Error ? "✖" : "⚠")} {check.Group} – {check.Title}: {check.Detail}");
                if (check.Hint is { } hint)
                {
                    text.AppendLine($"      {hint}");
                }
            }
        }

        // Traffic
        var entries = log.Query(new SpamLogQuery(Since: since));
        var stats = SpamLogReport.Build(entries, since, settings.Spam.JunkThreshold);
        var sent = entries.Count(e => e.Stage == SpamLogStage.Outbound && e.Action == SpamLogAction.Sent);
        var failed = entries.Count(e => e.Stage == SpamLogStage.Outbound && e.Action == SpamLogAction.Failed);
        var logins = entries.Count(e => e.Stage == SpamLogStage.Auth && e.Action is SpamLogAction.LoginFailed or SpamLogAction.LockedOut);
        text.AppendLine().AppendLine("Mailverkehr der letzten 7 Tage:")
            .AppendLine($"  Empfangen: {stats.Messages}, davon Spam: {stats.Spam} ({(stats.Messages == 0 ? 0 : 100 * stats.Spam / stats.Messages)} %)")
            .AppendLine($"  Abgewiesen: {stats.RejectedConnections + stats.RejectedSenders + stats.RejectedMessages} (Verbindungen, Absender, Mails)")
            .AppendLine($"  Gesendet: {sent}, Zustellfehler: {failed}")
            .AppendLine($"  Fehlgeschlagene Anmeldungen: {logins}");
        if (stats.TopSpamSenders.Count > 0)
        {
            text.AppendLine($"  Häufigste Spam-Absender: {string.Join(", ", stats.TopSpamSenders.Take(5).Select(s => $"{s.Domain} ({s.Count})"))}");
        }

        var queued = queue.List();
        text.AppendLine($"  Warteschlange: {(queued.Count == 0 ? "leer" : $"{queued.Count} Mail(s), älteste vom {Day(queued.Min(q => q.Created))}")}");

        // DMARC
        var reports = dmarc.Summary(since);
        text.AppendLine().AppendLine("DMARC (Berichte der großen Anbieter):");
        if (reports.Count == 0)
        {
            text.AppendLine("  keine Berichte eingegangen");
        }

        foreach (var domain in reports)
        {
            var foreign = domain.Sources.Where(s => s.Failed > 0).Sum(s => s.Failed);
            text.AppendLine($"  {domain.Domain}: {domain.Messages} Mails, {Math.Round(domain.PassRate, 1).ToString(de)} % bestanden" +
                            (foreign > 0 ? $", {foreign} nicht bestanden" : "") + $" (p={domain.Policy})");
        }

        var tls = dmarc.TlsSummary(since);
        if (tls.Count > 0)
        {
            text.AppendLine().AppendLine("TLS-Berichte (MTA-STS):");
            foreach (var domain in tls)
            {
                text.AppendLine($"  {domain.Domain}: {domain.Successful} verschlüsselt zugestellt, {domain.Failed} fehlgeschlagen" +
                                (domain.Failures.Count > 0 ? $" ({string.Join(", ", domain.Failures.Select(f => $"{f.Type} {f.Count}"))})" : ""));
            }
        }

        // Storage
        var usage = accounts.ListAccounts().Select(a => (a.Address, Used: mailboxes.GetUsage(a.Id), a.QuotaBytes)).OrderByDescending(a => a.Used).ToList();
        text.AppendLine().AppendLine($"Speicher: {BackupManager.Format(usage.Sum(u => u.Used))} in {usage.Count} Postfächern");
        foreach (var (address, used, quota) in usage.Take(5))
        {
            text.AppendLine($"  {address}: {BackupManager.Format(used)}{(quota > 0 ? $" von {BackupManager.Format(quota)} ({100 * used / quota} %)" : "")}");
        }

        var backup = backups.LastSuccess();
        text.AppendLine().AppendLine($"Letzte erfolgreiche Datensicherung: {(backup is null ? "keine" : Day(backup.Started))}");
        var web = settings.Web.PublicUrl is { Length: > 0 } url ? url.TrimEnd('/')
            : $"https://{settings.Hostname}{(settings.Web.HttpsPort == 443 ? "" : $":{settings.Web.HttpsPort}")}";
        text.AppendLine().AppendLine($"Details: {web}/Admin");
        var subject = problems.Count == 0 ? "Wochenbericht: alles in Ordnung" : $"Wochenbericht: {problems.Count} Punkt(e) zu prüfen";
        return new Weekly(subject, text.ToString(), problems.Count);
    }

    /// <summary>
    /// Quick check for the status address (no DNS lookups, answers within a second): empty if everything works.
    /// </summary>
    public IReadOnlyList<string> QuickProblems()
    {
        var problems = new List<string>();
        var now = timeProvider.GetUtcNow();
        try
        {
            _ = accounts.ListDomains();
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            problems.Add("Datenbank nicht erreichbar");
            return problems;
        }

        var queued = queue.List();
        if (queued.Count > 0 && now - queued.Min(q => q.Created) > TimeSpan.FromHours(6))
        {
            problems.Add($"Warteschlange hängt ({queued.Count} Mails)");
        }

        if (certificates.GetCertificate() is not { } certificate)
        {
            problems.Add("kein TLS-Zertifikat");
        }
        else if (certificate.NotAfter.ToUniversalTime() - now.UtcDateTime < TimeSpan.FromDays(7))
        {
            problems.Add("TLS-Zertifikat läuft ab");
        }

        try
        {
            if (new DriveInfo(Path.GetPathRoot(paths.Root)!).AvailableFreeSpace < 1024L * 1024 * 1024)
            {
                problems.Add("weniger als 1 GB Speicher frei");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }

        if (options.Value.Backup.Enabled && backups.LastSuccess() is { } last && now - last.Started > TimeSpan.FromHours(50))
        {
            problems.Add("Datensicherung überfällig");
        }

        return problems;
    }
}
