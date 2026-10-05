namespace Mailserver.Web;

/// <summary>Round initials badges for people (sender, account) with a colour that stays the same for a name.</summary>
public static class Avatars
{
    public static string Initials(string name)
    {
        var text = name.Trim().Trim('"');
        var at = text.IndexOf('@');
        if (at > 0 && !text.Contains(' '))
        {
            text = text[..at];
        }

        var parts = text.Split([' ', '.', '_', '-'], StringSplitOptions.RemoveEmptyEntries).Where(p => char.IsLetterOrDigit(p[0])).ToList();
        return parts.Count switch
        {
            0 => "?",
            1 => parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant(),
            _ => $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[^1][0])}",
        };
    }

    public static string Class(string name)
    {
        var hash = 0;
        foreach (var c in name.ToLowerInvariant())
        {
            hash = (hash * 31 + c) & 0x7fffffff;
        }

        return "av" + hash % 6;
    }
}
