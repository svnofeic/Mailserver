using System.Globalization;
using Mailserver.AntiSpam.Diagnostics;
using Mailserver.Core;
using Mailserver.Core.Data;
using Mailserver.Core.Dmarc;
using Mailserver.Core.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailserver.Smtp;

/// <summary>Sends the weekly status report to the administrators (Mailserver:Reports) and removes old DMARC reports.</summary>
public sealed class WeeklyReportService(
    StatusReport report,
    AdminNotifier notifier,
    AppState state,
    DmarcReports dmarc,
    IOptions<MailserverOptions> options,
    TimeProvider timeProvider,
    ILogger<WeeklyReportService> logger) : BackgroundService
{
    public const string LastSentKey = "weekly-report-sent";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);

    /// <summary>True when the report of the current week is due and was not sent yet.</summary>
    public static bool IsDue(DateTimeOffset localNow, ReportOptions settings, DateTimeOffset? lastSent)
    {
        if (!settings.WeeklyEnabled)
        {
            return false;
        }

        // The start of this week's slot: the configured day and hour on or before now.
        var daysBack = ((int)localNow.DayOfWeek - (int)settings.WeeklyDay + 7) % 7;
        var slot = new DateTimeOffset(localNow.Date.AddDays(-daysBack).AddHours(Math.Clamp(settings.WeeklyHour, 0, 23)), localNow.Offset);
        if (slot > localNow)
        {
            slot = slot.AddDays(-7);
        }

        return lastSent is null ? localNow - slot < TimeSpan.FromDays(1) : lastSent < slot;
    }

    public async Task<StatusReport.Weekly> SendAsync(CancellationToken cancellationToken)
    {
        var weekly = await report.BuildWeeklyAsync(cancellationToken);
        await notifier.NotifyAsync(weekly.Subject, weekly.Text, cancellationToken);
        state.Set(LastSentKey, timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        logger.LogInformation("Weekly report sent ({Problems} problems)", weekly.Problems);
        return weekly;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(3), timeProvider, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var last = DateTimeOffset.TryParse(state.Get(LastSentKey), CultureInfo.InvariantCulture, DateTimeStyles.None, out var sent) ? sent : (DateTimeOffset?)null;
                    if (IsDue(timeProvider.GetLocalNow(), options.Value.Reports, last))
                    {
                        await SendAsync(stoppingToken);
                        dmarc.Cleanup(timeProvider.GetUtcNow().AddDays(-400));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Weekly report could not be sent");
                }

                await Task.Delay(CheckInterval, timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
