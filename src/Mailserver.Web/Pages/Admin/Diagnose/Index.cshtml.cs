using Mailserver.AntiSpam.Diagnostics;

namespace Mailserver.Web.Pages.Admin.Diagnose;

/// <summary>Checks DNS, reputation, ports, certificate, queue, backup – everything that makes mail arrive.</summary>
public sealed class IndexModel(ServerDiagnostics diagnostics) : MailPageModel
{
    public IReadOnlyList<DiagnosticCheck> Checks { get; private set; } = [];
    public int Errors => Checks.Count(c => c.Status == CheckStatus.Error);
    public int Warnings => Checks.Count(c => c.Status == CheckStatus.Warning);

    public async Task OnGetAsync(CancellationToken cancellationToken) => Checks = await diagnostics.RunAsync(cancellationToken);

    public static string PillClass(CheckStatus status) => status switch
    {
        CheckStatus.Ok => "good",
        CheckStatus.Warning => "warn",
        CheckStatus.Error => "bad",
        _ => "",
    };

    public static string Label(CheckStatus status) => status switch
    {
        CheckStatus.Ok => "OK",
        CheckStatus.Warning => "Warnung",
        CheckStatus.Error => "Fehler",
        _ => "Info",
    };
}
