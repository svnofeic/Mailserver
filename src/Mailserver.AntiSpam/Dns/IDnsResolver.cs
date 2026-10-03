using System.Net;

namespace Mailserver.AntiSpam.Dns;

public enum DnsStatus
{
    Ok,
    /// <summary>The name does not exist (NXDOMAIN).</summary>
    NotFound,
    /// <summary>Timeout or server failure; the result is unknown.</summary>
    Error,
}

public sealed record DnsResult<T>(DnsStatus Status, IReadOnlyList<T> Records)
{
    public static DnsResult<T> Failed { get; } = new(DnsStatus.Error, []);
}

public sealed record MxRecord(int Preference, string Host);

/// <summary>The DNS lookups the spam checks need. Abstracted so tests can supply records without a network.</summary>
public interface IDnsResolver
{
    /// <summary>TXT records, each with its character-strings already joined.</summary>
    Task<DnsResult<string>> GetTxtAsync(string name, CancellationToken cancellationToken);

    /// <summary>A and AAAA records.</summary>
    Task<DnsResult<IPAddress>> GetAddressesAsync(string name, CancellationToken cancellationToken);

    Task<DnsResult<MxRecord>> GetMxAsync(string name, CancellationToken cancellationToken);

    Task<DnsResult<string>> GetPtrAsync(IPAddress address, CancellationToken cancellationToken);
}
