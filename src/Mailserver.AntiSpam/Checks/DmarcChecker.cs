using Mailserver.AntiSpam.Dns;

namespace Mailserver.AntiSpam.Checks;

public enum DmarcResult
{
    None,
    Pass,
    Fail,
    TempError,
}

public enum DmarcPolicy
{
    None,
    Quarantine,
    Reject,
}

public sealed record DmarcOutcome(DmarcResult Result, DmarcPolicy Policy, string FromDomain);

/// <summary>DMARC evaluation (RFC 7489): the From domain must be aligned with a passing SPF or DKIM result.</summary>
public sealed class DmarcChecker(IDnsResolver dns)
{
    public async Task<DmarcOutcome> CheckAsync(string fromDomain, SpfOutcome spf, IReadOnlyList<DkimSignatureResult> dkim,
        CancellationToken cancellationToken)
    {
        fromDomain = fromDomain.ToLowerInvariant();
        var organizational = DomainHelper.OrganizationalDomain(fromDomain);

        var record = await FindRecordAsync(fromDomain, cancellationToken);
        var isSubdomainPolicy = false;
        if (record is { Status: DnsStatus.Ok, Records.Count: 0 } && organizational != fromDomain)
        {
            record = await FindRecordAsync(organizational, cancellationToken);
            isSubdomainPolicy = true;
        }

        if (record.Status == DnsStatus.Error)
        {
            return new DmarcOutcome(DmarcResult.TempError, DmarcPolicy.None, fromDomain);
        }

        if (record.Records.Count != 1)
        {
            return new DmarcOutcome(DmarcResult.None, DmarcPolicy.None, fromDomain);
        }

        var tags = DkimChecker.ParseTags(record.Records[0]);
        var policy = ParsePolicy(isSubdomainPolicy && tags.TryGetValue("sp", out var sp) ? sp : tags.GetValueOrDefault("p"));
        var strictSpf = tags.GetValueOrDefault("aspf", "r").Equals("s", StringComparison.OrdinalIgnoreCase);
        var strictDkim = tags.GetValueOrDefault("adkim", "r").Equals("s", StringComparison.OrdinalIgnoreCase);

        var spfAligned = spf.Result == SpfResult.Pass && DomainHelper.Aligned(spf.Domain, fromDomain, strictSpf);
        var dkimAligned = dkim.Any(d => d.Result == DkimResult.Pass && DomainHelper.Aligned(d.Domain, fromDomain, strictDkim));
        return new DmarcOutcome(spfAligned || dkimAligned ? DmarcResult.Pass : DmarcResult.Fail, policy, fromDomain);
    }

    private async Task<DnsResult<string>> FindRecordAsync(string domain, CancellationToken cancellationToken)
    {
        var result = await dns.GetTxtAsync("_dmarc." + domain, cancellationToken);
        if (result.Status == DnsStatus.NotFound)
        {
            return new DnsResult<string>(DnsStatus.Ok, []);
        }

        return result with
        {
            Records = result.Records.Where(r => r.TrimStart().StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase)).ToList(),
        };
    }

    private static DmarcPolicy ParsePolicy(string? value) => value?.ToLowerInvariant() switch
    {
        "reject" => DmarcPolicy.Reject,
        "quarantine" => DmarcPolicy.Quarantine,
        _ => DmarcPolicy.None,
    };
}
