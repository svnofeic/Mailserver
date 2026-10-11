namespace Mailserver.Core.External;

/// <param name="Hint">What the user has to do at the provider first (German), or null.</param>
/// <param name="Microsoft">Outlook.com and friends: no passwords any more, sign-in through Microsoft (OAuth 2).</param>
public sealed record MailProvider(string Name, MailServerAddress Imap, MailServerAddress Smtp, string? Hint, bool Microsoft = false);

/// <summary>Server settings of common providers, so that the user only enters address and password.</summary>
public static class MailProviders
{
    private static readonly MailProvider TOnline = new("T-Online",
        new("secureimap.t-online.de", 993, MailSecurity.Ssl), new("securesmtp.t-online.de", 465, MailSecurity.Ssl),
        "T-Online verlangt ein eigenes „Passwort für E-Mail-Programme“ (nicht das Login-Passwort): im E-Mail Center unter " +
        "Einstellungen → Passwörter → „E-Mail-Passwort“ festlegen und dieses hier eintragen.");

    private static readonly MailProvider Gmx = new("GMX",
        new("imap.gmx.net", 993, MailSecurity.Ssl), new("mail.gmx.net", 465, MailSecurity.Ssl),
        "Bei GMX zuerst unter E-Mail → Einstellungen → POP3/IMAP Abruf „POP3 und IMAP Zugriff erlauben“ einschalten.");

    private static readonly MailProvider WebDe = new("WEB.DE",
        new("imap.web.de", 993, MailSecurity.Ssl), new("smtp.web.de", 587, MailSecurity.StartTls),
        "Bei WEB.DE zuerst unter E-Mail → Einstellungen → POP3/IMAP Abruf „POP3 und IMAP Zugriff erlauben“ einschalten.");

    private static readonly MailProvider Gmail = new("Gmail",
        new("imap.gmail.com", 993, MailSecurity.Ssl), new("smtp.gmail.com", 465, MailSecurity.Ssl),
        "Google verlangt ein App-Passwort: Google-Konto → Sicherheit → Bestätigung in zwei Schritten einschalten → App-Passwörter.");

    private static readonly MailProvider ICloud = new("iCloud",
        new("imap.mail.me.com", 993, MailSecurity.Ssl), new("smtp.mail.me.com", 587, MailSecurity.StartTls),
        "Apple verlangt ein app-spezifisches Passwort: account.apple.com → Anmeldung und Sicherheit → App-spezifische Passwörter.");

    private static readonly MailProvider Yahoo = new("Yahoo",
        new("imap.mail.yahoo.com", 993, MailSecurity.Ssl), new("smtp.mail.yahoo.com", 465, MailSecurity.Ssl),
        "Yahoo verlangt ein App-Passwort: Kontoinformationen → Kontosicherheit → App-Passwort generieren.");

    private static readonly MailProvider Freenet = new("freenet",
        new("mx.freenet.de", 993, MailSecurity.Ssl), new("mx.freenet.de", 587, MailSecurity.StartTls),
        "Bei freenet zuerst unter Einstellungen → E-Mail-Programme den Zugriff per IMAP/SMTP freischalten.");

    private static readonly MailProvider Posteo = new("Posteo",
        new("posteo.de", 993, MailSecurity.Ssl), new("posteo.de", 465, MailSecurity.Ssl), null);

    private static readonly MailProvider MailboxOrg = new("mailbox.org",
        new("imap.mailbox.org", 993, MailSecurity.Ssl), new("smtp.mailbox.org", 465, MailSecurity.Ssl), null);

    public static readonly MailProvider Outlook = new("Outlook.com",
        new("outlook.office365.com", 993, MailSecurity.Ssl), new("smtp-mail.outlook.com", 587, MailSecurity.StartTls),
        "Microsoft erlaubt keine Anmeldung mit Passwort mehr: Das Konto wird mit einem Code bei Microsoft angemeldet.", Microsoft: true);

    private static readonly Dictionary<string, MailProvider> ByDomain = new(StringComparer.OrdinalIgnoreCase)
    {
        ["t-online.de"] = TOnline, ["magenta.de"] = TOnline,
        ["gmx.de"] = Gmx, ["gmx.net"] = Gmx, ["gmx.at"] = Gmx, ["gmx.ch"] = Gmx, ["gmx.com"] = Gmx,
        ["web.de"] = WebDe,
        ["gmail.com"] = Gmail, ["googlemail.com"] = Gmail,
        ["icloud.com"] = ICloud, ["me.com"] = ICloud, ["mac.com"] = ICloud,
        ["yahoo.com"] = Yahoo, ["yahoo.de"] = Yahoo, ["ymail.com"] = Yahoo,
        ["freenet.de"] = Freenet,
        ["posteo.de"] = Posteo, ["posteo.net"] = Posteo,
        ["mailbox.org"] = MailboxOrg,
        ["outlook.com"] = Outlook, ["outlook.de"] = Outlook, ["hotmail.com"] = Outlook, ["hotmail.de"] = Outlook,
        ["live.com"] = Outlook, ["live.de"] = Outlook, ["msn.com"] = Outlook,
    };

    /// <summary>Known provider of an address, or null.</summary>
    public static MailProvider? For(string? address)
    {
        var at = address?.LastIndexOf('@') ?? -1;
        return at < 0 ? null : ByDomain.GetValueOrDefault(address![(at + 1)..].Trim());
    }

    /// <summary>Names of the providers with prepared settings, for the help text.</summary>
    public static IReadOnlyList<string> Names => ByDomain.Values.Select(p => p.Name).Distinct().ToList();
}
