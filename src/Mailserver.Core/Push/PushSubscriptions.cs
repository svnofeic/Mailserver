using Mailserver.Core.Data;

namespace Mailserver.Core.Push;

/// <summary>A browser or installed app that receives push messages for a mailbox.</summary>
public sealed record PushSubscription(long Id, long AccountId, string Endpoint, string P256dh, string Auth, string Device,
    DateTimeOffset Created, DateTimeOffset? LastSent, int Failures, string? LastError = null);

public sealed class PushSubscriptionStore(Database database, TimeProvider timeProvider)
{
    private const string Columns = "id, account_id, endpoint, p256dh, auth, device, created_utc, last_sent_utc, failures, last_error";

    /// <summary>Adds a device or updates it (same endpoint = same browser).</summary>
    public PushSubscription Save(long accountId, string endpoint, string p256dh, string auth, string device)
    {
        // Push services are always HTTPS; plain HTTP only on this machine (tests).
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && !(uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp)))
        {
            throw new ArgumentException("Ungültige Push-Adresse.");
        }

        if (WebPushCrypto.FromBase64Url(p256dh).Length != 65 || WebPushCrypto.FromBase64Url(auth).Length != 16)
        {
            throw new ArgumentException("Ungültige Push-Schlüssel.");
        }

        using var connection = database.Open();
        connection.Execute(
            """
            INSERT INTO push_subscriptions (account_id, endpoint, p256dh, auth, device, created_utc, failures)
            VALUES ($account, $endpoint, $p256dh, $auth, $device, $now, 0)
            ON CONFLICT (endpoint) DO UPDATE SET account_id = $account, p256dh = $p256dh, auth = $auth, device = $device, failures = 0, last_error = NULL
            """,
            ("$account", accountId), ("$endpoint", endpoint), ("$p256dh", p256dh), ("$auth", auth), ("$device", device),
            ("$now", timeProvider.GetUtcNow().ToDbTime()));
        return ForAccount(accountId).First(s => s.Endpoint == endpoint);
    }

    public IReadOnlyList<PushSubscription> ForAccount(long accountId)
    {
        using var connection = database.Open();
        return connection.Query($"SELECT {Columns} FROM push_subscriptions WHERE account_id = $account ORDER BY created_utc", Map, ("$account", accountId));
    }

    public bool Remove(long accountId, long id)
    {
        using var connection = database.Open();
        return connection.Execute("DELETE FROM push_subscriptions WHERE id = $id AND account_id = $account", ("$id", id), ("$account", accountId)) > 0;
    }

    public bool RemoveEndpoint(long accountId, string endpoint)
    {
        using var connection = database.Open();
        return connection.Execute("DELETE FROM push_subscriptions WHERE endpoint = $endpoint AND account_id = $account",
            ("$endpoint", endpoint), ("$account", accountId)) > 0;
    }

    /// <summary>Records the outcome of a delivery; a subscription that keeps failing is dropped.</summary>
    public void Record(long id, PushOutcome outcome, string? detail = null)
    {
        using var connection = database.Open();
        if (outcome == PushOutcome.Gone)
        {
            connection.Execute("DELETE FROM push_subscriptions WHERE id = $id", ("$id", id));
        }
        else if (outcome == PushOutcome.Sent)
        {
            connection.Execute("UPDATE push_subscriptions SET last_sent_utc = $now, failures = 0, last_error = NULL WHERE id = $id",
                ("$now", timeProvider.GetUtcNow().ToDbTime()), ("$id", id));
        }
        else
        {
            connection.Execute("UPDATE push_subscriptions SET failures = failures + 1, last_error = $error WHERE id = $id",
                ("$error", detail), ("$id", id));
            connection.Execute("DELETE FROM push_subscriptions WHERE id = $id AND failures >= 20", ("$id", id));
        }
    }

    private static PushSubscription Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3),
        r.GetString(4), r.GetString(5), r.GetDbTime(6), r.IsDBNull(7) ? null : r.GetDbTime(7), r.GetInt32(8),
        r.IsDBNull(9) ? null : r.GetString(9));
}
