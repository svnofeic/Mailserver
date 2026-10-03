using System.Net;
using System.Net.Sockets;
using Mailserver.Core;
using Mailserver.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Mailserver.AntiSpam.Checks;

/// <summary>
/// Greylisting: the first attempt for an unknown (network, sender, recipient) triplet is deferred. Real mail servers retry
/// after a few minutes; most spam bots do not. Networks are /24 (IPv4) or /64 (IPv6) because large senders retry from
/// different addresses of the same pool.
/// </summary>
public sealed class Greylist(Database database, IOptions<MailserverOptions> options, TimeProvider timeProvider)
{
    private DateTimeOffset _nextCleanup;

    /// <summary>True if the message may be accepted now; false means "try again later".</summary>
    public bool Check(IPAddress ip, string sender, string recipient)
    {
        var settings = options.Value.Spam.Greylisting;
        var now = timeProvider.GetUtcNow();
        var triplet = $"{Network(ip)}|{sender.ToLowerInvariant()}|{recipient.ToLowerInvariant()}";

        using var connection = database.Open();
        Cleanup(connection, now, settings);

        var row = Query(connection, triplet);
        if (row is null)
        {
            Execute(connection, "INSERT OR IGNORE INTO greylist (triplet, first_seen_utc, last_seen_utc, passed) VALUES ($t, $now, $now, 0)",
                triplet, now);
            return false;
        }

        var (firstSeen, passed) = row.Value;
        if (!passed && now - firstSeen < settings.Delay)
        {
            return false;
        }

        Execute(connection, "UPDATE greylist SET passed = 1, last_seen_utc = $now WHERE triplet = $t", triplet, now);
        return true;
    }

    private void Cleanup(SqliteConnection connection, DateTimeOffset now, GreylistingOptions settings)
    {
        if (now < _nextCleanup)
        {
            return;
        }

        _nextCleanup = now.AddHours(1);
        using var command = connection.CreateCommand();
        // Passed entries expire after inactivity; never-retried attempts (typical for spam) after a day.
        command.CommandText = "DELETE FROM greylist WHERE (passed = 1 AND last_seen_utc < $expired) OR (passed = 0 AND first_seen_utc < $stale)";
        command.Parameters.AddWithValue("$expired", Format(now - settings.Expiry));
        command.Parameters.AddWithValue("$stale", Format(now.AddDays(-1)));
        command.ExecuteNonQuery();
    }

    private static (DateTimeOffset FirstSeen, bool Passed)? Query(SqliteConnection connection, string triplet)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT first_seen_utc, passed FROM greylist WHERE triplet = $t";
        command.Parameters.AddWithValue("$t", triplet);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (DateTimeOffset.Parse(reader.GetString(0), System.Globalization.CultureInfo.InvariantCulture), reader.GetInt64(1) != 0) : null;
    }

    private static void Execute(SqliteConnection connection, string sql, string triplet, DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$t", triplet);
        command.Parameters.AddWithValue("$now", Format(now));
        command.ExecuteNonQuery();
    }

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture);

    private static string Network(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            bytes[3] = 0;
            return new IPAddress(bytes) + "/24";
        }

        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }
}
