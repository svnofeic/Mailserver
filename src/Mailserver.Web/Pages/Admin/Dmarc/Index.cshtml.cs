using System.Collections.Concurrent;
using System.Net;
using Mailserver.Core;
using Mailserver.Core.Accounts;
using Mailserver.Core.Dmarc;
using Mailserver.Core.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Mailserver.Web.Pages.Admin.Dmarc;

/// <summary>Evaluation of the DMARC reports: who sends mail in the name of our domains, and does SPF/DKIM pass.</summary>
public sealed class IndexModel(DmarcReports reports, AccountStore accounts, MailboxStore mailboxes, IOptions<MailserverOptions> options) : MailPageModel
{
    private static readonly ConcurrentDictionary<string, string?> HostNames = new();

    public IReadOnlyList<DmarcDomainSummary> Domains { get; private set; } = [];
    public IReadOnlyList<TlsDomainSummary> Tls { get; private set; } = [];

    /// <summary>German explanation of a TLS-RPT result type (RFC 8460 section 4.3).</summary>
    public static string TlsResult(string type) => type switch
    {
        "starttls-not-supported" => "STARTTLS nicht angeboten",
        "certificate-host-mismatch" => "Zertifikat passt nicht zum Namen",
        "certificate-expired" => "Zertifikat abgelaufen",
        "certificate-not-trusted" => "Zertifikat nicht vertrauenswürdig",
        "validation-failure" => "Prüfung fehlgeschlagen",
        "sts-policy-fetch-error" => "MTA-STS-Datei nicht abrufbar",
        "sts-policy-invalid" => "MTA-STS-Datei ungültig",
        "sts-webpki-invalid" => "Zertifikat von mta-sts.<domain> ungültig",
        "tlsa-invalid" or "dnssec-invalid" or "dane-required" => "DANE/DNSSEC-Fehler",
        _ => type,
    };
    public IReadOnlyList<string> DomainsWithoutReports { get; private set; } = [];
    public IReadOnlySet<string> ServerIps { get; private set; } = new HashSet<string>();
    public IReadOnlyDictionary<string, string?> Names { get; private set; } = new Dictionary<string, string?>();
    [BindProperty(SupportsGet = true)] public int Days { get; set; } = 30;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Days = Days is 7 or 30 or 90 or 365 ? Days : 30;
        Domains = reports.Summary(DateTimeOffset.UtcNow.AddDays(-Days));
        Tls = reports.TlsSummary(DateTimeOffset.UtcNow.AddDays(-Days));
        DomainsWithoutReports = accounts.ListDomains().Select(d => d.Name)
            .Where(d => !Domains.Any(s => s.Domain.Equals(d, StringComparison.OrdinalIgnoreCase))).ToList();
        try
        {
            ServerIps = (await Dns.GetHostAddressesAsync(options.Value.Hostname, cancellationToken))
                .Select(a => (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).ToString()).ToHashSet();
        }
        catch (System.Net.Sockets.SocketException)
        {
        }

        Names = await ReverseNamesAsync(Domains.SelectMany(d => d.Sources).Select(s => s.Ip).Distinct().Take(100), cancellationToken);
    }

    /// <summary>What to do next, from the numbers of a domain.</summary>
    public static (string Tone, string Text) Advice(DmarcDomainSummary domain)
    {
        var failingSources = domain.Sources.Where(s => s.Failed > 0 && s.Failed * 100 >= s.Messages * 10).ToList();
        if (domain.Messages == 0)
        {
            return ("", "Noch keine Mails in den Berichten.");
        }

        if (domain.PassRate >= 98 && failingSources.Count == 0)
        {
            return domain.Policy switch
            {
                "none" => ("good", "Alle Absender bestehen SPF/DKIM. Sie können den DMARC-Eintrag auf p=quarantine stellen (gefälschte Mails landen dann im Spam) – und nach einigen Wochen ohne Probleme auf p=reject."),
                "quarantine" => ("good", "Alles in Ordnung. Wenn das einige Wochen so bleibt, kann der DMARC-Eintrag auf p=reject (gefälschte Mails werden abgewiesen)."),
                _ => ("good", "Alles in Ordnung – gefälschte Mails in Ihrem Namen werden abgewiesen."),
            };
        }

        return ("warn", "Ein Teil der Mails besteht SPF/DKIM nicht. Prüfen Sie die rot markierten Absender: Sind es Dienste, die in Ihrem Namen senden " +
                        "(Newsletter, Shop, Kontaktformular), müssen sie in den SPF-Eintrag oder mit DKIM signieren. Sind es fremde Server, versucht jemand, " +
                        "Ihre Adresse zu fälschen. Den DMARC-Eintrag erst verschärfen, wenn die eigenen Dienste alle bestehen.");
    }

    /// <summary>Reads the reports already in the administrators' mailboxes (e.g. from before this evaluation existed).</summary>
    public IActionResult OnPostImport()
    {
        var imported = 0;
        var scanned = 0;
        foreach (var account in accounts.ListAccounts().Where(a => a.IsAdmin && a.Enabled))
        {
            foreach (var folder in mailboxes.ListFolders(account.Id))
            {
                foreach (var message in mailboxes.ListMessages(folder.Id))
                {
                    try
                    {
                        using var stream = mailboxes.OpenMessage(message);
                        var headers = HeaderList.Load(stream);
                        var subject = headers[HeaderId.Subject] ?? "";
                        if (!subject.Contains("Report Domain", StringComparison.OrdinalIgnoreCase) && !subject.Contains("DMARC", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        scanned++;
                        stream.Position = 0;
                        var (isReport, added) = reports.Import(MimeMessage.Load(stream));
                        scanned += isReport ? 0 : -1;
                        imported += added;
                    }
                    catch (Exception ex) when (ex is IOException or FormatException)
                    {
                    }
                }
            }
        }

        Message = scanned == 0 ? "In den Postfächern der Administratoren wurden keine DMARC-Berichte gefunden."
            : $"{scanned} Bericht-Mail(s) gefunden, {imported} neue Berichte übernommen (bekannte werden nicht doppelt gezählt).";
        return RedirectToPage(new { Days });
    }

    private static async Task<IReadOnlyDictionary<string, string?>> ReverseNamesAsync(IEnumerable<string> ips, CancellationToken cancellationToken)
    {
        var missing = ips.Where(ip => !HostNames.ContainsKey(ip)).ToList();
        var lookups = missing.Select(async ip =>
        {
            try
            {
                var lookup = Dns.GetHostEntryAsync(ip, cancellationToken);
                var done = await Task.WhenAny(lookup, Task.Delay(TimeSpan.FromSeconds(3), cancellationToken));
                HostNames[ip] = done == lookup && lookup.IsCompletedSuccessfully && lookup.Result.HostName != ip ? lookup.Result.HostName : null;
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException or OperationCanceledException)
            {
                HostNames[ip] = null;
            }
        });
        await Task.WhenAll(lookups);
        return HostNames;
    }
}
