using System.Security.Cryptography;
using System.Text;

namespace Mailserver.Core.Security;

/// <summary>A parsed MTA-STS policy (RFC 8461 section 3.2).</summary>
public sealed record MtaStsPolicy(string Mode, IReadOnlyList<string> Mx, TimeSpan MaxAge)
{
    public bool Enforce => Mode == "enforce";

    /// <summary>True when <paramref name="host"/> is allowed by the "mx" lines ("*.example.com" stands for exactly one label).</summary>
    public bool Allows(string host)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        foreach (var pattern in Mx.Select(m => m.TrimEnd('.').ToLowerInvariant()))
        {
            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = pattern[1..];
                if (host.EndsWith(suffix, StringComparison.Ordinal) && host.Length > suffix.Length && !host[..^suffix.Length].Contains('.'))
                {
                    return true;
                }
            }
            else if (host == pattern)
            {
                return true;
            }
        }

        return false;
    }

    public static MtaStsPolicy? Parse(string text)
    {
        string? version = null, mode = null;
        var mx = new List<string>();
        long maxAge = -1;
        foreach (var line in text.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "version": version = value; break;
                case "mode": mode = value.ToLowerInvariant(); break;
                case "mx": mx.Add(value); break;
                case "max_age" when long.TryParse(value, out var seconds) && seconds >= 0: maxAge = Math.Min(seconds, 31557600); break;
            }
        }

        return version == "STSv1" && mode is "enforce" or "testing" or "none" && maxAge >= 0 && (mx.Count > 0 || mode == "none")
            ? new MtaStsPolicy(mode, mx, TimeSpan.FromSeconds(maxAge))
            : null;
    }
}

/// <summary>The MTA-STS policy this server publishes for its domains (same for all of them: one MX host).</summary>
public static class MtaStsPublisher
{
    public static string PolicyText(MailserverOptions options)
    {
        var mode = options.MtaSts.Mode?.ToLowerInvariant() is "enforce" or "none" ? options.MtaSts.Mode.ToLowerInvariant() : "testing";
        var maxAge = (long)TimeSpan.FromDays(Math.Clamp(options.MtaSts.MaxAgeDays, 1, 365)).TotalSeconds;
        return $"version: STSv1\r\nmode: {mode}\r\nmx: {options.Hostname}\r\nmax_age: {maxAge}\r\n";
    }

    /// <summary>The id for the _mta-sts TXT record; it changes whenever the policy text changes, so others fetch it again.</summary>
    public static string PolicyId(MailserverOptions options) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PolicyText(options))))[..16].ToLowerInvariant();

    public static string TxtRecord(MailserverOptions options) => $"v=STSv1; id={PolicyId(options)}";

    public static string TlsRptRecord(string domain) => $"v=TLSRPTv1; rua=mailto:postmaster@{domain}";
}
