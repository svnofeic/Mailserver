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
    /// Subject name to look up in LocalMachine\My (Windows). The newest valid certificate wins,
    /// so renewals by win-acme are picked up automatically. Defaults to <see cref="MailserverOptions.Hostname"/>.
    /// </summary>
    public string? StoreSubject { get; set; }

    /// <summary>How often the certificate is reloaded to pick up renewals.</summary>
    public TimeSpan ReloadInterval { get; set; } = TimeSpan.FromHours(1);
}

public sealed class SmtpOptions
{
    /// <summary>Addresses to bind to, e.g. "0.0.0.0" and "::". Empty means "0.0.0.0".</summary>
    /// <remarks>
    /// List settings have no non-empty defaults on purpose: the configuration binder appends configured entries to a default
    /// array instead of replacing it, which would bind "0.0.0.0" twice.
    /// </remarks>
    public string[] ListenAddresses { get; set; } = [];

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
}

public sealed class ImapOptions
{
    /// <summary>Addresses to bind to, e.g. "0.0.0.0" and "::". Empty means "0.0.0.0".</summary>
    public string[] ListenAddresses { get; set; } = [];

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
