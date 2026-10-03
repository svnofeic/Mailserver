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
    /// <summary>Addresses to bind to, e.g. "0.0.0.0" and "::".</summary>
    public string[] ListenAddresses { get; set; } = ["0.0.0.0"];

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
    /// <summary>Addresses to bind to, e.g. "0.0.0.0" and "::".</summary>
    public string[] ListenAddresses { get; set; } = ["0.0.0.0"];

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
