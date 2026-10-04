using Mailserver.Core;
using Mailserver.Core.Backup;
using Mailserver.Core.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailserver.Smtp;

/// <summary>
/// Runs the backup once a day at the configured time. A backup missed because the server was off is made a few minutes
/// after the start; a failed one is retried every few hours, and the administrators get a mail.
/// </summary>
public sealed class BackupService(
    BackupManager backups,
    AdminNotifier notifier,
    IOptions<MailserverOptions> options,
    TimeProvider timeProvider,
    ILogger<BackupService> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), timeProvider, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var settings = options.Value.Backup;
                if (settings.Enabled && backups.Problem() is null && !backups.Running)
                {
                    var runs = backups.RecentRuns(1);
                    if (BackupSchedule.IsDue(timeProvider.GetLocalNow(), settings.Time, backups.LastSuccess(), runs.FirstOrDefault()))
                    {
                        await RunAsync(runs.FirstOrDefault(), stoppingToken);
                    }
                }

                await Task.Delay(CheckInterval, timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunAsync(BackupRun? previous, CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting the scheduled backup to {Directory}", options.Value.Backup.Directory);
        var run = await backups.RunAsync(cancellationToken: stoppingToken);
        if (run.Success == false && previous?.Success != false && !stoppingToken.IsCancellationRequested)
        {
            // Only the first failure in a row is reported; the retries are in the backup list.
            await notifier.NotifyAsync("Datensicherung fehlgeschlagen",
                $"""
                Die Datensicherung nach {options.Value.Backup.Directory} ist fehlgeschlagen:

                {run.Message}

                Der Server versucht es alle paar Stunden erneut. Details und „Jetzt sichern“ unter Admin → Datensicherung.
                """, stoppingToken);
        }
    }
}

/// <summary>When the next backup is due.</summary>
public static class BackupSchedule
{
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(3);

    /// <param name="now">Local time.</param>
    /// <param name="time">Configured local time of day.</param>
    public static bool IsDue(DateTimeOffset now, TimeSpan time, BackupRun? lastSuccess, BackupRun? lastRun)
    {
        // The most recent scheduled moment that has passed: today at the given time, or yesterday.
        var slot = new DateTimeOffset(now.Date + time, now.Offset);
        if (slot > now)
        {
            slot = slot.AddDays(-1);
        }

        if (lastSuccess is not null && lastSuccess.Started >= slot)
        {
            return false;
        }

        return lastRun is null || lastRun.Started < slot || (lastRun.Success == false && now - lastRun.Started >= RetryAfterFailure);
    }
}
