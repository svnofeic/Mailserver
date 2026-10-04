namespace Mailserver.Core;

/// <summary>
/// Root configuration, bound from the "Mailserver" section of appsettings.json.
/// </summary>
public sealed class MailserverOptions
{
    public const string SectionName = "Mailserver";

    /// <summary>Public host name of this server (must match the PTR record and the TLS certificate).</summary>
    public string Hostname { get; set; } = "mail.example.com";

    /// <summary>Data directory. Relative paths are resolved against the application directory.</summary>
    public string DataDirectory { get; set; } = "data";

    public int MaxMessageSizeBytes { get; set; } = 50 * 1024 * 1024;

    public TlsOptions Tls { get; set; } = new();

    public SmtpOptions Smtp { get; set; } = new();

    public ImapOptions Imap { get; set; } = new();

    public WebOptions Web { get; set; } = new();

    public DeliveryOptions Delivery { get; set; } = new();

    public SecurityOptions Security { get; set; } = new();

    public SpamOptions Spam { get; set; } = new();
}

public sealed class TlsOptions
{
    /// <summary>Path to a PFX file (e.g. exported by win-acme). Takes precedence over the certificate store.</summary>
    public string? PfxPath { get; set; }

    public string? PfxPassword { get; set; }

    /// <summary>
    /// Host name to look up in LocalMachine\My and LocalMachine\WebHosting (Windows, matched against subject and
    /// alternative names). The newest valid certificate wins, so renewals by win-acme or Plesk are picked up automatically. Defaults to <see cref="MailserverOptions.Hostname"/>.
    /// </summary>
    public string? StoreSubject { get; set; }

    /// <summary>How often the certificate is reloaded to pick up renewals.</summary>
    public TimeSpan ReloadInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Certificates from Let's Encrypt, issued and renewed by the server itself.</summary>
    public AcmeOptions Acme { get; set; } = new();
}

public sealed class AcmeOptions
{
    public const string LetsEncrypt = "https://acme-v02.api.letsencrypt.org/directory";
    public const string LetsEncryptStaging = "https://acme-staging-v02.api.letsencrypt.org/directory";

    public bool Enabled { get; set; }

    /// <summary>Let's Encrypt writes here before a certificate would expire (e.g. if renewing keeps failing).</summary>
    public string? Email { get; set; }

    /// <summary>
    /// Names on the certificate, separated by commas or spaces, e.g. "mail.feicht.me, webmail.feicht.me". Empty means
    /// <see cref="MailserverOptions.Hostname"/>. A plain string, because list settings from settings.json and
    /// appsettings.json would be merged entry by entry.
    /// </summary>
    public string? Hostnames { get; set; }

    /// <summary>Test certificates from the Let's Encrypt staging environment (not trusted by clients, generous limits).</summary>
    public bool UseStaging { get; set; }

    /// <summary>Another ACME server instead of Let's Encrypt.</summary>
    public string? DirectoryUrl { get; set; }

    /// <summary>Port for the http-01 check. Let's Encrypt always connects to port 80; other values only for tests.</summary>
    public int HttpPort { get; set; } = 80;

    /// <summary>
    /// Web root of another web server answering on port 80 (e.g. IIS). Challenge files are written there instead of the
    /// server answering on port 80 itself.
    /// </summary>
    public string? ChallengeDirectory { get; set; }

    /// <summary>Renew this many days before expiry (Let's Encrypt certificates are valid for 90 days).</summary>
    public int RenewDaysBefore { get; set; } = 30;

    public IReadOnlyList<string> EffectiveHostnames(string hostname) =>
        (string.IsNullOrWhiteSpace(Hostnames) ? [hostname] : Hostnames.Split([',', ';', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        .Select(h => h.Trim().TrimEnd('.').ToLowerInvariant()).Where(h => h.Length > 0).Distinct().ToList();

    public Uri EffectiveDirectoryUrl => new(string.IsNullOrWhiteSpace(DirectoryUrl) ? UseStaging ? LetsEncryptStaging : LetsEncrypt : DirectoryUrl);
}

public sealed class SmtpOptions
{
    /// <summary>Addresses to bind to, e.g. "0.0.0.0" and "::". Empty means "0.0.0.0".</summary>
    /// <remarks>
    /// List settings have no non-empty defaults on purpose: the configuration binder appends configured entries to a default
    /// array instead of replacing it, which would bind "0.0.0.0" twice.
    /// </remarks>
    public string[] ListenAddresses { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> EffectiveListenAddresses => ListenAddressDefaults.Resolve(ListenAddresses);

    /// <summary>MX port for mail from other servers. 0 disables it.</summary>
    public int InboundPort { get; set; } = 25;

    /// <summary>Submission port (STARTTLS + AUTH) for mail clients. 0 disables it.</summary>
    public int SubmissionPort { get; set; } = 587;

    /// <summary>Submission port with implicit TLS. 0 disables it.</summary>
    public int SubmissionTlsPort { get; set; } = 465;

    /// <summary>Allows AUTH without TLS. Only for local testing — never enable this in production.</summary>
    public bool AllowInsecureAuthentication { get; set; }

    public TimeSpan SessionTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Address ranges (CIDR) that may send through port 25 without authentication, to any recipient and as any sender,
    /// e.g. "127.0.0.1/32" for websites on this server that use PHP mail() via localhost. Empty by default.
    /// Only list addresses you fully control: every host in these ranges is an open relay.
    /// </summary>
    public string[] RelayNetworks { get; set; } = [];

    /// <summary>
    /// Stores a copy of every message sent through the submission ports in the sender's "Sent" folder. If the mail
    /// program stores its own copy (IMAP APPEND with the same Message-ID) within a day, the server copy is removed again.
    /// </summary>
    public bool SaveSentCopies { get; set; } = true;

    public bool IsRelayClient(System.Net.IPAddress? address) =>
        address is not null && RelayNetworks.Select(Security.NetworkRange.Parse).Any(range => range.Contains(address));
}

public sealed class WebOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Addresses to bind to. Empty means "0.0.0.0".</summary>
    public string[] ListenAddresses { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> EffectiveListenAddresses => ListenAddressDefaults.Resolve(ListenAddresses);

    /// <summary>HTTPS port of the web interface. 443 is usually taken by IIS on Windows servers.</summary>
    public int HttpsPort { get; set; } = 9443;

    /// <summary>Plain HTTP without TLS. Only for local testing — never enable this in production. 0 disables it.</summary>
    public int InsecureHttpPort { get; set; }

    /// <summary>Sign-in lifetime; extended with every request.</summary>
    public TimeSpan SessionTimeout { get; set; } = TimeSpan.FromMinutes(60);
}

public sealed class ImapOptions
{
    /// <summary>Addresses to bind to, e.g. "0.0.0.0" and "::". Empty means "0.0.0.0".</summary>
    public string[] ListenAddresses { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> EffectiveListenAddresses => ListenAddressDefaults.Resolve(ListenAddresses);

    /// <summary>IMAP with STARTTLS. Logins are only accepted after STARTTLS. 0 disables it.</summary>
    public int Port { get; set; } = 143;

    /// <summary>IMAP with implicit TLS (IMAPS). 0 disables it.</summary>
    public int TlsPort { get; set; } = 993;

    /// <summary>Allows LOGIN without TLS. Only for local testing — never enable this in production.</summary>
    public bool AllowInsecureAuthentication { get; set; }

    /// <summary>Simultaneous connections per client IP (mail clients typically open 2–10).</summary>
    public int MaxConnectionsPerIp { get; set; } = 30;

    /// <summary>Connections without any command for this long are closed (RFC 3501 requires at least 30 minutes).</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(31);

    /// <summary>
    /// Writes every IMAP command and response to data\logs\imap-trace-&lt;date&gt;.log (passwords masked) to diagnose
    /// mail programs. Only switch on while investigating: the file contains folder names and message headers.
    /// </summary>
    public bool Trace { get; set; }
}

public sealed class DeliveryOptions
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(15);

    public int MaxParallelDeliveries { get; set; } = 4;

    /// <summary>Messages that cannot be delivered within this time are bounced.</summary>
    public TimeSpan MaxQueueLifetime { get; set; } = TimeSpan.FromDays(5);

    /// <summary>Remote SMTP port. Only changed for tests.</summary>
    public int RemotePort { get; set; } = 25;

    /// <summary>Optional relay host, e.g. when the VPS provider blocks outbound port 25.</summary>
    public SmartHostOptions? SmartHost { get; set; }
}

public sealed class SmartHostOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string? Username { get; set; }
    public string? Password { get; set; }

    /// <summary>"Auto", "StartTls", "SslOnConnect" or "None".</summary>
    public string Security { get; set; } = "Auto";
}

public sealed class SecurityOptions
{
    /// <summary>Failed logins per IP before that IP is locked out.</summary>
    public int MaxAuthFailuresPerIp { get; set; } = 10;

    public TimeSpan AuthFailureWindow { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan AuthLockoutDuration { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Rejects mail on port 25 that claims a local domain as envelope sender without authentication.</summary>
    public bool RejectUnauthenticatedLocalSender { get; set; } = true;

    /// <summary>Messages with more Received headers than this are rejected (mail loop protection).</summary>
    public int MaxHopCount { get; set; } = 30;
}

public sealed class SpamOptions
{
    /// <summary>Master switch for SPF/DKIM/DMARC checks, blocklists, greylisting and scoring of mail received on port 25.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Senders in these networks (CIDR) skip all checks, e.g. a backup MX or a monitoring host.</summary>
    public string[] TrustedNetworks { get; set; } = [];

    /// <summary>Connections from this machine (127.0.0.0/8, ::1) skip all checks.</summary>
    public bool TrustLoopback { get; set; } = true;

    /// <summary>From this score on, a message is spam and goes to the recipient's Junk folder.</summary>
    public double JunkThreshold { get; set; } = 5.0;

    /// <summary>From this score on, a message is discarded without delivery. 0 disables discarding.</summary>
    public double DeleteThreshold { get; set; } = 0;

    /// <summary>Rejects messages whose sender domain publishes DMARC p=reject and that fail DMARC.</summary>
    public bool EnforceDmarcReject { get; set; } = true;

    /// <summary>Rejects at MAIL FROM when SPF says "fail" (-all). Off by default: DMARC and scoring handle it more gracefully.</summary>
    public bool RejectSpfFail { get; set; }

    /// <summary>Set to false to query no blocklists at all.</summary>
    public bool DnsBlocklistsEnabled { get; set; } = true;

    /// <summary>Blocklists to query. Empty means the defaults (Spamhaus ZEN: reject, SpamCop: +3).</summary>
    public DnsBlocklistOptions[] DnsBlocklists { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<DnsBlocklistOptions> EffectiveDnsBlocklists =>
        !DnsBlocklistsEnabled ? [] :
        DnsBlocklists.Length > 0 ? DnsBlocklists :
        [
            new() { Zone = "zen.spamhaus.org", Action = "Reject" },
            new() { Zone = "bl.spamcop.net", Action = "Score", Score = 3 },
        ];

    public GreylistingOptions Greylisting { get; set; } = new();

    public SpamLogOptions Log { get; set; } = new();
}

public sealed class SpamLogOptions
{
    /// <summary>Records every spam decision (checks, score, folder, rules, user feedback) for later analysis.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Entries older than this are deleted automatically.</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>Stores subject lines. Helpful for analysis, but personal data – switch off if not wanted.</summary>
    public bool IncludeSubject { get; set; } = true;
}

public sealed class DnsBlocklistOptions
{
    public string Zone { get; set; } = "";

    /// <summary>"Reject" refuses the connection's mail, "Score" adds <see cref="Score"/> to the spam score.</summary>
    public string Action { get; set; } = "Score";

    public double Score { get; set; } = 5;
}

public sealed class GreylistingOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>How long an unknown sender must wait before a retry is accepted.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a sender/recipient pair stays known after its last message.</summary>
    public TimeSpan Expiry { get; set; } = TimeSpan.FromDays(36);

    /// <summary>Senders whose SPF check passes are not greylisted (large providers retry slowly).</summary>
    public bool SkipOnSpfPass { get; set; } = true;
}

internal static class ListenAddressDefaults
{
    public static IReadOnlyList<string> Resolve(string[] configured) =>
        configured.Length == 0 ? ["0.0.0.0"] : configured.Select(a => a.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
