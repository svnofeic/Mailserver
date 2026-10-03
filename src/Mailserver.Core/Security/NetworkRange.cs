using System.Net;
using System.Net.Sockets;

namespace Mailserver.Core.Security;

/// <summary>An address range in CIDR notation ("192.0.2.0/24", "2001:db8::/32").</summary>
public readonly record struct NetworkRange(IPAddress Network, int PrefixLength)
{
    public static NetworkRange Parse(string value)
    {
        var slash = value.IndexOf('/');
        var address = IPAddress.Parse(slash < 0 ? value : value[..slash]);
        var max = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefix = slash < 0 ? max : int.Parse(value[(slash + 1)..]);
        if (prefix < 0 || prefix > max)
        {
            throw new FormatException($"Invalid prefix length in {value}");
        }

        return new NetworkRange(address, prefix);
    }

    public bool Contains(IPAddress address) => Matches(Network, address, PrefixLength);

    /// <summary>True if both addresses share the first <paramref name="prefixLength"/> bits (IPv4-mapped IPv6 is treated as IPv4).</summary>
    public static bool Matches(IPAddress network, IPAddress address, int prefixLength)
    {
        if (network.IsIPv4MappedToIPv6) network = network.MapToIPv4();
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (network.AddressFamily != address.AddressFamily)
        {
            return false;
        }

        var a = network.GetAddressBytes();
        var b = address.GetAddressBytes();
        var fullBytes = prefixLength / 8;
        for (var i = 0; i < fullBytes; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        var remainingBits = prefixLength % 8;
        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (a[fullBytes] & mask) == (b[fullBytes] & mask);
    }
}
