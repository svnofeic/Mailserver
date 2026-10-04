using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Mailserver.AntiSpam.Checks;
using Mailserver.AntiSpam.Dns;
using Mailserver.Core;
using Mailserver.Core.Accounts;
using Mailserver.Core.Antivirus;
using Mailserver.Core.Backup;
using Mailserver.Core.Dkim;
using Mailserver.Core.Queue;
using Mailserver.Core.Security;
using Microsoft.Extensions.Options;

namespace Mailserver.AntiSpam.Diagnostics;

public enum CheckStatus
{
    Ok,
    Info,
    Warning,
    Error,
}

public sealed record DiagnosticCheck(string Group, string Title, CheckStatus Status, string Detail, string? Hint = null);

/// <summary>
/// Checks everything a mail server needs to deliver and receive reliably: DNS of all domains (MX, SPF, DKIM, DMARC),
/// reverse DNS and blacklists of the server address, open ports, outgoing port 25, certificate, queue, disk space,
/// backup and virus scanner. Used by the web interface (Admin → Diagnose) and mailadmin diagnose.
/// </summary>
public sealed class ServerDiagnostics(
    IDnsResolver dns,
    AccountStore accounts,
    DkimKeyStore dkim,
    OutboundQueue queue,
    CertificateProvider certificates,
    BackupManager backups,
    MalwareFilter malware,
    DataPaths paths,
    IOptions<MailserverOptions> options,
    TimeProvider timeProvider)
{
    public const string ServerGroup = "Server";
    public const string NetworkGroup = "Netzwerk und Ruf";

    /// <summary>Blacklists that large mail providers use; a listing there means mails are rejected or land in spam.</summary>
    public static readonly IReadOnlyList<(string Zone, string Name, string Lookup)> Blocklists =
    [
        ("zen.spamhaus.org", "Spamhaus", "https://check.spamhaus.org/results/?query={0}"),
        ("bl.spamcop.net", "SpamCop", "https://www.spamcop.net/bl.shtml?{0}"),
        ("b.barracudacentral.org", "Barracuda", "https://www.barracudacentral.org/lookups/lookup-reputation"),
        ("psbl.surriel.com", "PSBL", "https://psbl.org/listing?ip={0}"),
    ];

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Opens a TCP connection; returns null on success or the error. Replaceable in tests.</summary>
    public Func<string, int, CancellationToken, Task<string?>> Connect { get; set; } = TryConnectAsync;

    /// <summary>Asks GitHub whether a newer release exists; null = no update or unknown. Replaceable in tests.</summary>
    public Func<CancellationToken, Task<string?>> LatestRelease { get; set; } = ReadLatestReleaseAsync;

    public async Task<IReadOnlyList<DiagnosticCheck>> RunAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var addresses = await dns.GetAddressesAsync(settings.Hostname, cancellationToken);
        var serverIps = addresses.Records.Select(a => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).Distinct().ToList();

        var tasks = new List<Task<IReadOnlyList<DiagnosticCheck>>>
        {
            Task.FromResult(CheckServer()),
            CheckUpdateAsync(cancellationToken),
            CheckPortsAsync(cancellationToken),
            CheckOutboundAsync(cancellationToken),
            CheckAddressAsync(addresses, serverIps, cancellationToken),
        };
        tasks.AddRange(accounts.ListDomains().Select(domain => CheckDomainAsync(domain, serverIps, cancellationToken)));
        var results = await Task.WhenAll(tasks);
        return results.SelectMany(r => r).ToList();
    }

    private IReadOnlyList<DiagnosticCheck> CheckServer()
    {
        var settings = options.Value;
        var checks = new List<DiagnosticCheck>
        {
            new(ServerGroup, "Version", CheckStatus.Info, $"{BuildInfo.Version} auf {System.Runtime.InteropServices.RuntimeInformation.OSDescription}"),
        };

        // Certificate
        var certificate = certificates.GetCertificate();
        if (certificate is null)
        {
            checks.Add(new(ServerGroup, "TLS-Zertifikat", CheckStatus.Error, $"Kein gültiges Zertifikat für {settings.Hostname}.",
                "Ohne Zertifikat bleiben SMTP-Versand (587/465) und IMAPS (993) geschlossen. Verwaltung → Zertifikat → Let's Encrypt."));
        }
        else
        {
            var days = (int)Math.Floor((certificate.NotAfter.ToUniversalTime() - timeProvider.GetUtcNow().UtcDateTime).TotalDays);
            var names = string.Join(", ", certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().SelectMany(e => e.EnumerateDnsNames()));
            checks.Add(new(ServerGroup, "TLS-Zertifikat", days < 14 ? CheckStatus.Error : days < 30 ? CheckStatus.Warning : CheckStatus.Ok,
                $"gültig bis {certificate.NotAfter:dd.MM.yyyy} (noch {days} Tage) für {names}",
                days < 30 ? "Läuft bald ab: Verwaltung → Zertifikat prüfen (Let's Encrypt verlängert 30 Tage vor Ablauf)." : null));
        }

        // Disk space
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(paths.Root)!);
            var free = drive.AvailableFreeSpace;
            var gb = free / 1024.0 / 1024 / 1024;
            checks.Add(new(ServerGroup, "Speicherplatz", gb < 1 ? CheckStatus.Error : gb < 5 ? CheckStatus.Warning : CheckStatus.Ok,
                $"{BackupManager.Format(free)} frei auf {drive.Name}",
                gb < 5 ? "Wird der Platz knapp, können keine Mails mehr angenommen werden." : null));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            checks.Add(new(ServerGroup, "Speicherplatz", CheckStatus.Warning, $"nicht ermittelbar: {ex.Message}"));
        }

        // Queue
        var entries = queue.List();
        var now = timeProvider.GetUtcNow();
        if (entries.Count == 0)
        {
            checks.Add(new(ServerGroup, "Warteschlange", CheckStatus.Ok, "leer – alle Mails sind zugestellt"));
        }
        else
        {
            var oldest = entries.Min(e => e.Created);
            var failing = entries.Where(e => e.LastError is not null).ToList();
            var status = now - oldest > TimeSpan.FromHours(6) ? CheckStatus.Error : now - oldest > TimeSpan.FromHours(1) ? CheckStatus.Warning : CheckStatus.Ok;
            var detail = $"{entries.Count} Mail(s) warten, die älteste seit {(int)(now - oldest).TotalMinutes} Minuten";
            checks.Add(new(ServerGroup, "Warteschlange", status, detail,
                failing.Count > 0 ? $"Letzter Fehler ({failing[0].Recipient}): {failing[0].LastError}" : null));
        }

        // Backup
        var lastBackup = backups.LastSuccess();
        if (!settings.Backup.Enabled)
        {
            checks.Add(new(ServerGroup, "Datensicherung", CheckStatus.Warning, "ausgeschaltet",
                "Verwaltung → Datensicherung: nächtliche Sicherung auf einen Speicher außerhalb des Servers einrichten."));
        }
        else if (lastBackup is null)
        {
            checks.Add(new(ServerGroup, "Datensicherung", CheckStatus.Error, "noch keine erfolgreiche Sicherung",
                backups.RecentRuns(1).FirstOrDefault()?.Message));
        }
        else
        {
            var age = now - lastBackup.Started;
            checks.Add(new(ServerGroup, "Datensicherung", age > TimeSpan.FromHours(50) ? CheckStatus.Error : CheckStatus.Ok,
                $"letzte erfolgreiche Sicherung vor {FormatAge(age)} nach {backups.Stores.Describe(settings.Backup)}",
                age > TimeSpan.FromHours(50) ? backups.RecentRuns(1).FirstOrDefault()?.Message : null));
        }

        // Virus scanner
        if (!settings.Antivirus.Enabled)
        {
            checks.Add(new(ServerGroup, "Virenschutz", CheckStatus.Warning, "ausgeschaltet", "Verwaltung → Einstellungen → Virenschutz."));
        }
        else if (malware.Scanner is { } scanner)
        {
            checks.Add(new(ServerGroup, "Virenschutz", CheckStatus.Ok, $"Anhangfilter und {scanner.Name} aktiv"));
        }
        else
        {
            checks.Add(new(ServerGroup, "Virenschutz", CheckStatus.Warning, "nur Anhangfilter, kein Virenscanner", malware.ScannerProblem));
        }

        // Risky settings
        if (settings.Smtp.AllowInsecureAuthentication || settings.Imap.AllowInsecureAuthentication)
        {
            checks.Add(new(ServerGroup, "Anmeldung ohne Verschlüsselung", CheckStatus.Warning,
                "Smtp/Imap:AllowInsecureAuthentication ist eingeschaltet – Passwörter können unverschlüsselt übertragen werden.",
                "Nur für Tests gedacht; in appsettings.json wieder auf false setzen."));
        }

        var openRelay = settings.Smtp.RelayNetworks.Select(NetworkRange.Parse)
            .Where(r => r.PrefixLength < (r.Network.AddressFamily == AddressFamily.InterNetwork ? 24 : 64))
            .Select(r => (NetworkRange?)r).FirstOrDefault();
        if (openRelay is not null)
        {
            checks.Add(new(ServerGroup, "Relay ohne Anmeldung", CheckStatus.Error,
                $"Smtp:RelayNetworks enthält das große Netz {openRelay} – damit kann fast jeder über den Server Spam versenden.",
                "Nur einzelne Adressen eintragen, z. B. 127.0.0.1/32 für Websites auf diesem Server."));
        }

        return checks;
    }

    private async Task<IReadOnlyList<DiagnosticCheck>> CheckUpdateAsync(CancellationToken cancellationToken)
    {
        // Local builds have no release to compare with.
        if (BuildInfo.Version.StartsWith("1.0.0", StringComparison.Ordinal) || BuildInfo.Version == "unbekannt")
        {
            return [];
        }

        var latest = await LatestRelease(cancellationToken);
        return latest is null || latest == BuildInfo.Version
            ? []
            : [new(ServerGroup, "Update", CheckStatus.Info, $"Version {latest} ist verfügbar (installiert: {BuildInfo.Version}).",
                @"Aktualisieren: C:\Mailserver\update.ps1 in einer PowerShell als Administrator.")];
    }

    private async Task<IReadOnlyList<DiagnosticCheck>> CheckPortsAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var ports = new List<(string Name, IReadOnlyList<string> Listen, int Port)>
        {
            ("SMTP-Empfang", settings.Smtp.ListenAddresses, settings.Smtp.InboundPort),
            ("SMTP-Versand (STARTTLS)", settings.Smtp.ListenAddresses, settings.Smtp.SubmissionPort),
            ("SMTP-Versand (SSL)", settings.Smtp.ListenAddresses, settings.Smtp.SubmissionTlsPort),
            ("IMAP (STARTTLS)", settings.Imap.ListenAddresses, settings.Imap.Port),
            ("IMAPS", settings.Imap.ListenAddresses, settings.Imap.TlsPort),
        };
        if (settings.Web.Enabled)
        {
            ports.Add(("Weboberfläche", settings.Web.ListenAddresses, settings.Web.HttpsPort));
        }

        var checks = await Task.WhenAll(ports.Where(p => p.Port > 0).Select(async p =>
        {
            var host = p.Listen.FirstOrDefault(a => a is not ("0.0.0.0" or "::" or "*")) ?? "127.0.0.1";
            var error = await Connect(host, p.Port, cancellationToken);
            return error is null
                ? new DiagnosticCheck(NetworkGroup, $"Port {p.Port} – {p.Name}", CheckStatus.Ok, "nimmt Verbindungen an")
                : new DiagnosticCheck(NetworkGroup, $"Port {p.Port} – {p.Name}", CheckStatus.Error, $"keine Verbindung: {error}",
                    "Läuft der Dienst? Belegt ein anderes Programm den Port (z. B. SmarterMail)? Ohne Zertifikat bleiben 587/465/993 zu.");
        }));
        return [.. checks, new(NetworkGroup, "Erreichbarkeit von außen", CheckStatus.Info,
            "Ob die Ports auch aus dem Internet erreichbar sind (Windows-Firewall, Firewall des Hosters), lässt sich nur von außen prüfen.",
            "Z. B. mit https://mxtoolbox.com/diagnostic.aspx (Port 25) oder indem man das Handy ohne WLAN verbindet.")];
    }

    private async Task<IReadOnlyList<DiagnosticCheck>> CheckOutboundAsync(CancellationToken cancellationToken)
    {
        var smartHost = options.Value.Delivery.SmartHost;
        if (smartHost is { Host.Length: > 0 })
        {
            var error = await Connect(smartHost.Host, smartHost.Port, cancellationToken);
            return [error is null
                ? new(NetworkGroup, "Zustellung nach außen", CheckStatus.Ok, $"Relay-Server {smartHost.Host}:{smartHost.Port} erreichbar")
                : new(NetworkGroup, "Zustellung nach außen", CheckStatus.Error, $"Relay-Server {smartHost.Host}:{smartHost.Port} nicht erreichbar: {error}")];
        }

        // Many hosting providers block outgoing port 25; then nothing can be delivered without a relay server.
        var mx = await dns.GetMxAsync("gmail.com", cancellationToken);
        var target = mx.Records.OrderBy(r => r.Preference).FirstOrDefault()?.Host.TrimEnd('.') ?? "gmail-smtp-in.l.google.com";
        var result = await Connect(target, 25, cancellationToken);
        return [result is null
            ? new(NetworkGroup, "Port 25 nach außen", CheckStatus.Ok, $"Verbindung zu {target} möglich")
            : new(NetworkGroup, "Port 25 nach außen", CheckStatus.Error, $"keine Verbindung zu {target}:25 ({result})",
                "Vermutlich sperrt der Hoster ausgehenden Port 25. Freischalten lassen oder einen Relay-Server eintragen (Einstellungen → Zustellung).")];
    }

    private async Task<IReadOnlyList<DiagnosticCheck>> CheckAddressAsync(DnsResult<IPAddress> addresses, IReadOnlyList<IPAddress> serverIps,
        CancellationToken cancellationToken)
    {
        var hostname = options.Value.Hostname;
        if (serverIps.Count == 0)
        {
            return [new(NetworkGroup, $"DNS {hostname}", CheckStatus.Error,
                addresses.Status == DnsStatus.Error ? "DNS-Abfrage fehlgeschlagen" : $"{hostname} hat keinen A-Eintrag.",
                "Beim DNS-Anbieter einen A-Eintrag für den Hostnamen mit der öffentlichen IP des Servers anlegen.")];
        }

        var checks = new List<DiagnosticCheck>();
        var local = LocalAddresses();
        var matches = serverIps.Any(local.Contains);
        checks.Add(new(NetworkGroup, $"DNS {hostname}", matches || local.All(IsPrivate) ? CheckStatus.Ok : CheckStatus.Warning,
            $"zeigt auf {string.Join(", ", serverIps)}" + (matches ? " (dieser Server)" : ""),
            matches || local.All(IsPrivate) ? null
                : $"Dieser Server hat die Adressen {string.Join(", ", local.Where(a => !IsPrivate(a)))} – passt der A-Eintrag?"));

        foreach (var ip in serverIps)
        {
            var ptr = await dns.GetPtrAsync(ip, cancellationToken);
            var name = ptr.Records.FirstOrDefault()?.TrimEnd('.');
            checks.Add(string.Equals(name, hostname, StringComparison.OrdinalIgnoreCase)
                ? new(NetworkGroup, $"Reverse DNS {ip}", CheckStatus.Ok, $"→ {name}")
                : new(NetworkGroup, $"Reverse DNS {ip}", CheckStatus.Error, name is null ? "kein PTR-Eintrag" : $"→ {name} statt {hostname}",
                    $"Beim VPS-Anbieter den Reverse-DNS (PTR) der IP auf {hostname} setzen – sonst lehnen Gmail, Outlook & Co. Mails ab."));
        }

        var checker = new DnsBlocklistChecker(dns);
        foreach (var ip in serverIps.Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IsPrivate(ip)))
        {
            var listed = new List<string>();
            var links = new List<string>();
            foreach (var (zone, name, lookup) in Blocklists)
            {
                if (await checker.IsListedAsync(ip, zone, cancellationToken))
                {
                    listed.Add(name);
                    links.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, lookup, ip));
                }
            }

            checks.Add(listed.Count == 0
                ? new(NetworkGroup, $"Blacklists {ip}", CheckStatus.Ok, $"auf keiner von {string.Join(", ", Blocklists.Select(b => b.Name))}")
                : new(NetworkGroup, $"Blacklists {ip}", CheckStatus.Error, $"gelistet bei {string.Join(", ", listed)}",
                    $"Ursache beseitigen (Postfach mit gestohlenem Passwort? Versandlimits prüfen), dann Entfernung beantragen: {string.Join(" ", links)}"));
        }

        return checks;
    }

    private async Task<IReadOnlyList<DiagnosticCheck>> CheckDomainAsync(Domain domain, IReadOnlyList<IPAddress> serverIps,
        CancellationToken cancellationToken)
    {
        var hostname = options.Value.Hostname;
        var group = $"Domain {domain.Name}";
        var checks = new List<DiagnosticCheck>();

        // MX
        var mx = await dns.GetMxAsync(domain.Name, cancellationToken);
        // A null MX (".", RFC 7505) means the domain accepts no mail at all.
        var hosts = mx.Records.OrderBy(r => r.Preference).Select(r => r.Host.TrimEnd('.')).Where(h => h.Length > 0).ToList();
        var pointsHere = hosts.Any(h => string.Equals(h, hostname, StringComparison.OrdinalIgnoreCase));
        if (!pointsHere && serverIps.Count > 0)
        {
            foreach (var host in hosts)
            {
                var resolved = await dns.GetAddressesAsync(host, cancellationToken);
                pointsHere |= resolved.Records.Any(serverIps.Contains);
            }
        }

        checks.Add(hosts.Count == 0
            ? new(group, "MX", CheckStatus.Error, mx.Status == DnsStatus.Error ? "DNS-Abfrage fehlgeschlagen" : "kein MX-Eintrag",
                $"MX-Eintrag {domain.Name} → 10 {hostname} anlegen.")
            : new(group, "MX", pointsHere ? CheckStatus.Ok : CheckStatus.Error, string.Join(", ", hosts),
                pointsHere ? null : $"Der MX zeigt nicht auf diesen Server ({hostname}) – Mails für {domain.Name} kommen hier nicht an."));

        // SPF: evaluated like a receiving server would, for a mail from this server.
        var ipv4 = serverIps.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork) ?? serverIps.FirstOrDefault();
        var spfRecord = (await dns.GetTxtAsync(domain.Name, cancellationToken)).Records.FirstOrDefault(t => t.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase));
        if (spfRecord is null)
        {
            checks.Add(new(group, "SPF", CheckStatus.Error, "kein SPF-Eintrag", $"TXT-Eintrag {domain.Name}: v=spf1 mx -all"));
        }
        else if (options.Value.Delivery.SmartHost is { Host.Length: > 0 } smartHost)
        {
            checks.Add(new(group, "SPF", CheckStatus.Info, spfRecord, $"Mails gehen über {smartHost.Host}; dessen Server müssen im SPF-Eintrag erlaubt sein."));
        }
        else if (ipv4 is null)
        {
            checks.Add(new(group, "SPF", CheckStatus.Info, spfRecord, $"Nicht ausgewertet, weil {hostname} keine IP-Adresse hat."));
        }
        else
        {
            var spf = await new SpfChecker(dns).CheckAsync(ipv4, $"postmaster@{domain.Name}", hostname, cancellationToken);
            checks.Add(spf.Result == SpfResult.Pass
                ? new(group, "SPF", CheckStatus.Ok, $"{spfRecord} – erlaubt {ipv4}")
                : new(group, "SPF", spf.Result is SpfResult.Neutral or SpfResult.SoftFail ? CheckStatus.Warning : CheckStatus.Error,
                    $"{spfRecord} – Ergebnis für {ipv4}: {spf.Result}", "Der Eintrag muss diesen Server erlauben, z. B. v=spf1 mx -all."));
        }

        // DKIM
        var selector = domain.DkimSelector;
        if (selector is null || !dkim.HasKey(domain.Name, selector))
        {
            checks.Add(new(group, "DKIM", CheckStatus.Error, "kein DKIM-Schlüssel", $"mailadmin dkim rotate {domain.Name} und den DNS-Eintrag anlegen."));
        }
        else
        {
            var expected = PublicKey(dkim.GetDnsRecord(domain.Name, selector));
            var txt = await dns.GetTxtAsync($"{selector}._domainkey.{domain.Name}", cancellationToken);
            var published = txt.Records.Select(PublicKey).FirstOrDefault(p => p is not null);
            checks.Add(published is null
                ? new(group, "DKIM", CheckStatus.Error, $"kein Eintrag unter {selector}._domainkey.{domain.Name}",
                    $"Den TXT-Eintrag aus Verwaltung → Domains → {domain.Name} anlegen – ohne ihn landen Mails eher im Spam.")
                : published == expected
                    ? new(group, "DKIM", CheckStatus.Ok, $"Selector {selector} veröffentlicht und passend")
                    : new(group, "DKIM", CheckStatus.Error, $"der Eintrag unter {selector}._domainkey.{domain.Name} passt nicht zum Schlüssel",
                        $"Den TXT-Eintrag aus Verwaltung → Domains → {domain.Name} neu kopieren (vollständig, ohne Zeilenumbrüche)."));
        }

        // DMARC
        var dmarc = (await dns.GetTxtAsync($"_dmarc.{domain.Name}", cancellationToken)).Records
            .FirstOrDefault(t => t.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase));
        var policy = dmarc?.Split(';').Select(p => p.Trim()).FirstOrDefault(p => p.StartsWith("p=", StringComparison.OrdinalIgnoreCase))?[2..].ToLowerInvariant();
        checks.Add(dmarc is null
            ? new(group, "DMARC", CheckStatus.Warning, "kein DMARC-Eintrag",
                $"TXT-Eintrag _dmarc.{domain.Name}: v=DMARC1; p=none; rua=mailto:postmaster@{domain.Name} – Gmail und Yahoo verlangen ihn.")
            : policy is "quarantine" or "reject"
                ? new(group, "DMARC", CheckStatus.Ok, dmarc)
                : new(group, "DMARC", CheckStatus.Info, dmarc, "Wenn alles stabil läuft, auf p=quarantine oder p=reject verschärfen – das schützt vor gefälschten Absendern."));

        return checks;
    }

    private static string? PublicKey(string record) =>
        record.Split(';').Select(p => p.Trim()).FirstOrDefault(p => p.StartsWith("p=", StringComparison.Ordinal))?[2..].Replace(" ", "") is { Length: > 0 } key
            ? key
            : null;

    private static HashSet<IPAddress> LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => !IPAddress.IsLoopback(a) && !a.IsIPv6LinkLocal)
                .ToHashSet();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private static bool IsPrivate(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal || IPAddress.IsLoopback(ip);
        }

        var b = ip.GetAddressBytes();
        return b[0] is 10 or 127 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] is >= 64 and <= 127) ||
               (b[0] == 169 && b[1] == 254);
    }

    private static string FormatAge(TimeSpan age) =>
        age.TotalHours < 1 ? $"{(int)age.TotalMinutes} Minuten" : age.TotalHours < 48 ? $"{(int)age.TotalHours} Stunden" : $"{(int)age.TotalDays} Tagen";

    private static async Task<string?> TryConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            await client.ConnectAsync(host, port, timeout.Token);
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "Zeitüberschreitung";
        }
        catch (SocketException ex)
        {
            return ex.SocketErrorCode == SocketError.ConnectionRefused ? "Verbindung abgelehnt" : ex.Message;
        }
    }

    private static async Task<string?> ReadLatestReleaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mailserver-Diagnose");
            using var document = JsonDocument.Parse(await http.GetStringAsync(
                "https://api.github.com/repos/svnofeic/Mailserver/releases/tags/latest", cancellationToken));
            // Title: "Mailserver 1.0.28 (dfd097c)"
            return document.RootElement.GetProperty("name").GetString()?.Replace("Mailserver ", "").Trim();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}
