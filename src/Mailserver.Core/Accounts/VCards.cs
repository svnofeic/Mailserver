using System.Text;

namespace Mailserver.Core.Accounts;

/// <summary>Just enough vCard (3.0/4.0) for the address book: names and e-mail addresses, for import from iPhone, Outlook or Google and export.</summary>
public static class VCards
{
    /// <summary>Every e-mail address of every card, with the card's name.</summary>
    public static IReadOnlyList<(string? Name, string Address)> Read(string text)
    {
        // Unfold continuation lines (RFC 6350 3.2).
        var lines = text.Replace("\r\n", "\n").Replace("\n ", "").Replace("\n\t", "").Split('\n');
        var result = new List<(string?, string)>();
        string? name = null;
        string? structured = null;
        var addresses = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            var property = line[..colon];
            var value = Unescape(line[(colon + 1)..]).Trim();
            var key = property.Split(';')[0];
            // Grouped properties ("item1.EMAIL") as written by Apple.
            key = key[(key.LastIndexOf('.') + 1)..].ToUpperInvariant();
            switch (key)
            {
                case "BEGIN":
                    name = structured = null;
                    addresses.Clear();
                    break;
                case "FN":
                    name = value;
                    break;
                case "N":
                    var parts = value.Split(';');
                    structured = string.Join(' ', new[] { parts.ElementAtOrDefault(1), parts.ElementAtOrDefault(0) }.Where(p => !string.IsNullOrWhiteSpace(p)));
                    break;
                case "EMAIL":
                    if (EmailAddress.TryParse(value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? value[7..] : value, out var address))
                    {
                        addresses.Add(address.ToString());
                    }

                    break;
                case "END":
                    var display = string.IsNullOrWhiteSpace(name) ? structured : name;
                    result.AddRange(addresses.Distinct(StringComparer.OrdinalIgnoreCase).Select(a => (string.IsNullOrWhiteSpace(display) ? null : display, a)));
                    addresses.Clear();
                    break;
            }
        }

        return result;
    }

    public static string Write(IEnumerable<Contact> contacts)
    {
        var builder = new StringBuilder();
        foreach (var contact in contacts)
        {
            var name = string.IsNullOrWhiteSpace(contact.Name) ? contact.Address : contact.Name;
            builder.Append("BEGIN:VCARD\r\nVERSION:3.0\r\n")
                .Append("FN:").Append(Escape(name)).Append("\r\n")
                .Append("N:").Append(Escape(name)).Append(";;;;\r\n")
                .Append("EMAIL;TYPE=INTERNET:").Append(contact.Address).Append("\r\n")
                .Append("END:VCARD\r\n");
        }

        return builder.ToString();
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace(",", "\\,").Replace(";", "\\;").Replace("\n", "\\n");

    private static string Unescape(string value) => value.Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");
}
