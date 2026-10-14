using Mailserver.Core.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mailserver.Smtp;

/// <summary>Keeps the search index up to date: new messages every minute, deleted ones once an hour.</summary>
public sealed class SearchIndexer(SearchIndex index, ILogger<SearchIndexer> logger) : BackgroundService
{
    private const int BatchSize = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastCleanup = DateTimeOffset.MinValue;
        // Let the server start first.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var total = 0;
                int indexed;
                do
                {
                    indexed = await index.IndexPendingAsync(BatchSize, cancellationToken: stoppingToken);
                    total += indexed;
                }
                while (indexed == BatchSize && !stoppingToken.IsCancellationRequested);

                if (total > 0)
                {
                    logger.LogInformation("Search index: {Count} messages added", total);
                }

                if (DateTimeOffset.UtcNow - lastCleanup > TimeSpan.FromHours(1))
                {
                    index.RemoveStale();
                    lastCleanup = DateTimeOffset.UtcNow;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Search index could not be updated");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
}
