using System.Collections.Concurrent;

namespace Mailserver.Web;

/// <summary>
/// Limits "Passwort vergessen" requests: a few per mailbox and per IP address per hour, so nobody can flood someone's external
/// mailbox with reset mails or use the form to probe addresses.
/// </summary>
public sealed class RecoveryThrottle(TimeProvider timeProvider)
{
    public const int PerMailbox = 3;
    public const int PerAddress = 10;
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _requests = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Counts the request; false if the mailbox or the IP address is over its limit.</summary>
    public bool TryRequest(string mailbox, string? ip) => Take($"m:{mailbox}", PerMailbox) & Take($"i:{ip}", PerAddress);

    private bool Take(string key, int limit)
    {
        var now = timeProvider.GetUtcNow();
        var times = _requests.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (times)
        {
            while (times.Count > 0 && now - times.Peek() > Window)
            {
                times.Dequeue();
            }

            if (times.Count >= limit)
            {
                return false;
            }

            times.Enqueue(now);
            return true;
        }
    }
}
