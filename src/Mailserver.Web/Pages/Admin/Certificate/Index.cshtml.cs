using System.Security.Cryptography.X509Certificates;
using Mailserver.Core;
using Mailserver.Core.Configuration;
using Mailserver.Core.Security;
using Mailserver.Core.Security.Acme;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Mailserver.Web.Pages.Admin.Certificate;

/// <summary>The TLS certificate: what is in use, and automatic certificates from Let's Encrypt.</summary>
public sealed class IndexModel(IOptions<MailserverOptions> options, CertificateProvider certificates, AcmeCertificateManager acme,
    SettingsStore settings) : MailPageModel
{
    public X509Certificate2? Current { get; private set; }
    public IReadOnlyList<string> CurrentNames { get; private set; } = [];
    public string Source { get; private set; } = "";
    public AcmeStatus Status { get; private set; } = AcmeStatus.None;
    public string? RenewalReason { get; private set; }
    public bool RestartRecommended { get; private set; }
    public string Hostname => options.Value.Hostname;
    public bool UsesPfxFile => !string.IsNullOrEmpty(options.Value.Tls.PfxPath);

    [BindProperty]
    public AcmeForm Form { get; set; } = new();

    public void OnGet()
    {
        Form = AcmeForm.From(options.Value.Tls.Acme);
        Describe();
    }

    public IActionResult OnPostSave() => Save();

    public async Task<IActionResult> OnPostIssueAsync(CancellationToken cancellationToken)
    {
        var acmeOptions = Form.ToOptions(options.Value.Tls.Acme);
        if (Validate(acmeOptions) is { } problem)
        {
            ErrorMessage = problem;
            Describe();
            return Page();
        }

        settings.SaveAcme(acmeOptions);
        var result = await acme.IssueAsync(cancellationToken: cancellationToken, settings: acmeOptions);
        if (result.Success == true)
        {
            certificates.Invalidate();
            Message = result.Message;
        }
        else
        {
            ErrorMessage = $"Ausstellen fehlgeschlagen: {result.Message}";
        }

        return RedirectToPage();
    }

    public IActionResult OnPostRestart()
    {
        if (!OperatingSystem.IsWindows())
        {
            ErrorMessage = "Neustart ist nur unter Windows möglich.";
            return RedirectToPage();
        }

        // A separate process survives the service stopping and starts it again.
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"Start-Sleep -Seconds 2; Restart-Service -Name Mailserver -Force\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Message = "Der Dienst wird neu gestartet. In etwa 30 Sekunden ist die Weboberfläche wieder erreichbar.";
        return RedirectToPage();
    }

    private IActionResult Save()
    {
        var acmeOptions = Form.ToOptions(options.Value.Tls.Acme);
        if (Validate(acmeOptions) is { } problem)
        {
            ErrorMessage = problem;
            Describe();
            return Page();
        }

        settings.SaveAcme(acmeOptions);
        Message = acmeOptions.Enabled
            ? "Gespeichert. Der Server holt ein Zertifikat, sobald eines fehlt oder bald abläuft – sofort mit „Jetzt ausstellen“."
            : "Gespeichert. Let's Encrypt ist ausgeschaltet.";
        return RedirectToPage();
    }

    private string? Validate(AcmeOptions acmeOptions)
    {
        if (acmeOptions.Enabled && string.IsNullOrWhiteSpace(acmeOptions.Email))
        {
            return "Bitte eine E-Mail-Adresse angeben – Let's Encrypt warnt dorthin, falls ein Zertifikat abzulaufen droht.";
        }

        if (!string.IsNullOrWhiteSpace(acmeOptions.Email) && !EmailAddress.TryParse(acmeOptions.Email, out _))
        {
            return "Die E-Mail-Adresse ist ungültig.";
        }

        if (!string.IsNullOrWhiteSpace(acmeOptions.ChallengeDirectory) && !Directory.Exists(acmeOptions.ChallengeDirectory))
        {
            return $"Den Ordner {acmeOptions.ChallengeDirectory} gibt es nicht.";
        }

        return acmeOptions.EffectiveHostnames(Hostname).Count == 0 ? "Bitte mindestens einen Hostnamen angeben." : null;
    }

    private void Describe()
    {
        Current = certificates.GetCertificate();
        if (Current is not null)
        {
            CurrentNames = Current.Extensions.OfType<X509SubjectAlternativeNameExtension>().SelectMany(e => e.EnumerateDnsNames()).ToList();
            using var issued = acme.LoadCertificate();
            Source = UsesPfxFile ? $"PFX-Datei {options.Value.Tls.PfxPath}"
                : issued?.Thumbprint == Current.Thumbprint ? "Let's Encrypt (von diesem Server ausgestellt)"
                : "Windows-Zertifikatsspeicher";
        }

        Status = acme.Status;
        RenewalReason = acme.RenewalReason();
        RestartRecommended = certificates.RestartRecommended;
    }
}

public sealed class AcmeForm
{
    public bool Enabled { get; set; }
    public string? Email { get; set; }
    public string? Hostnames { get; set; }
    public bool UseStaging { get; set; }
    public string? ChallengeDirectory { get; set; }

    public static AcmeForm From(AcmeOptions acme) => new()
    {
        Enabled = acme.Enabled,
        Email = acme.Email,
        Hostnames = string.Join("\n", (acme.Hostnames ?? "").Split([',', ';', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)),
        UseStaging = acme.UseStaging,
        ChallengeDirectory = acme.ChallengeDirectory,
    };

    public AcmeOptions ToOptions(AcmeOptions current) => new()
    {
        Enabled = Enabled,
        Email = Email?.Trim(),
        Hostnames = string.Join(", ", (Hostnames ?? "").Split([',', ';', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)),
        UseStaging = UseStaging,
        ChallengeDirectory = string.IsNullOrWhiteSpace(ChallengeDirectory) ? null : ChallengeDirectory.Trim(),
        DirectoryUrl = current.DirectoryUrl,
        HttpPort = current.HttpPort,
        RenewDaysBefore = current.RenewDaysBefore,
    };
}
