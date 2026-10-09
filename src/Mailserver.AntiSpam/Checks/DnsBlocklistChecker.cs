using System.Net;
using System.Net.Sockets;
using Mailserver.AntiSpam.Dns;

namespace Mailserver.AntiSpam.Checks;

/// <summary>Queries DNS-based blocklists (RFC 5782) for the connecting IP address.</summary>
public sealed class DnsBlocklistChecker(IDnsResolver dns)
{
    /// <summary>True if <paramref name="ip"/> is listed in <paramref name="zone"/>. Lookup failures count as "not listed".</summary>
    public async Task<bool> IsListedAsync(IPAddress ip, string zone, CancellationToken cancellationToken) =>
        (await LookupAsync(ip, zone, cancellationToken)).Count > 0;

    /// <summary>The return codes (e.g. 127.0.0.2) under which <paramref name="ip"/> is listed; empty if it is not.</summary>
    public async Task<IReadOnlyList<IPAddress>> LookupAsync(IPAddress ip, string zone, CancellationToken cancellationToken)
    {
        var result = await dns.GetAddressesAsync($"{ReverseName(ip)}.{zone}", cancellationToken);
        // Only 127.0.0.2–127.255.255.253 mean "listed". 127.255.255.x are error codes, e.g. Spamhaus refusing queries
        // that arrive through public resolvers; those must not reject legitimate mail.
        return result.Records.Where(a =>
        {
            var bytes = a.GetAddressBytes();
            return a.AddressFamily == AddressFamily.InterNetwork && bytes[0] == 127 && !(bytes[1] == 255 && bytes[2] == 255) &&
                   !(bytes[1] == 0 && bytes[2] == 0 && bytes[3] < 2);
        }).ToList();
    }

    /// <summary>
    /// Spamhaus ZEN combines several lists. Only the CSS (127.0.0.3: low reputation, often a shared relay of a large
    /// provider through which some customer sent spam) is weak enough that rejecting would also lose forwarded and
    /// legitimate mail; it only adds to the score.
    /// </summary>
    public static bool IsWeakSpamhausListing(string zone, IPAddress code) =>
        zone.EndsWith("spamhaus.org", StringComparison.OrdinalIgnoreCase) && code.Equals(IPAddress.Parse("127.0.0.3"));

    /// <summary>Name of a Spamhaus return code for logs, e.g. "SBL"; null for other lists.</summary>
    public static string? SpamhausList(IPAddress code) => code.GetAddressBytes() is [127, 0, 0, var last]
        ? last switch { 2 => "SBL", 3 => "CSS", >= 4 and <= 7 => "XBL", 9 => "DROP", 10 or 11 => "PBL", _ => null }
        : null;

    public static string ReverseName(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        var bytes = ip.GetAddressBytes();
        return ip.AddressFamily == AddressFamily.InterNetwork
            ? string.Join('.', bytes.Reverse())
            : string.Join('.', bytes.Reverse().SelectMany(b => new[] { b & 0xF, b >> 4 }).Select(n => n.ToString("x")));
    }
}
