using System.Net;
using Mailserver.Core.Accounts;
using Mailserver.Core.Data;
using Mailserver.Core.Routing;
using Mailserver.Core.SpamLogging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailserver.Core.Security;

public enum SendingDecision
{
    Allowed,
    /// <summary>Too many recipients in one message: rejected permanently, the user has to split it.</summary>
    TooManyRecipients,
    /// <summary>Hourly/daily limit reached without locking (relay clients, or locking switched off): try again later.</summary>
    Limited,
    /// <summary>The mailbox is locked; an administrator has to release it.</summary>
    Blocked,
}

public sealed record SendingCheck(SendingDecision Decision, string? Reason = null)
{
    public static readonly SendingCheck Allowed = new(SendingDecision.Allowed);
}

/// <summary>
/// Counts recipients outside this server per mailbox (or per IP for Smtp:RelayNetworks) over the last hour and day.
/// When a limit is exceeded the mailbox is locked and the administrators are told — the typical sign of a stolen password.
/// </summary>
public sealed class SendingLimiter(
    Database database,
    AccountStore accounts,
    MailboxSettingsStore mailboxSettings,
    AdminNotifier notifier,
    SpamLog spamLog,
    IOptions<MailserverOptions> options,
    TimeProvider timeProvider,
    ILogger<SendingLimiter> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Checks a message before it is sent and, if allowed, counts it.</summary>
    /// <param name="account">The sending mailbox; null for a relay client.</param>
    public async Task<SendingCheck> CheckAsync(Account? account, IPAddress? relayClient, IReadOnlyList<EmailAddress> recipients,
        CancellationToken cancellationToken = default)
    {
        var limits = options.Value.Security.Sending;
        var state = account is null ? SendingState.Default : mailboxSettings.Get(account.Id).Sending;
        if (state.IsBlocked)
        {
            return new SendingCheck(SendingDecision.Blocked,
                $"Der Versand für {account!.Address} ist gesperrt ({state.BlockedReason}). Bitte an den Administrator wenden.");
        }

        var external = recipients.Distinct().Count(r => !accounts.IsLocalDomain(r.Domain));
        if (external == 0)
        {
            return SendingCheck.Allowed;
        }

        if (limits.MaxRecipientsPerMessage > 0 && external > limits.MaxRecipientsPerMessage)
        {
            return new SendingCheck(SendingDecision.TooManyRecipients,
                $"Höchstens {limits.MaxRecipientsPerMessage} externe Empfänger pro Nachricht (diese hat {external}).");
        }

        var perHour = state.PerHour ?? limits.MaxRecipientsPerHour;
        var perDay = state.PerDay ?? limits.MaxRecipientsPerDay;
        var key = account is null ? $"relay:{relayClient}" : $"account:{account.Id}";
        var now = timeProvider.GetUtcNow();

        string? exceeded;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var connection = database.Open();
            connection.Execute("DELETE FROM send_log WHERE sent_utc < $cutoff", ("$cutoff", (now - TimeSpan.FromDays(1)).ToDbTime()));
            long Sum(TimeSpan window) => Convert.ToInt64(connection.Scalar(
                "SELECT COALESCE(SUM(recipients), 0) FROM send_log WHERE sender_key = $key AND sent_utc >= $since",
                ("$key", key), ("$since", (now - window).ToDbTime())));

            var hour = Sum(TimeSpan.FromHours(1));
            var day = Sum(TimeSpan.FromDays(1));
            exceeded = perHour > 0 && hour + external > perHour ? $"mehr als {perHour} externe Empfänger in einer Stunde"
                : perDay > 0 && day + external > perDay ? $"mehr als {perDay} externe Empfänger an einem Tag"
                : null;
            if (exceeded is null)
            {
                connection.Execute("INSERT INTO send_log (sender_key, sent_utc, recipients) VALUES ($key, $now, $count)",
                    ("$key", key), ("$now", now.ToDbTime()), ("$count", external));
                return SendingCheck.Allowed;
            }
        }
        finally
        {
            _gate.Release();
        }

        var who = account?.Address.ToString() ?? $"Relay {relayClient}";
        logger.LogWarning("Sending limit for {Sender}: {Reason}", who, exceeded);
        spamLog.Write(new SpamLogEntry
        {
            Stage = SpamLogStage.Submission, Action = SpamLogAction.Rejected, ClientIp = relayClient?.ToString(), MailFrom = account?.Address.ToString(),
            Recipient = string.Join(", ", recipients.Take(20)), Tests = "SEND_LIMIT", Detail = $"Versandlimit: {exceeded}",
        });

        if (account is not null && limits.BlockOnLimit)
        {
            mailboxSettings.BlockSending(account.Id, exceeded);
            await notifier.NotifyAsync($"Versand für {account.Address} gesperrt",
                $"""
                Das Postfach {account.Address} hat {exceeded} versendet und wurde für den Versand gesperrt.

                Das passiert typischerweise, wenn das Passwort in falsche Hände geraten ist und das Postfach für Spam benutzt wird.
                Empfehlung: Passwort ändern, im Verlauf (Admin → Verlauf, Suche nach der Adresse) die letzten Sendungen prüfen und
                die Sperre danach unter Admin → Postfächer → {account.Address} aufheben.

                Empfangen kann das Postfach weiterhin.
                """, cancellationToken);
            return new SendingCheck(SendingDecision.Blocked,
                $"Versandlimit überschritten ({exceeded}) – der Versand für {account.Address} ist gesperrt. Bitte an den Administrator wenden.");
        }

        return new SendingCheck(SendingDecision.Limited, $"Versandlimit erreicht ({exceeded}). Bitte später erneut versuchen.");
    }

    /// <summary>External recipients counted for a mailbox in the last hour and day.</summary>
    public (long Hour, long Day) Usage(long accountId)
    {
        var now = timeProvider.GetUtcNow();
        using var connection = database.Open();
        long Sum(TimeSpan window) => Convert.ToInt64(connection.Scalar(
            "SELECT COALESCE(SUM(recipients), 0) FROM send_log WHERE sender_key = $key AND sent_utc >= $since",
            ("$key", $"account:{accountId}"), ("$since", (now - window).ToDbTime())));
        return (Sum(TimeSpan.FromHours(1)), Sum(TimeSpan.FromDays(1)));
    }
}
