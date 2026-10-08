using Mailserver.Core.External;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mailserver.Smtp.External;

/// <summary>Fetches the addresses at other providers every few minutes.</summary>
public sealed class ExternalFetchService(
    ExternalAccountStore store,
    IExternalMail client,
    TimeProvider timeProvider,
    ILogger<ExternalFetchService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), timeProvider, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await FetchDueAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(30), timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public async Task FetchDueAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var account in store.ListEnabled().Where(a => a.LastFetch is null || now - a.LastFetch >= Interval))
        {
            try
            {
                var result = await client.FetchAsync(account, cancellationToken);
                // A full inbox taken over at once: continue right away instead of waiting for the next interval.
                while (result.Error is null && result.Fetched >= ExternalMailClient.MaxPerRun && !cancellationToken.IsCancellationRequested)
                {
                    result = await client.FetchAsync(account, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Fetching {Address} failed", account.Address);
            }
        }
    }
}
