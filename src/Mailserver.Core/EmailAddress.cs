using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Mailserver.Core;

/// <summary>
/// A normalized mailbox address. The domain is lower-cased and IDN-encoded; comparisons are case-insensitive.
/// </summary>
public readonly record struct EmailAddress
{
    private static readonly IdnMapping Idn = new();

    private EmailAddress(string localPart, string domain)
    {
        LocalPart = localPart;
        Domain = domain;
    }

    public string LocalPart { get; }

    public string Domain { get; }

    public static EmailAddress Parse(string value) =>
        TryParse(value, out var address) ? address : throw new FormatException($"Invalid e-mail address: '{value}'");

    public static bool TryParse(string? value, out EmailAddress address)
    {
        address = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim().Trim('<', '>');
        var at = value.LastIndexOf('@');
        if (at <= 0 || at == value.Length - 1)
        {
            return false;
        }

        var local = value[..at];
        if (local.Length > 64 || local.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            return false;
        }

        if (!TryNormalizeDomain(value[(at + 1)..], out var domain))
        {
            return false;
        }

        address = new EmailAddress(local, domain);
        return true;
    }

    public static EmailAddress Create(string localPart, string domain) => Parse($"{localPart}@{domain}");

    public static string NormalizeDomain(string domain) =>
        TryNormalizeDomain(domain, out var normalized) ? normalized : throw new FormatException($"Invalid domain: '{domain}'");

    public static bool TryNormalizeDomain(string? domain, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(domain))
        {
            return false;
        }

        try
        {
            normalized = Idn.GetAscii(domain.Trim().TrimEnd('.')).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return false;
        }

        return normalized.Length <= 253 && normalized.Contains('.');
    }

    public bool Equals(EmailAddress other) =>
        string.Equals(LocalPart, other.LocalPart, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Domain, other.Domain, StringComparison.Ordinal);

    public override int GetHashCode() =>
        HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(LocalPart ?? ""), Domain);

    public override string ToString() => $"{LocalPart}@{Domain}";
}
