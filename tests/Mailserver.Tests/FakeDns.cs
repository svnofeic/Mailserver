using System.Net;
using Mailserver.AntiSpam.Dns;

namespace Mailserver.Tests;

/// <summary>In-memory DNS for spam filter tests. Unknown names are NXDOMAIN.</summary>
public sealed class FakeDns : IDnsResolver
{
    public Dictionary<string, List<string>> Txt { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<IPAddress>> Addresses { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<MxRecord>> Mx { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Ptr { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Failing { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Queries { get; private set; }

    public FakeDns AddTxt(string name, string value)
    {
        (Txt.TryGetValue(name, out var list) ? list : Txt[name] = []).Add(value);
        return this;
    }

    public FakeDns AddA(string name, params string[] addresses)
    {
        (Addresses.TryGetValue(name, out var list) ? list : Addresses[name] = []).AddRange(addresses.Select(IPAddress.Parse));
        return this;
    }

    public Task<DnsResult<string>> GetTxtAsync(string name, CancellationToken cancellationToken) => Lookup(Txt, name);

    public Task<DnsResult<IPAddress>> GetAddressesAsync(string name, CancellationToken cancellationToken) => Lookup(Addresses, name);

    public Task<DnsResult<MxRecord>> GetMxAsync(string name, CancellationToken cancellationToken) => Lookup(Mx, name);

    public Task<DnsResult<string>> GetPtrAsync(IPAddress address, CancellationToken cancellationToken) =>
        Task.FromResult(Ptr.TryGetValue(address.ToString(), out var name)
            ? new DnsResult<string>(DnsStatus.Ok, [name])
            : new DnsResult<string>(DnsStatus.NotFound, []));

    private Task<DnsResult<T>> Lookup<T>(Dictionary<string, List<T>> table, string name)
    {
        Queries++;
        name = name.TrimEnd('.');
        if (Failing.Contains(name))
        {
            return Task.FromResult(DnsResult<T>.Failed);
        }

        return Task.FromResult(table.TryGetValue(name, out var records)
            ? new DnsResult<T>(DnsStatus.Ok, records)
            : new DnsResult<T>(DnsStatus.NotFound, []));
    }
}

public sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}
