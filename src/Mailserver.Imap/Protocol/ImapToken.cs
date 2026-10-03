using System.Text;

namespace Mailserver.Imap.Protocol;

/// <summary>A parsed element of a client command.</summary>
public abstract record ImapToken;

/// <summary>An atom such as a command name, flag, sequence set or "BODY[HEADER.FIELDS (FROM)]&lt;0.100&gt;".</summary>
public sealed record AtomToken(string Value) : ImapToken
{
    public bool Is(string value) => Value.Equals(value, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Value;
}

/// <summary>A quoted string or literal.</summary>
public sealed record StringToken(byte[] Bytes) : ImapToken
{
    public string Text => Encoding.UTF8.GetString(Bytes);

    public override string ToString() => Text;
}

/// <summary>A parenthesized list.</summary>
public sealed record ListToken(IReadOnlyList<ImapToken> Items) : ImapToken;

public sealed class ImapParseException(string message) : Exception(message);

public static class ImapTokenExtensions
{
    /// <summary>The textual value of an atom or string ("astring" in RFC 3501).</summary>
    public static string AsString(this ImapToken token) => token switch
    {
        AtomToken atom => atom.Value,
        StringToken str => str.Text,
        _ => throw new ImapParseException("Expected a string"),
    };

    public static string AsAtom(this ImapToken token) =>
        token is AtomToken atom ? atom.Value : throw new ImapParseException("Expected an atom");

    public static IReadOnlyList<ImapToken> AsList(this ImapToken token) => token switch
    {
        ListToken list => list.Items,
        _ => [token],
    };
}
