using System.Net;
using DnsClient;
using DnsClient.Protocol;

namespace Mailserver.AntiSpam.Dns;

public sealed class DnsClientResolver(ILookupClient client) : IDnsResolver
{
    public Task<DnsResult<string>> GetTxtAsync(string name, CancellationToken cancellationToken) =>
        QueryAsync(name, QueryType.TXT, r => r.Answers.TxtRecords().Select(t => string.Concat(t.Text)), cancellationToken);

    public async Task<DnsResult<IPAddress>> GetAddressesAsync(string name, CancellationToken cancellationToken)
    {
        var v4 = await QueryAsync(name, QueryType.A, r => r.Answers.ARecords().Select(a => a.Address), cancellationToken);
        if (v4.Status == DnsStatus.NotFound)
        {
            return v4;
        }

        var v6 = await QueryAsync(name, QueryType.AAAA, r => r.Answers.AaaaRecords().Select(a => a.Address), cancellationToken);
        var all = v4.Records.Concat(v6.Records).ToList();
        var status = all.Count > 0 ? DnsStatus.Ok : v4.Status == DnsStatus.Error || v6.Status == DnsStatus.Error ? DnsStatus.Error : DnsStatus.Ok;
        return new DnsResult<IPAddress>(status, all);
    }

    public Task<DnsResult<MxRecord>> GetMxAsync(string name, CancellationToken cancellationToken) =>
        QueryAsync(name, QueryType.MX, r => r.Answers.MxRecords().Select(m => new MxRecord(m.Preference, m.Exchange.Value.TrimEnd('.'))),
            cancellationToken);

    public async Task<DnsResult<string>> GetPtrAsync(IPAddress address, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.QueryReverseAsync(address, cancellationToken);
            return Convert(response, r => r.Answers.PtrRecords().Select(p => p.PtrDomainName.Value.TrimEnd('.')));
        }
        catch (DnsResponseException)
        {
            return DnsResult<string>.Failed;
        }
    }

    private async Task<DnsResult<T>> QueryAsync<T>(string name, QueryType type, Func<IDnsQueryResponse, IEnumerable<T>> select,
        CancellationToken cancellationToken)
    {
        try
        {
            return Convert(await client.QueryAsync(name, type, cancellationToken: cancellationToken), select);
        }
        catch (Exception ex) when (ex is DnsResponseException or ArgumentException)
        {
            return DnsResult<T>.Failed;
        }
    }

    private static DnsResult<T> Convert<T>(IDnsQueryResponse response, Func<IDnsQueryResponse, IEnumerable<T>> select)
    {
        if (response.Header.ResponseCode == DnsHeaderResponseCode.NotExistentDomain)
        {
            return new DnsResult<T>(DnsStatus.NotFound, []);
        }

        return response.HasError ? DnsResult<T>.Failed : new DnsResult<T>(DnsStatus.Ok, select(response).ToList());
    }
}
