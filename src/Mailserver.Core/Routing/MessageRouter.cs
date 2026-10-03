using Mailserver.Core.Accounts;
using Mailserver.Core.Queue;
using Mailserver.Core.Rules;
using Mailserver.Core.Storage;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Mailserver.Core.Routing;

/// <summary>
/// Hands an accepted message to local mailboxes and/or the outbound queue.
/// </summary>
public sealed class MessageRouter(
    AccountStore accounts,
    MailboxStore mailboxes,
    OutboundQueue queue,
    RuleStore rules,
    ILogger<MessageRouter> logger)
{
    /// <param name="message">The complete message including the Received header added by this server.</param>
    /// <param name="envelopeSender">MAIL FROM; empty for bounces.</param>
    /// <param name="recipients">RCPT TO addresses.</param>
    /// <param name="allowRelay">True for authenticated submissions and server-generated mail; false for inbound mail on port 25.</param>
    /// <param name="verdict">Spam check result for mail from other servers; null for trusted sources.</param>
    public async Task RouteAsync(byte[] message, string envelopeSender, IReadOnlyList<EmailAddress> recipients, bool allowRelay,
        CancellationToken cancellationToken, InboundVerdict? verdict = null)
    {
        verdict ??= InboundVerdict.Clean;
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

        RuleSubject? subject = null;
        foreach (var account in localTargets.Values)
        {
            var decision = Decide(account, message, verdict, ref subject);
            if (decision.Discard || (verdict.Discard && !decision.NotSpam))
            {
                logger.LogInformation("Discarded message for {Account} (score {Score:F1}, rules: {Rules})", account.Address, verdict.Score,
                    string.Join(", ", decision.MatchedRules));
                continue;
            }

            var stored = await mailboxes.AppendAsync(account, message, decision.Folder ?? MailboxStore.Inbox,
                MessageFlags.Format(decision.Flags), cancellationToken: cancellationToken);
            logger.LogInformation("Delivered message to {Account} in {Folder} (uid {Uid}, score {Score:F1}{Rules})", account.Address,
                decision.Folder, stored.Uid, verdict.Score, decision.MatchedRules.Count > 0 ? ", rules: " + string.Join(", ", decision.MatchedRules) : "");
        }

        if (relay.Count > 0)
        {
            await queue.EnqueueAsync(message, envelopeSender, relay, cancellationToken);
            logger.LogInformation("Queued message from <{Sender}> for {Count} remote recipient(s)", envelopeSender, relay.Count);
        }

        // Forwarded mail is sent with the alias as envelope sender, so SPF at the destination checks our domain instead of
        // failing for the original sender's domain. Bounces of forwards therefore come back to the alias, never loop.
        if (verdict.IsSpam && forwards.Count > 0)
        {
            // Forwarding spam would damage this server's reputation at the destination.
            logger.LogInformation("Not forwarding spam (score {Score:F1}) to {Targets}", verdict.Score, string.Join(", ", forwards.Select(f => f.Target)));
            forwards.Clear();
        }

        foreach (var group in forwards.GroupBy(f => f.Alias))
        {
            var sender = envelopeSender.Length == 0 ? "" : group.Key.ToString();
            await queue.EnqueueAsync(message, sender, group.Select(f => f.Target), cancellationToken);
            logger.LogInformation("Forwarding message for {Alias} to {Targets}", group.Key, string.Join(", ", group.Select(f => f.Target)));
        }
    }

    private DeliveryDecision Decide(Account account, byte[] message, InboundVerdict verdict, ref RuleSubject? subject)
    {
        var applicable = rules.GetRulesFor(account.Address);
        if (applicable.Count == 0)
        {
            return new DeliveryDecision(verdict.IsSpam ? "Junk" : MailboxStore.Inbox, false, false, [], []);
        }

        subject ??= new RuleSubject(MimeMessage.Load(new MemoryStream(message, writable: false)), verdict.Score);
        return RuleEngine.Decide(applicable, subject, verdict.IsSpam);
    }
}
