using Mailserver.Core.Accounts;
using Mailserver.Core.Queue;
using Mailserver.Core.Storage;
using Microsoft.Extensions.Logging;

namespace Mailserver.Core.Routing;

/// <summary>
/// Hands an accepted message to local mailboxes and/or the outbound queue.
/// </summary>
public sealed class MessageRouter(
    AccountStore accounts,
    MailboxStore mailboxes,
    OutboundQueue queue,
    ILogger<MessageRouter> logger)
{
    /// <param name="message">The complete message including the Received header added by this server.</param>
    /// <param name="envelopeSender">MAIL FROM; empty for bounces.</param>
    /// <param name="recipients">RCPT TO addresses.</param>
    /// <param name="allowRelay">True for authenticated submissions and server-generated mail; false for inbound mail on port 25.</param>
    public async Task RouteAsync(byte[] message, string envelopeSender, IReadOnlyList<EmailAddress> recipients, bool allowRelay,
        CancellationToken cancellationToken)
    {
        var localTargets = new Dictionary<long, Account>();
        var relay = new List<EmailAddress>();
        var forwards = new List<(EmailAddress Alias, EmailAddress Target)>();

        foreach (var recipient in recipients.Distinct())
        {
            if (!accounts.IsLocalDomain(recipient.Domain))
            {
                if (!allowRelay)
                {
                    throw new InvalidOperationException($"Relaying to {recipient} is not allowed for this session.");
                }

                relay.Add(recipient);
                continue;
            }

            var resolution = accounts.Resolve(recipient);
            if (resolution.IsEmpty)
            {
                // Only possible for server-generated mail (e.g. a bounce to a deleted account); SMTP sessions are filtered earlier.
                logger.LogWarning("Dropping message for unknown local recipient {Recipient}", recipient);
                continue;
            }

            foreach (var account in resolution.LocalAccounts)
            {
                localTargets.TryAdd(account.Id, account);
            }

            forwards.AddRange(resolution.ExternalAddresses.Select(target => (recipient, target)));
        }

        foreach (var account in localTargets.Values)
        {
            var stored = await mailboxes.AppendAsync(account, message, MailboxStore.Inbox, cancellationToken: cancellationToken);
            logger.LogInformation("Delivered message to {Account} (uid {Uid})", account.Address, stored.Uid);
        }

        if (relay.Count > 0)
        {
            await queue.EnqueueAsync(message, envelopeSender, relay, cancellationToken);
            logger.LogInformation("Queued message from <{Sender}> for {Count} remote recipient(s)", envelopeSender, relay.Count);
        }

        // Forwarded mail is sent with the alias as envelope sender, so SPF at the destination checks our domain instead of
        // failing for the original sender's domain. Bounces of forwards therefore come back to the alias, never loop.
        foreach (var group in forwards.GroupBy(f => f.Alias))
        {
            var sender = envelopeSender.Length == 0 ? "" : group.Key.ToString();
            await queue.EnqueueAsync(message, sender, group.Select(f => f.Target), cancellationToken);
            logger.LogInformation("Forwarding message for {Alias} to {Targets}", group.Key, string.Join(", ", group.Select(f => f.Target)));
        }
    }
}
