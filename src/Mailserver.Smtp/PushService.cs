using Mailserver.Core.Push;
using Microsoft.Extensions.Hosting;

namespace Mailserver.Smtp;

/// <summary>Sends the queued push notifications for new mail.</summary>
public sealed class PushService(PushNotifier notifier) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await notifier.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
