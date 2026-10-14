using System.Globalization;
using Mailserver.AntiSpam.Diagnostics;
using Mailserver.Core;
using Mailserver.Core.Data;
using Mailserver.Core.Routing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Mailserver.Web.Pages.Admin.Report;

/// <summary>Weekly status report (preview, send now) and the status address for outside monitoring.</summary>
public sealed class IndexModel(StatusReport report, AdminNotifier notifier, AppState state, IOptions<MailserverOptions> options) : MailPageModel
{
    /// <summary>Same key as the background service in Mailserver.Smtp, so "send now" counts as this week's report.</summary>
    private const string LastSentKey = "weekly-report-sent";

    public ReportOptions Settings => options.Value.Reports;
    public string StatusUrl { get; private set; } = "";
    public IReadOnlyList<string> Problems { get; private set; } = [];
    public DateTimeOffset? LastSent { get; private set; }
    public StatusReport.Weekly? Preview { get; private set; }

    public static string DayName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "Montag", DayOfWeek.Tuesday => "Dienstag", DayOfWeek.Wednesday => "Mittwoch", DayOfWeek.Thursday => "Donnerstag",
        DayOfWeek.Friday => "Freitag", DayOfWeek.Saturday => "Samstag", _ => "Sonntag",
    };

    public void OnGet() => Load();

    public async Task<IActionResult> OnPostPreviewAsync(CancellationToken cancellationToken)
    {
        Load();
        Preview = await report.BuildWeeklyAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostSendAsync(CancellationToken cancellationToken)
    {
        var weekly = await report.BuildWeeklyAsync(cancellationToken);
        await notifier.NotifyAsync(weekly.Subject, weekly.Text, cancellationToken);
        state.Set(LastSentKey, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        Message = $"Bericht „{weekly.Subject}“ an die Administratoren gesendet.";
        return RedirectToPage();
    }

    private void Load()
    {
        Problems = report.QuickProblems();
        LastSent = DateTimeOffset.TryParse(state.Get(LastSentKey), CultureInfo.InvariantCulture, DateTimeStyles.None, out var sent) ? sent : null;
        var settings = options.Value;
        var web = settings.Web.PublicUrl is { Length: > 0 } url ? url.TrimEnd('/')
            : $"https://{settings.Hostname}{(settings.Web.HttpsPort == 443 ? "" : $":{settings.Web.HttpsPort}")}";
        StatusUrl = $"{web}/status{(string.IsNullOrEmpty(Settings.StatusKey) ? "" : "?key=" + Uri.EscapeDataString(Settings.StatusKey))}";
    }
}
