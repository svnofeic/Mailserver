namespace Mailserver.AntiSpam.Checks;

public static class DomainHelper
{
    // Public suffixes with two labels that are common in Europe and elsewhere. A full Public Suffix List is not bundled; for
    // domains below other multi-label suffixes the organizational domain is approximated by the last two labels.
    private static readonly HashSet<string> TwoLabelSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "co.uk", "org.uk", "ac.uk", "gov.uk", "me.uk", "ltd.uk", "plc.uk", "co.at", "or.at", "gv.at", "ac.at",
        "com.au", "net.au", "org.au", "co.nz", "co.jp", "ne.jp", "or.jp", "co.za", "com.br", "com.tr", "com.cn", "com.pl",
        "co.in", "co.il", "com.mx", "com.ar", "com.sg", "com.hk", "com.tw", "co.kr",
    };

    /// <summary>The registrable ("organizational") domain used for DMARC relaxed alignment, e.g. mail.example.co.uk → example.co.uk.</summary>
    public static string OrganizationalDomain(string domain)
    {
        var labels = domain.TrimEnd('.').ToLowerInvariant().Split('.');
        if (labels.Length <= 2)
        {
            return string.Join('.', labels);
        }

        var lastTwo = $"{labels[^2]}.{labels[^1]}";
        var take = TwoLabelSuffixes.Contains(lastTwo) ? 3 : 2;
        return string.Join('.', labels[^take..]);
    }

    public static bool Aligned(string a, string b, bool strict) =>
        strict
            ? a.Equals(b, StringComparison.OrdinalIgnoreCase)
            : OrganizationalDomain(a).Equals(OrganizationalDomain(b), StringComparison.OrdinalIgnoreCase);
}
