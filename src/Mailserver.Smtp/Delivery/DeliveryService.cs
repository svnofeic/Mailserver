using Mailserver.Core;
using Mailserver.Core.Queue;
using Mailserver.Core.Routing;
using Mailserver.Core.SpamLogging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Mailserver.Smtp.Delivery;

/// <summary>
/// Works through the outbound queue: delivers, retries with backoff and bounces after permanent failures or expiry.
/// </summary>
public sealed class DeliveryService(
    OutboundQueue queue,
    RemoteDeliveryClient client,
    MessageRouter router,
    IOptions<MailserverOptions> options,
    TimeProvider timeProvider,
    SpamLog spamLog,
    ILogger<DeliveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Queue run failed");
            }

            try
            {
                await queue.WaitForWorkAsync(options.Value.Delivery.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task ProcessDueAsync(CancellationToken cancellationToken)
    {
        var batches = queue.GetDueBatches(timeProvider.GetUtcNow());
        if (batches.Count == 0)
        {
            return;
        }

        await Parallel.ForEachAsync(batches,
            new ParallelOptions { MaxDegreeOfParallelism = options.Value.Delivery.MaxParallelDeliveries, CancellationToken = cancellationToken },
            async (batch, ct) => await ProcessBatchAsync(batch, ct));
    }

    private async Task ProcessBatchAsync(DeliveryBatch batch, CancellationToken cancellationToken)
    {
        MimeMessage message;
        try
        {
            await using var stream = queue.OpenMessage(batch.MessageFile);
            message = await MimeMessage.LoadAsync(stream, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            logger.LogError("Queued message file {File} is missing; dropping {Count} entries", batch.MessageFile, batch.Entries.Count);
            queue.Complete(batch.Entries);
            return;
        }

        var results = await client.DeliverAsync(batch, message, cancellationToken);

        var delivered = results.Where(r => r.Outcome == DeliveryOutcome.Delivered).Select(r => r.Entry).ToList();
        var expiry = timeProvider.GetUtcNow() - options.Value.Delivery.MaxQueueLifetime;
        var failed = results
            .Where(r => r.Outcome == DeliveryOutcome.PermanentFailure ||
                        (r.Outcome == DeliveryOutcome.TemporaryFailure && r.Entry.Created < expiry))
            .ToList();
        var deferred = results.Where(r => r.Outcome == DeliveryOutcome.TemporaryFailure && !failed.Contains(r)).ToList();

        LogOutbound(batch, message, results.Where(r => r.Outcome == DeliveryOutcome.Delivered).ToList(), SpamLogAction.Sent);
        LogOutbound(batch, message, deferred, SpamLogAction.Deferred);
        LogOutbound(batch, message, failed, SpamLogAction.Failed);

        if (failed.Count > 0)
        {
            await BounceAsync(batch, failed, cancellationToken);
        }

        queue.Complete(delivered.Concat(failed.Select(r => r.Entry)));

        foreach (var group in deferred.GroupBy(r => r.Error))
        {
            logger.LogInformation("Deferred {Count} recipient(s) at {Domain}: {Error}", group.Count(), batch.Domain, group.Key);
            queue.Defer(group.Select(r => r.Entry), group.Key ?? "unknown error");
        }
    }

    private void LogOutbound(DeliveryBatch batch, MimeMessage message, IReadOnlyList<RecipientResult> results, string action)
    {
        foreach (var group in results.GroupBy(r => r.Error))
        {
            spamLog.Write(new SpamLogEntry
            {
                Stage = SpamLogStage.Outbound,
                Action = action,
                MailFrom = batch.Sender,
                Recipient = string.Join(", ", group.Select(r => r.Entry.Recipient)),
                HeaderFrom = message.From.Mailboxes.FirstOrDefault()?.Address,
                Subject = message.Subject,
                MessageId = message.MessageId,
                Detail = group.Key ?? $"zugestellt an {batch.Domain}",
            });
        }
    }

    private async Task BounceAsync(DeliveryBatch batch, IReadOnlyList<RecipientResult> failed, CancellationToken cancellationToken)
    {
        foreach (var result in failed)
        {
            logger.LogWarning("Giving up on {Recipient}: {Error}", result.Entry.Recipient, result.Error);
        }

        // Never bounce a bounce (RFC 5321 6.1) – this is what prevents loops.
        if (!EmailAddress.TryParse(batch.Sender, out var sender))
        {
            return;
        }

        try
        {
            await using var original = queue.OpenMessage(batch.MessageFile);
            var bounce = await BounceFactory.CreateAsync(options.Value.Hostname, sender, original,
                failed.Select(r => (r.Entry.Recipient, r.Error ?? "unknown error")).ToList(), cancellationToken);
            await router.RouteAsync(bounce, "", [sender], allowRelay: true, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not create bounce for {Sender}", sender);
        }
    }
}
