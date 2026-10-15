using System.Text.RegularExpressions;

namespace Mailserver.Core.Dav;

/// <summary>Reading a contact (vCard 3.0/4.0, RFC 6350) as stored by CardDAV clients.</summary>
public static partial class VCardData
{
    public const int MaxBytes = 1024 * 1024;

    /// <summary>The UID of a single vCard. Throws <see cref="FormatException"/> with a German message.</summary>
    public static DavObjectInfo Inspect(string text)
    {
        if (text.Length > MaxBytes)
        {
            throw new FormatException("Zu groß (Foto?).");
        }

        var unfolded = Unfold(text);
        var begins = BeginCard().Matches(unfolded).Count;
        if (begins != 1 || !EndCard().IsMatch(unfolded))
        {
            throw new FormatException("Genau eine vCard erwartet.");
        }

        if (Property(unfolded, "UID") is not { Length: > 0 } uid)
        {
            throw new FormatException("Die UID fehlt.");
        }

        return new DavObjectInfo(uid, "VCARD", null, null);
    }

    /// <summary>The value of the first property of that name (unfolded, parameters dropped), or null.</summary>
    public static string? Property(string text, string name)
    {
        foreach (var line in Unfold(text).Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            var colon = trimmed.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var head = trimmed[..colon];
            // "item1.EMAIL;TYPE=WORK" is EMAIL as well.
            var property = head.Split(';')[0];
            property = property.Contains('.') ? property[(property.LastIndexOf('.') + 1)..] : property;
            if (property.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[(colon + 1)..].Trim();
            }
        }

        return null;
    }

    public static string Unfold(string text) => Folding().Replace(text, "");

    [GeneratedRegex(@"\r?\n[ \t]")]
    private static partial Regex Folding();

    [GeneratedRegex(@"^BEGIN:VCARD\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex BeginCard();

    [GeneratedRegex(@"^END:VCARD\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex EndCard();
}
