using System.Net;
using System.Net.Sockets;
using Mailserver.AntiSpam.Dns;

namespace Mailserver.AntiSpam.Checks;

/// <summary>Queries DNS-based blocklists (RFC 5782) for the connecting IP address.</summary>
public sealed class DnsBlocklistChecker(IDnsResolver dns)
{
    /// <summary>True if <paramref name="ip"/> is listed in <paramref name="zone"/>. Lookup failures count as "not listed".</summary>
    public async Task<bool> IsListedAsync(IPAddress ip, string zone, CancellationToken cancellationToken)
    {
        var result = await dns.GetAddressesAsync($"{ReverseName(ip)}.{zone}", cancellationToken);
        // Only 127.0.0.2–127.255.255.253 mean "listed". 127.255.255.x are error codes, e.g. Spamhaus refusing queries
        // that arrive through public resolvers; those must not reject legitimate mail.
        return result.Records.Any(a =>
        {
            var bytes = a.GetAddressBytes();
            return a.AddressFamily == AddressFamily.InterNetwork && bytes[0] == 127 && !(bytes[1] == 255 && bytes[2] == 255) &&
                   !(bytes[1] == 0 && bytes[2] == 0 && bytes[3] < 2);
        });
    }

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
