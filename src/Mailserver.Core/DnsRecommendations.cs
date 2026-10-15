namespace Mailserver.Core;

public sealed record DnsRecord(string Name, string Type, string Value, string? Hint = null);

/// <summary>The DNS records a domain needs for this server (shown by mailadmin and the web interface).</summary>
public static class DnsRecommendations
{
    public static IReadOnlyList<DnsRecord> For(string domain, string hostname, string? dkimSelector, string? dkimRecord) =>
    [
        new(hostname, "A", "<öffentliche IPv4 des Servers>", "Hostname des Mailservers"),
        new(domain, "MX", $"10 {hostname}.", "Mails für die Domain gehen an diesen Server"),
        new(domain, "TXT", "v=spf1 mx -all", "SPF: nur der MX-Server darf für die Domain senden"),
        new($"_dmarc.{domain}", "TXT", $"v=DMARC1; p=none; rua=mailto:postmaster@{domain}",
            "nach erfolgreichen Tests auf p=quarantine bzw. p=reject verschärfen"),
        .. dkimSelector is not null && dkimRecord is not null
            ? new[] { new DnsRecord($"{dkimSelector}._domainkey.{domain}", "TXT", dkimRecord,
                "manche DNS-Anbieter verlangen, den Wert in Stücke zu höchstens 255 Zeichen zu teilen") }
            : [],
        new("<IPv4 des Servers>", "PTR", hostname, "Reverse DNS – beim VPS-Anbieter einzutragen"),
    ];

    /// <summary>Optional: MTA-STS and TLS reports (see Verwaltung → Domains → Domain).</summary>
    public static IReadOnlyList<DnsRecord> MtaSts(string domain, MailserverOptions options) =>
    [
        new($"mta-sts.{domain}", "A", "<IPv4 der Website (Plesk/IIS)>", "Subdomain für die Richtliniendatei, mit gültigem HTTPS-Zertifikat"),
        new($"_mta-sts.{domain}", "TXT", Security.MtaStsPublisher.TxtRecord(options),
            "die id ändert sich, wenn sich die Richtlinie ändert – dann den Eintrag anpassen"),
        new($"_smtp._tls.{domain}", "TXT", Security.MtaStsPublisher.TlsRptRecord(domain), "TLS-Berichte der anderen Server an postmaster@"),
    ];
}
