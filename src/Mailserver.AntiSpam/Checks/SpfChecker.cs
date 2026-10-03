using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Mailserver.AntiSpam.Dns;

namespace Mailserver.AntiSpam.Checks;

public enum SpfResult
{
    None,
    Neutral,
    Pass,
    Fail,
    SoftFail,
    TempError,
    PermError,
}

public sealed record SpfOutcome(SpfResult Result, string Domain, string? Mechanism = null);

/// <summary>
/// Sender Policy Framework evaluation according to RFC 7208, including include/redirect, a/mx with CIDR, exists and macros.
/// The deprecated "ptr" mechanism never matches (allowed by RFC 7208 5.5).
/// </summary>
public sealed partial class SpfChecker(IDnsResolver dns)
{
    private const int MaxDnsLookups = 10;
    private const int MaxVoidLookups = 2;
    private const int MaxMxHosts = 10;

    private sealed class Evaluation(IPAddress ip, string sender, string helo)
    {
        public IPAddress Ip { get; } = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
        public string Sender { get; } = sender;
        public string Helo { get; } = helo;
        public int Lookups;
        public int VoidLookups;
    }

    private sealed class SpfException(SpfResult result, string message) : Exception(message)
    {
        public SpfResult Result { get; } = result;
    }

    /// <param name="sender">MAIL FROM address; for the null sender use "postmaster@" + HELO name (RFC 7208 2.4).</param>
    public async Task<SpfOutcome> CheckAsync(IPAddress ip, string sender, string helo, CancellationToken cancellationToken)
    {
        var at = sender.LastIndexOf('@');
        var domain = (at < 0 ? sender : sender[(at + 1)..]).TrimEnd('.').ToLowerInvariant();
        if (at == 0 || at < 0)
        {
            sender = "postmaster@" + domain;
        }

        if (!IsValidDomain(domain))
        {
            return new SpfOutcome(SpfResult.None, domain);
        }

        var evaluation = new Evaluation(ip, sender, helo);
        try
        {
            return await CheckHostAsync(evaluation, domain, cancellationToken);
        }
        catch (SpfException ex)
        {
            return new SpfOutcome(ex.Result, domain, ex.Message);
        }
    }

    private async Task<SpfOutcome> CheckHostAsync(Evaluation evaluation, string domain, CancellationToken cancellationToken)
    {
        if (!IsValidDomain(domain))
        {
            return new SpfOutcome(SpfResult.None, domain);
        }

        var txt = await dns.GetTxtAsync(domain, cancellationToken);
        if (txt.Status == DnsStatus.Error)
        {
            return new SpfOutcome(SpfResult.TempError, domain, "DNS error");
        }

        var records = txt.Records
            .Where(r => r.Equals("v=spf1", StringComparison.OrdinalIgnoreCase) || r.StartsWith("v=spf1 ", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (records.Count == 0)
        {
            return new SpfOutcome(SpfResult.None, domain);
        }

        if (records.Count > 1)
        {
            return new SpfOutcome(SpfResult.PermError, domain, "multiple SPF records");
        }

        string? redirect = null;
        foreach (var term in records[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var modifier = ModifierPattern().Match(term);
            if (modifier.Success)
            {
                if (modifier.Groups[1].Value.Equals("redirect", StringComparison.OrdinalIgnoreCase))
                {
                    redirect ??= term[(modifier.Length)..];
                }

                continue; // exp= and unknown modifiers are ignored
            }

            var qualifier = term[0] is '+' or '-' or '~' or '?' ? term[0] : '+';
            var mechanism = qualifier == term[0] ? term[1..] : term;
            if (await MatchesAsync(evaluation, domain, mechanism, cancellationToken))
            {
                var result = qualifier switch
                {
                    '-' => SpfResult.Fail,
                    '~' => SpfResult.SoftFail,
                    '?' => SpfResult.Neutral,
                    _ => SpfResult.Pass,
                };
                return new SpfOutcome(result, domain, term);
            }
        }

        if (redirect is not null)
        {
            CountLookup(evaluation);
            var target = Expand(redirect, evaluation, domain);
            var redirected = await CheckHostAsync(evaluation, target, cancellationToken);
            return redirected.Result == SpfResult.None ? new SpfOutcome(SpfResult.PermError, domain, "redirect without SPF record") : redirected;
        }

        return new SpfOutcome(SpfResult.Neutral, domain, "default");
    }

    private async Task<bool> MatchesAsync(Evaluation evaluation, string domain, string mechanism, CancellationToken cancellationToken)
    {
        var separator = mechanism.IndexOfAny([':', '/']);
        var name = (separator < 0 ? mechanism : mechanism[..separator]).ToLowerInvariant();
        var argument = separator < 0 ? "" : mechanism[separator..];

        switch (name)
        {
            case "all":
                return true;

            case "ip4":
            case "ip6":
            {
                NetworkRange range;
                try
                {
                    range = NetworkRange.Parse(argument.TrimStart(':'));
                }
                catch (FormatException)
                {
                    throw new SpfException(SpfResult.PermError, $"invalid address in {mechanism}");
                }

                var family = name == "ip4" ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
                return range.Network.AddressFamily == family && range.Contains(evaluation.Ip);
            }

            case "include":
            {
                CountLookup(evaluation);
                var target = Expand(RequireDomain(argument), evaluation, domain);
                var included = await CheckHostAsync(evaluation, target, cancellationToken);
                return included.Result switch
                {
                    SpfResult.Pass => true,
                    SpfResult.Fail or SpfResult.SoftFail or SpfResult.Neutral => false,
                    SpfResult.TempError => throw new SpfException(SpfResult.TempError, $"include:{target}"),
                    _ => throw new SpfException(SpfResult.PermError, $"include:{target} has no valid SPF record"),
                };
            }

            case "a":
            {
                CountLookup(evaluation);
                var (target, cidr4, cidr6) = ParseDomainAndCidr(argument, evaluation, domain);
                var addresses = await LookupAddressesAsync(evaluation, target, cancellationToken);
                return addresses.Any(a => MatchesCidr(a, evaluation.Ip, cidr4, cidr6));
            }

            case "mx":
            {
                CountLookup(evaluation);
                var (target, cidr4, cidr6) = ParseDomainAndCidr(argument, evaluation, domain);
                var mx = await dns.GetMxAsync(target, cancellationToken);
                if (mx.Status == DnsStatus.Error)
                {
                    throw new SpfException(SpfResult.TempError, $"mx:{target}");
                }

                if (mx.Records.Count == 0)
                {
                    CountVoid(evaluation);
                }

                if (mx.Records.Count > MaxMxHosts)
                {
                    throw new SpfException(SpfResult.PermError, $"mx:{target} has too many hosts");
                }

                foreach (var host in mx.Records.OrderBy(r => r.Preference).Select(r => r.Host))
                {
                    var addresses = await dns.GetAddressesAsync(host, cancellationToken);
                    if (addresses.Records.Any(a => MatchesCidr(a, evaluation.Ip, cidr4, cidr6)))
                    {
                        return true;
                    }
                }

                return false;
            }

            case "exists":
            {
                CountLookup(evaluation);
                var target = Expand(RequireDomain(argument), evaluation, domain);
                var addresses = await LookupAddressesAsync(evaluation, target, cancellationToken);
                return addresses.Any(a => a.AddressFamily == AddressFamily.InterNetwork);
            }

            case "ptr":
                CountLookup(evaluation);
                return false;

            default:
                throw new SpfException(SpfResult.PermError, $"unknown mechanism {name}");
        }
    }

    private async Task<IReadOnlyList<IPAddress>> LookupAddressesAsync(Evaluation evaluation, string name, CancellationToken cancellationToken)
    {
        var result = await dns.GetAddressesAsync(name, cancellationToken);
        if (result.Status == DnsStatus.Error)
        {
            throw new SpfException(SpfResult.TempError, $"lookup of {name} failed");
        }

        if (result.Records.Count == 0)
        {
            CountVoid(evaluation);
        }

        return result.Records;
    }

    private static void CountLookup(Evaluation evaluation)
    {
        if (++evaluation.Lookups > MaxDnsLookups)
        {
            throw new SpfException(SpfResult.PermError, "too many DNS lookups");
        }
    }

    private static void CountVoid(Evaluation evaluation)
    {
        if (++evaluation.VoidLookups > MaxVoidLookups)
        {
            throw new SpfException(SpfResult.PermError, "too many void DNS lookups");
        }
    }

    private static string RequireDomain(string argument) =>
        argument.StartsWith(':') && argument.Length > 1 ? argument[1..] : throw new SpfException(SpfResult.PermError, "missing domain");

    /// <summary>Parses ":domain/24//64", "/24", "//64" or "".</summary>
    private static (string Domain, int Cidr4, int Cidr6) ParseDomainAndCidr(string argument, Evaluation evaluation, string currentDomain)
    {
        var cidr4 = 32;
        var cidr6 = 128;
        var doubleSlash = argument.IndexOf("//", StringComparison.Ordinal);
        if (doubleSlash >= 0)
        {
            cidr6 = ParseCidr(argument[(doubleSlash + 2)..], 128);
            argument = argument[..doubleSlash];
        }

        var slash = argument.IndexOf('/');
        if (slash >= 0)
        {
            cidr4 = ParseCidr(argument[(slash + 1)..], 32);
            argument = argument[..slash];
        }

        var domain = argument.StartsWith(':') ? Expand(argument[1..], evaluation, currentDomain) : currentDomain;
        return (domain, cidr4, cidr6);
    }

    private static int ParseCidr(string value, int max) =>
        int.TryParse(value, out var cidr) && cidr >= 0 && cidr <= max ? cidr : throw new SpfException(SpfResult.PermError, "invalid CIDR length");

    private static bool MatchesCidr(IPAddress candidate, IPAddress ip, int cidr4, int cidr6) =>
        NetworkRange.Matches(candidate, ip, candidate.AddressFamily == AddressFamily.InterNetwork ? cidr4 : cidr6);

    /// <summary>Macro expansion (RFC 7208 section 7).</summary>
    private static string Expand(string spec, Evaluation evaluation, string domain)
    {
        var result = new StringBuilder();
        for (var i = 0; i < spec.Length; i++)
        {
            if (spec[i] != '%')
            {
                result.Append(spec[i]);
                continue;
            }

            if (i + 1 >= spec.Length)
            {
                throw new SpfException(SpfResult.PermError, "invalid macro");
            }

            var next = spec[++i];
            switch (next)
            {
                case '%':
                    result.Append('%');
                    continue;
                case '_':
                    result.Append(' ');
                    continue;
                case '-':
                    result.Append("%20");
                    continue;
                case '{':
                    var close = spec.IndexOf('}', i);
                    if (close < 0)
                    {
                        throw new SpfException(SpfResult.PermError, "invalid macro");
                    }

                    result.Append(ExpandMacro(spec[(i + 1)..close], evaluation, domain));
                    i = close;
                    continue;
                default:
                    throw new SpfException(SpfResult.PermError, "invalid macro");
            }
        }

        return result.ToString().TrimEnd('.');
    }

    private static string ExpandMacro(string macro, Evaluation evaluation, string domain)
    {
        var match = MacroPattern().Match(macro);
        if (!match.Success)
        {
            throw new SpfException(SpfResult.PermError, "invalid macro");
        }

        var letter = char.ToLowerInvariant(match.Groups["letter"].Value[0]);
        var at = evaluation.Sender.LastIndexOf('@');
        var value = letter switch
        {
            's' => evaluation.Sender,
            'l' => at > 0 ? evaluation.Sender[..at] : "postmaster",
            'o' => evaluation.Sender[(at + 1)..],
            'd' => domain,
            'i' => FormatIp(evaluation.Ip),
            'p' => "unknown",
            'v' => evaluation.Ip.AddressFamily == AddressFamily.InterNetwork ? "in-addr" : "ip6",
            'h' => evaluation.Helo,
            _ => throw new SpfException(SpfResult.PermError, $"macro letter {letter} not allowed here"),
        };

        var delimiters = match.Groups["delims"].Success && match.Groups["delims"].Length > 0 ? match.Groups["delims"].Value.ToCharArray() : ['.'];
        var parts = value.Split(delimiters).ToList();
        if (match.Groups["reverse"].Success && match.Groups["reverse"].Length > 0)
        {
            parts.Reverse();
        }

        if (match.Groups["digits"].Success && match.Groups["digits"].Length > 0)
        {
            var keep = int.Parse(match.Groups["digits"].Value);
            if (keep == 0)
            {
                throw new SpfException(SpfResult.PermError, "invalid macro");
            }

            parts = parts.Skip(Math.Max(0, parts.Count - keep)).ToList();
        }

        var expanded = string.Join('.', parts);
        return char.IsUpper(match.Groups["letter"].Value[0]) ? Uri.EscapeDataString(expanded) : expanded;
    }

    /// <summary>IPv4 dotted quad; IPv6 as dot-separated nibbles.</summary>
    private static string FormatIp(IPAddress ip) =>
        ip.AddressFamily == AddressFamily.InterNetwork
            ? ip.ToString()
            : string.Join('.', ip.GetAddressBytes().SelectMany(b => new[] { b >> 4, b & 0xF }).Select(n => n.ToString("x")));

    private static bool IsValidDomain(string domain) =>
        domain.Length is > 0 and <= 253 && domain.Contains('.') && domain.Split('.').All(l => l.Length is > 0 and <= 63);

    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9\-_\.]*)=")]
    private static partial Regex ModifierPattern();

    [GeneratedRegex(@"^(?<letter>[slodiphcrtvSLODIPHCRTV])(?<digits>\d*)(?<reverse>[rR]?)(?<delims>[\.\-\+,/_=]*)$")]
    private static partial Regex MacroPattern();
}
