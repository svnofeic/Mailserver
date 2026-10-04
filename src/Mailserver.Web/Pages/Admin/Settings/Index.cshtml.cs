using System.Globalization;
using System.Text.Json;
using Mailserver.Core;
using Mailserver.Core.Configuration;
using Mailserver.Core.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Mailserver.Web.Pages.Admin.Settings;

/// <summary>
/// Edits the settings that apply at runtime. They are written to data/settings.json, which overrides appsettings.json and is
/// reloaded automatically.
/// </summary>
public sealed class IndexModel(IOptions<MailserverOptions> options, SettingsStore store, CertificateProvider certificates, DataPaths paths,
    Mailserver.Core.Antivirus.MalwareFilter malwareFilter) : MailPageModel
{
    public MailserverOptions Current => options.Value;
    public string SettingsFile => paths.SettingsFile;
    public string Certificate { get; private set; } = "";
    public string? ScannerName { get; private set; }
    public string? ScannerProblem { get; private set; }

    [BindProperty]
    public SettingsForm Form { get; set; } = new();

    public void OnGet()
    {
        Form = SettingsForm.From(Current);
        Describe();
    }

    public IActionResult OnPost()
    {
        try
        {
            // Start from a copy of the current values so settings that are not on the form stay untouched.
            var spam = Clone(Current.Spam);
            var security = Clone(Current.Security);
            var delivery = Clone(Current.Delivery);
            var antivirus = Clone(Current.Antivirus);
            Form.ApplyTo(spam, security, delivery, antivirus);
            store.Save(spam, security, delivery, antivirus);
        }
        catch (FormatException ex)
        {
            ErrorMessage = ex.Message;
            Describe();
            return Page();
        }

        Message = "Einstellungen gespeichert. Sie gelten nach wenigen Sekunden, ein Neustart ist nicht nötig.";
        return RedirectToPage();
    }

    /// <summary>Scans the harmless EICAR test file; every virus scanner must report it.</summary>
    public async Task<IActionResult> OnPostTestScannerAsync(CancellationToken cancellationToken)
    {
        if (malwareFilter.Scanner is not { } scanner)
        {
            ErrorMessage = malwareFilter.ScannerProblem;
            return RedirectToPage();
        }

        var result = await scanner.ScanAsync([new Mailserver.Core.Antivirus.Attachment("eicar.com", Mailserver.Core.Antivirus.MalwareFilter.Eicar)],
            cancellationToken);
        if (result.Outcome == Mailserver.Core.Antivirus.ScanOutcome.Infected)
        {
            Message = $"{scanner.Name} funktioniert: die EICAR-Testdatei wurde erkannt ({result.Threat}).";
        }
        else
        {
            ErrorMessage = result.Outcome == Mailserver.Core.Antivirus.ScanOutcome.Clean
                ? $"{scanner.Name} hat die EICAR-Testdatei NICHT erkannt – der Scanner arbeitet nicht richtig."
                : $"{scanner.Name}: {result.Detail}";
        }

        return RedirectToPage();
    }

    private void Describe()
    {
        ScannerName = malwareFilter.Scanner?.Name;
        ScannerProblem = malwareFilter.ScannerProblem;
        var certificate = certificates.GetCertificate();
        Certificate = certificate is null ? "kein Zertifikat gefunden" : $"{certificate.Subject}, gültig bis {Format.Time(certificate.NotAfter)}";
    }

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
}

public sealed class SettingsForm
{
    public bool SpamEnabled { get; set; }
    public double JunkThreshold { get; set; }
    public double DeleteThreshold { get; set; }
    public bool EnforceDmarcReject { get; set; }
    public bool RejectSpfFail { get; set; }
    public bool TrustLoopback { get; set; }
    public string? TrustedNetworks { get; set; }
    public bool DnsBlocklistsEnabled { get; set; }
    public string? DnsBlocklists { get; set; }
    public bool GreylistingEnabled { get; set; }
    public double GreylistingDelayMinutes { get; set; }
    public double GreylistingExpiryDays { get; set; }
    public bool GreylistingSkipOnSpfPass { get; set; }
    public bool LogEnabled { get; set; }
    public int LogRetentionDays { get; set; }
    public bool LogIncludeSubject { get; set; }

    public int MaxAuthFailuresPerIp { get; set; }
    public double AuthFailureWindowMinutes { get; set; }
    public double AuthLockoutMinutes { get; set; }
    public bool RejectUnauthenticatedLocalSender { get; set; }
    public int MaxHopCount { get; set; }

    public double MaxQueueLifetimeDays { get; set; }
    public int MaxParallelDeliveries { get; set; }
    public string? SmartHost { get; set; }
    public int SmartHostPort { get; set; } = 587;
    public string? SmartHostUsername { get; set; }
    public string? SmartHostPassword { get; set; }
    public string SmartHostSecurity { get; set; } = "Auto";

    public bool AntivirusEnabled { get; set; }
    public string Scanner { get; set; } = "Auto";
    public string? ClamAvHost { get; set; }
    public int ClamAvPort { get; set; } = 3310;
    public bool ScanOutgoing { get; set; }
    public string? BlockedExtensions { get; set; }
    public string SuspiciousAttachments { get; set; } = "Junk";
    public string OnScanError { get; set; } = "Accept";

    public int SendMaxPerMessage { get; set; }
    public int SendMaxPerHour { get; set; }
    public int SendMaxPerDay { get; set; }
    public bool SendBlockOnLimit { get; set; }

    public static SettingsForm From(MailserverOptions o) => new()
    {
        SpamEnabled = o.Spam.Enabled, JunkThreshold = o.Spam.JunkThreshold, DeleteThreshold = o.Spam.DeleteThreshold,
        EnforceDmarcReject = o.Spam.EnforceDmarcReject, RejectSpfFail = o.Spam.RejectSpfFail, TrustLoopback = o.Spam.TrustLoopback,
        TrustedNetworks = string.Join('\n', o.Spam.TrustedNetworks),
        DnsBlocklistsEnabled = o.Spam.DnsBlocklistsEnabled,
        DnsBlocklists = string.Join('\n', o.Spam.EffectiveDnsBlocklists.Select(b =>
            $"{b.Zone};{b.Action}{(b.Action.Equals("Score", StringComparison.OrdinalIgnoreCase) ? ";" + b.Score.ToString(CultureInfo.InvariantCulture) : "")}")),
        GreylistingEnabled = o.Spam.Greylisting.Enabled, GreylistingDelayMinutes = o.Spam.Greylisting.Delay.TotalMinutes,
        GreylistingExpiryDays = o.Spam.Greylisting.Expiry.TotalDays, GreylistingSkipOnSpfPass = o.Spam.Greylisting.SkipOnSpfPass,
        LogEnabled = o.Spam.Log.Enabled, LogRetentionDays = o.Spam.Log.RetentionDays, LogIncludeSubject = o.Spam.Log.IncludeSubject,
        MaxAuthFailuresPerIp = o.Security.MaxAuthFailuresPerIp, AuthFailureWindowMinutes = o.Security.AuthFailureWindow.TotalMinutes,
        AuthLockoutMinutes = o.Security.AuthLockoutDuration.TotalMinutes, RejectUnauthenticatedLocalSender = o.Security.RejectUnauthenticatedLocalSender,
        MaxHopCount = o.Security.MaxHopCount,
        MaxQueueLifetimeDays = o.Delivery.MaxQueueLifetime.TotalDays, MaxParallelDeliveries = o.Delivery.MaxParallelDeliveries,
        SmartHost = o.Delivery.SmartHost?.Host, SmartHostPort = o.Delivery.SmartHost?.Port ?? 587, SmartHostUsername = o.Delivery.SmartHost?.Username,
        SmartHostSecurity = o.Delivery.SmartHost?.Security ?? "Auto",
        AntivirusEnabled = o.Antivirus.Enabled, Scanner = o.Antivirus.Scanner, ClamAvHost = o.Antivirus.ClamAvHost, ClamAvPort = o.Antivirus.ClamAvPort,
        ScanOutgoing = o.Antivirus.ScanOutgoing, SuspiciousAttachments = o.Antivirus.SuspiciousAttachments, OnScanError = o.Antivirus.OnScanError,
        BlockedExtensions = string.IsNullOrWhiteSpace(o.Antivirus.BlockedExtensions) ? AntivirusOptions.DefaultBlockedExtensions : o.Antivirus.BlockedExtensions,
        SendMaxPerMessage = o.Security.Sending.MaxRecipientsPerMessage, SendMaxPerHour = o.Security.Sending.MaxRecipientsPerHour,
        SendMaxPerDay = o.Security.Sending.MaxRecipientsPerDay, SendBlockOnLimit = o.Security.Sending.BlockOnLimit,
    };

    public void ApplyTo(SpamOptions spam, SecurityOptions security, DeliveryOptions delivery, AntivirusOptions antivirus)
    {
        Require(Scanner is "Auto" or "Defender" or "ClamAV" or "None", "Unbekannter Virenscanner.");
        Require(SuspiciousAttachments is "Junk" or "Reject" or "Allow", "Ungültige Aktion für verdächtige Anhänge.");
        Require(OnScanError is "Accept" or "Defer", "Ungültige Aktion bei Scanfehlern.");
        Require(ClamAvPort is > 0 and < 65536, "Ungültiger ClamAV-Port.");
        Require(SendMaxPerMessage >= 0 && SendMaxPerHour >= 0 && SendMaxPerDay >= 0, "Versandlimits dürfen nicht negativ sein (0 = ohne Limit).");
        antivirus.Enabled = AntivirusEnabled;
        antivirus.Scanner = Scanner;
        antivirus.ClamAvHost = string.IsNullOrWhiteSpace(ClamAvHost) ? "127.0.0.1" : ClamAvHost.Trim();
        antivirus.ClamAvPort = ClamAvPort;
        antivirus.ScanOutgoing = ScanOutgoing;
        antivirus.SuspiciousAttachments = SuspiciousAttachments;
        antivirus.OnScanError = OnScanError;
        var extensions = (BlockedExtensions ?? "").Split([',', ';', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim().TrimStart('.').ToLowerInvariant()).Distinct().ToList();
        Require(extensions.All(e => e.All(c => char.IsLetterOrDigit(c) || c == '-')), "Dateiendungen bitte ohne Sonderzeichen angeben, z. B. exe, js, vbs.");
        antivirus.BlockedExtensions = string.Join(", ", extensions);

        security.Sending.MaxRecipientsPerMessage = SendMaxPerMessage;
        security.Sending.MaxRecipientsPerHour = SendMaxPerHour;
        security.Sending.MaxRecipientsPerDay = SendMaxPerDay;
        security.Sending.BlockOnLimit = SendBlockOnLimit;

        Require(JunkThreshold > 0, "Die Spam-Schwelle muss größer als 0 sein.");
        Require(DeleteThreshold == 0 || DeleteThreshold > JunkThreshold, "Die Lösch-Schwelle muss 0 (aus) oder größer als die Spam-Schwelle sein.");
        Require(LogRetentionDays is >= 1 and <= 3650, "Die Aufbewahrung muss zwischen 1 und 3650 Tagen liegen.");
        Require(MaxAuthFailuresPerIp is >= 1 and <= 1000, "Fehlversuche pro IP: 1 bis 1000.");
        Require(MaxHopCount is >= 5 and <= 100, "Maximale Hops: 5 bis 100.");
        Require(MaxParallelDeliveries is >= 1 and <= 64, "Parallele Zustellungen: 1 bis 64.");
        Require(MaxQueueLifetimeDays is > 0 and <= 30, "Zustellversuche: höchstens 30 Tage.");
        Require(GreylistingDelayMinutes is >= 0 and <= 60, "Greylisting-Wartezeit: 0 bis 60 Minuten.");

        spam.Enabled = SpamEnabled;
        spam.JunkThreshold = JunkThreshold;
        spam.DeleteThreshold = DeleteThreshold;
        spam.EnforceDmarcReject = EnforceDmarcReject;
        spam.RejectSpfFail = RejectSpfFail;
        spam.TrustLoopback = TrustLoopback;
        spam.TrustedNetworks = Lines(TrustedNetworks).Select(line =>
        {
            try
            {
                _ = NetworkRange.Parse(line);
                return line;
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                throw new FormatException($"Ungültiges Netz „{line}“ – erwartet z. B. 192.0.2.0/24.");
            }
        }).ToArray();
        spam.DnsBlocklistsEnabled = DnsBlocklistsEnabled;
        spam.DnsBlocklists = Lines(DnsBlocklists).Select(ParseBlocklist).ToArray();
        spam.Greylisting.Enabled = GreylistingEnabled;
        spam.Greylisting.Delay = TimeSpan.FromMinutes(GreylistingDelayMinutes);
        spam.Greylisting.Expiry = TimeSpan.FromDays(Math.Max(1, GreylistingExpiryDays));
        spam.Greylisting.SkipOnSpfPass = GreylistingSkipOnSpfPass;
        spam.Log.Enabled = LogEnabled;
        spam.Log.RetentionDays = LogRetentionDays;
        spam.Log.IncludeSubject = LogIncludeSubject;

        security.MaxAuthFailuresPerIp = MaxAuthFailuresPerIp;
        security.AuthFailureWindow = TimeSpan.FromMinutes(Math.Max(1, AuthFailureWindowMinutes));
        security.AuthLockoutDuration = TimeSpan.FromMinutes(Math.Max(1, AuthLockoutMinutes));
        security.RejectUnauthenticatedLocalSender = RejectUnauthenticatedLocalSender;
        security.MaxHopCount = MaxHopCount;

        delivery.MaxQueueLifetime = TimeSpan.FromDays(MaxQueueLifetimeDays);
        delivery.MaxParallelDeliveries = MaxParallelDeliveries;
        if (string.IsNullOrWhiteSpace(SmartHost))
        {
            delivery.SmartHost = null;
        }
        else
        {
            Require(SmartHostPort is > 0 and < 65536, "Ungültiger Port für den Relay-Server.");
            Require(SmartHostSecurity is "Auto" or "StartTls" or "SslOnConnect" or "None", "Ungültige Verschlüsselung für den Relay-Server.");
            var previousPassword = delivery.SmartHost?.Password;
            delivery.SmartHost = new SmartHostOptions
            {
                Host = SmartHost.Trim(), Port = SmartHostPort, Username = string.IsNullOrWhiteSpace(SmartHostUsername) ? null : SmartHostUsername.Trim(),
                // An empty password field keeps the stored password (it is never sent to the browser).
                Password = string.IsNullOrEmpty(SmartHostPassword) ? previousPassword : SmartHostPassword,
                Security = SmartHostSecurity,
            };
        }
    }

    private static DnsBlocklistOptions ParseBlocklist(string line)
    {
        var parts = line.Split(';', StringSplitOptions.TrimEntries);
        var action = parts.Length > 1 && parts[1].Equals("Reject", StringComparison.OrdinalIgnoreCase) ? "Reject" : "Score";
        var score = 5.0;
        if (parts.Length > 2 && !double.TryParse(parts[2].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out score))
        {
            throw new FormatException($"Ungültige Punktzahl in „{line}“.");
        }

        if (!EmailAddress.TryNormalizeDomain(parts[0], out var zone))
        {
            throw new FormatException($"Ungültige Blacklist-Zone „{parts[0]}“.");
        }

        return new DnsBlocklistOptions { Zone = zone, Action = action, Score = score };
    }

    private static IEnumerable<string> Lines(string? text) =>
        (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => !l.StartsWith('#'));

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new FormatException(message);
        }
    }
}
