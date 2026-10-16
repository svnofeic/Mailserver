using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Mailserver.Web.Dav;

/// <summary>One request of a calendar or contacts program.</summary>
public sealed record DavRequest(DateTimeOffset Time, string Client, string Method, string What, int Status);

/// <summary>A device or program that synchronises calendars or contacts, with its last requests.</summary>
public sealed record DavClient(string Name, DateTimeOffset LastSeen, int LastStatus, IReadOnlyList<DavRequest> Requests)
{
    public bool LastOk => LastStatus is >= 200 and < 400;
}

/// <summary>
/// What the phones and programs of a mailbox did lately (kept in memory, since the start of the service), so the user can
/// see whether the iPhone syncs at all and what came of it.
/// </summary>
public sealed partial class DavActivity
{
    private const int PerAccount = 60;
    private readonly ConcurrentDictionary<long, ConcurrentQueue<DavRequest>> _requests = new();

    public void Record(long accountId, string? userAgent, string method, string what, int status)
    {
        var queue = _requests.GetOrAdd(accountId, _ => new ConcurrentQueue<DavRequest>());
        queue.Enqueue(new DavRequest(DateTimeOffset.UtcNow, ClientName(userAgent), method, what, status));
        while (queue.Count > PerAccount && queue.TryDequeue(out _))
        {
        }
    }

    /// <summary>The programs of the mailbox, the most recent first, each with its requests (newest first).</summary>
    public IReadOnlyList<DavClient> Clients(long accountId)
    {
        if (!_requests.TryGetValue(accountId, out var queue))
        {
            return [];
        }

        return queue.ToArray().GroupBy(r => r.Client)
            .Select(g =>
            {
                var list = g.OrderByDescending(r => r.Time).ToList();
                return new DavClient(g.Key, list[0].Time, list[0].Status, list);
            })
            .OrderByDescending(c => c.LastSeen).ToList();
    }

    /// <summary>"iPhone/iPad (iOS 26.0)", "Mac (macOS 15.1)", "Thunderbird", "DAVx⁵ (Android)" … from the User-Agent.</summary>
    public static string ClientName(string? userAgent)
    {
        var agent = userAgent ?? "";
        if (Ios().Match(agent) is { Success: true } ios)
        {
            return $"iPhone/iPad (iOS {ios.Groups[1].Value})";
        }

        if (MacOs().Match(agent) is { Success: true } mac)
        {
            return $"Mac (macOS {mac.Groups[1].Value})";
        }

        if (agent.Contains("DAVx5", StringComparison.OrdinalIgnoreCase) || agent.Contains("DAVdroid", StringComparison.OrdinalIgnoreCase))
        {
            return "DAVx⁵ (Android)";
        }

        if (agent.Contains("Thunderbird", StringComparison.OrdinalIgnoreCase))
        {
            return "Thunderbird";
        }

        if (agent.Contains("CalDavSynchronizer", StringComparison.OrdinalIgnoreCase))
        {
            return "Outlook (CalDav Synchronizer)";
        }

        var first = agent.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrEmpty(first) ? "Unbekanntes Programm" : first.Length > 60 ? first[..60] : first;
    }

    [GeneratedRegex(@"\biOS/(\d+(?:\.\d+)*)", RegexOptions.IgnoreCase)]
    private static partial Regex Ios();

    [GeneratedRegex(@"\b(?:macOS|Mac OS X|Mac_OS_X)/(\d+(?:[._]\d+)*)", RegexOptions.IgnoreCase)]
    private static partial Regex MacOs();
}
