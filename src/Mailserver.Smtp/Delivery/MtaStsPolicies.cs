using System.Collections.Concurrent;
using DnsClient;
using Mailserver.Core.Security;
using Microsoft.Extensions.Logging;

namespace Mailserver.Smtp.Delivery;

/// <summary>
/// MTA-STS policies of other domains (RFC 8461): if a domain publishes one with mode "enforce", mail goes only to the
/// MX hosts it names, and only over TLS with a valid certificate. Policies are kept for their max_age, so an attacker who
/// suppresses the DNS record cannot switch the protection off for a domain seen before.
/// </summary>
public sealed class MtaStsPolicies(ILookupClient dns, ILogger<MtaStsPolicies> logger)
{
    private sealed record Cached(string Id, MtaStsPolicy Policy, DateTimeOffset Expires);

    private const int MaxPolicyBytes = 64 * 1024;
    private readonly ConcurrentDictionary<string, Cached> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>TXT records of a name (empty if there are none or DNS fails); replaceable in tests.</summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<string>>> LookupTxt { get; set; } = async (name, cancellationToken) =>
    {
        try
        {
            var response = await dns.QueryAsync(name, QueryType.TXT, cancellationToken: cancellationToken);
            return response.Answers.TxtRecords().Select(r => string.Concat(r.Text)).ToList();
        }
        catch (DnsResponseException)
        {
            return [];
        }
    };

    /// <summary>Loads the policy text from https://mta-sts.&lt;domain&gt;/.well-known/mta-sts.txt; replaceable in tests.</summary>
    public Func<string, CancellationToken, Task<string?>> Fetch { get; set; } = FetchAsync;

    public async Task<MtaStsPolicy?> GetAsync(string domain, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        _cache.TryGetValue(domain, out var cached);
        if (cached is not null && cached.Expires <= now)
        {
            _cache.TryRemove(domain, out _);
            cached = null;
        }

        var records = (await LookupTxt($"_mta-sts.{domain}", cancellationToken)).Where(t => t.StartsWith("v=STSv1", StringComparison.Ordinal)).ToList();
        var id = records.Count == 1 ? records[0].Split(';').Select(p => p.Trim()).FirstOrDefault(p => p.StartsWith("id=", StringComparison.Ordinal))?[3..] : null;

        // No (readable) record: a remembered policy still applies until it expires.
        if (id is null || (cached is not null && cached.Id == id))
        {
            return cached?.Policy;
        }

        var text = await Fetch(domain, cancellationToken);
        if (text is null || MtaStsPolicy.Parse(text) is not { } policy)
        {
            logger.LogInformation("MTA-STS policy of {Domain} could not be loaded; {Fallback}", domain, cached is null ? "delivering without" : "keeping the known one");
            return cached?.Policy;
        }

        if (policy.Mode == "none")
        {
            _cache.TryRemove(domain, out _);
            return null;
        }

        _cache[domain] = new Cached(id, policy, now + policy.MaxAge);
        return policy;
    }

    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };

    private static async Task<string?> FetchAsync(string domain, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.GetAsync($"https://mta-sts.{domain}/.well-known/mta-sts.txt", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "text/plain" ||
                response.Content.Headers.ContentLength > MaxPolicyBytes)
            {
                return null;
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            return text.Length > MaxPolicyBytes ? null : text;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }
    }
}
