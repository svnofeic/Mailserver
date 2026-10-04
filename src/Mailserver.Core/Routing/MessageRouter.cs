using Mailserver.Core.Accounts;
using Mailserver.Core.Queue;
using Mailserver.Core.Rules;
using Mailserver.Core.SpamLogging;
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
    SpamLog spamLog,
    MailboxSettingsStore mailboxSettings,
    AutoResponder autoResponder,
    OutgoingMessagePreparer preparer,
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
        MimeMessage? parsed = null;
        var replies = new List<(MimeMessage Reply, EmailAddress To)>();
        foreach (var account in localTargets.Values)
        {
            var decision = Decide(account, message, verdict, ref subject);
            if (decision.Discard || (verdict.Discard && !decision.NotSpam))
            {
                logger.LogInformation("Discarded message for {Account} (score {Score:F1}, rules: {Rules})", account.Address, verdict.Score,
                    string.Join(", ", decision.MatchedRules));
                LogDelivery(verdict, account, SpamLogAction.Discarded, null, decision,
                    decision.Discard ? "rule" : "above delete threshold");
                continue;
            }

            // Forwarding and out-of-office replies apply to wanted mail only, never to spam.
            var settings = mailboxSettings.Get(account.Id);
            var isJunk = string.Equals(decision.Folder, "Junk", StringComparison.OrdinalIgnoreCase);
            var forwarding = settings.Forwarding.IsActive && !isJunk;
            if (forwarding)
            {
                await ForwardAsync(message, envelopeSender, account, settings.Forwarding, verdict, cancellationToken);
            }

            if (!forwarding || settings.Forwarding.KeepCopy)
            {
                var stored = await mailboxes.AppendAsync(account, message, decision.Folder ?? MailboxStore.Inbox,
                    MessageFlags.Format(decision.Flags), cancellationToken: cancellationToken);
                LogDelivery(verdict, account, SpamLogAction.Delivered, decision.Folder ?? MailboxStore.Inbox, decision,
                    decision.NotSpam && verdict.IsSpam ? "spam verdict overridden by rule" : null);
                logger.LogInformation("Delivered message to {Account} in {Folder} (uid {Uid}, score {Score:F1}{Rules})", account.Address,
                    decision.Folder, stored.Uid, verdict.Score, decision.MatchedRules.Count > 0 ? ", rules: " + string.Join(", ", decision.MatchedRules) : "");
            }

            if (settings.AutoReply.Enabled && !isJunk)
            {
                parsed ??= MimeMessage.Load(new MemoryStream(message, writable: false));
                if (autoResponder.CreateReply(account, settings.AutoReply, parsed, envelopeSender) is { } reply)
                {
                    replies.Add(reply);
                    LogAction(verdict, SpamLogAction.AutoReplied, account.Address.ToString(), $"Abwesenheitsnotiz an {reply.To}");
                }
            }
        }

        foreach (var (reply, to) in replies)
        {
            // Empty envelope sender (RFC 3834): a bounce of the notice goes nowhere, and no other autoresponder answers it.
            using var buffer = new MemoryStream();
            await reply.WriteToAsync(buffer, cancellationToken);
            var prepared = await preparer.PrepareAsync(buffer.ToArray(), cancellationToken);
            await RouteAsync(prepared, "", [to], allowRelay: true, cancellationToken);
            logger.LogInformation("Sent out-of-office reply to {Recipient}", to);
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
            if (verdict.TraceId is not null)
            {
                spamLog.Write(new SpamLogEntry
                {
                    Session = verdict.TraceId, Stage = SpamLogStage.Delivery, Action = SpamLogAction.NotForwarded, Score = verdict.Score,
                    Recipient = string.Join(", ", forwards.Select(f => f.Target)),
                });
            }

            forwards.Clear();
        }

        foreach (var group in forwards.GroupBy(f => f.Alias))
        {
            var sender = envelopeSender.Length == 0 ? "" : group.Key.ToString();
            await queue.EnqueueAsync(message, sender, group.Select(f => f.Target), cancellationToken);
            logger.LogInformation("Forwarding message for {Alias} to {Targets}", group.Key, string.Join(", ", group.Select(f => f.Target)));
        }
    }

    /// <summary>
    /// Mailbox forwarding: like an alias, external copies are sent with the mailbox as envelope sender (SPF passes, bounces
    /// return here). Local targets get the message directly; their own forwarding does not apply again, so two mailboxes
    /// forwarding to each other cannot loop.
    /// </summary>
    private async Task ForwardAsync(byte[] message, string envelopeSender, Account account, Forwarding forwarding, InboundVerdict verdict,
        CancellationToken cancellationToken)
    {
        var external = new List<EmailAddress>();
        foreach (var target in forwarding.Targets)
        {
            if (!accounts.IsLocalDomain(target.Domain))
            {
                external.Add(target);
                continue;
            }

            foreach (var local in accounts.Resolve(target).LocalAccounts.Where(a => a.Id != account.Id))
            {
                await mailboxes.AppendAsync(local, message, MailboxStore.Inbox, cancellationToken: cancellationToken);
            }

            external.AddRange(accounts.Resolve(target).ExternalAddresses);
        }

        if (external.Count > 0)
        {
            await queue.EnqueueAsync(message, envelopeSender.Length == 0 ? "" : account.Address.ToString(), external.Distinct(), cancellationToken);
        }

        logger.LogInformation("Forwarding message for {Account} to {Targets}", account.Address, string.Join(", ", forwarding.Targets));
        LogAction(verdict, SpamLogAction.Forwarded, account.Address.ToString(),
            $"weitergeleitet an {string.Join(", ", forwarding.Targets)}{(forwarding.KeepCopy ? "" : " (ohne Kopie im Postfach)")}");
    }

    private void LogAction(InboundVerdict verdict, string action, string recipient, string detail)
    {
        if (verdict.TraceId is not null)
        {
            spamLog.Write(new SpamLogEntry
            {
                Session = verdict.TraceId, Stage = SpamLogStage.Delivery, Action = action, Recipient = recipient, Score = verdict.Score,
                Detail = detail,
            });
        }
    }

    /// <summary>Only mail checked by the spam filter is logged; it carries the session id that links to the check entries.</summary>
    private void LogDelivery(InboundVerdict verdict, Account account, string action, string? folder, DeliveryDecision decision, string? detail)
    {
        if (verdict.TraceId is null)
        {
            return;
        }

        spamLog.Write(new SpamLogEntry
        {
            Session = verdict.TraceId,
            Stage = SpamLogStage.Delivery,
            Action = action,
            Recipient = account.Address.ToString(),
            Score = verdict.Score,
            Folder = folder,
            Rules = decision.MatchedRules.Count == 0 ? null : string.Join(" | ", decision.MatchedRules),
            Detail = detail,
        });
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
