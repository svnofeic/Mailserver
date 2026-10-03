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
    IOptions<MailserverOptions> options) : MailPageModel
{
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
        LoginFailures = entries.Count(e => e.Stage == SpamLogStage.Auth);

        var week = DateTimeOffset.UtcNow.AddDays(-7);
        Hints = SpamLogReport.Build(log.Query(new SpamLogQuery(Since: week)), week, Options.Spam.JunkThreshold).Hints;

        var certificate = certificates.GetCertificate();
        if (certificate is null)
        {
            CertificateInfo = "keines gefunden";
            CertificateWarning = "Kein TLS-Zertifikat: Mailprogramme können sich nicht anmelden (IMAP/SMTP-Versand sind abgeschaltet).";
        }
        else
        {
            var daysLeft = (certificate.NotAfter.ToUniversalTime() - DateTime.UtcNow).TotalDays;
            CertificateInfo = $"{certificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.DnsName, false)}, gültig bis {Format.Time(certificate.NotAfter)} ({daysLeft:0} Tage)";
            if (daysLeft < 14)
            {
                CertificateWarning = $"Das TLS-Zertifikat läuft in {daysLeft:0} Tagen ab. Erneuerung (win-acme) prüfen.";
            }
        }
    }
}
