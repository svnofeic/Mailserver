using System.Text;
using System.Text.RegularExpressions;

namespace Mailserver.Imap.Mime;

/// <summary>A parsed "BODY[section]&lt;partial&gt;" fetch attribute.</summary>
public sealed partial record BodySection(
    bool Peek,
    int[] Path,
    string Specifier,
    IReadOnlyList<string> Fields,
    long? PartialStart,
    long? PartialLength)
{
    /// <summary>Parses e.g. "BODY.PEEK[1.2.HEADER.FIELDS (From To)]&lt;0.2048&gt;"; returns null if <paramref name="atom"/> is no section.</summary>
    public static BodySection? TryParse(string atom)
    {
        var match = Pattern().Match(atom);
        if (!match.Success)
        {
            return null;
        }

        var section = match.Groups["section"].Value.Trim();
        var path = new List<int>();
        var specifier = "";
        var fields = new List<string>();

        var fieldListStart = section.IndexOf('(');
        if (fieldListStart >= 0)
        {
            var close = section.LastIndexOf(')');
            if (close < fieldListStart)
            {
                return null;
            }

            fields.AddRange(section[(fieldListStart + 1)..close].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(f => f.Trim('"')));
            section = section[..fieldListStart].Trim();
        }

        foreach (var part in section.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (specifier.Length == 0 && int.TryParse(part, out var number) && number > 0)
            {
                path.Add(number);
            }
            else
            {
                specifier = specifier.Length == 0 ? part.ToUpperInvariant() : specifier + "." + part.ToUpperInvariant();
            }
        }

        if (specifier is not ("" or "HEADER" or "TEXT" or "MIME" or "HEADER.FIELDS" or "HEADER.FIELDS.NOT") ||
            (specifier == "MIME" && path.Count == 0) || (specifier.StartsWith("HEADER.FIELDS", StringComparison.Ordinal) && fields.Count == 0))
        {
            return null;
        }

        long? start = match.Groups["start"].Success ? long.Parse(match.Groups["start"].Value) : null;
        long? length = match.Groups["length"].Success ? long.Parse(match.Groups["length"].Value) : null;
        return new BodySection(match.Groups["peek"].Success, path.ToArray(), specifier, fields, start, length);
    }

    /// <summary>The section name as echoed in the response, e.g. "BODY[1.HEADER.FIELDS (FROM)]&lt;0&gt;".</summary>
    public string ResponseName
    {
        get
        {
            var builder = new StringBuilder("BODY[");
            builder.Append(string.Join('.', Path));
            if (Specifier.Length > 0)
            {
                builder.Append(Path.Length > 0 ? "." : "").Append(Specifier);
            }

            if (Fields.Count > 0)
            {
                builder.Append(" (").Append(string.Join(' ', Fields.Select(f => f.ToUpperInvariant()))).Append(')');
            }

            builder.Append(']');
            if (PartialStart is not null)
            {
                builder.Append('<').Append(PartialStart).Append('>');
            }

            return builder.ToString();
        }
    }

    /// <summary>The requested bytes; empty if the part does not exist (RFC 3501 allows an empty string then).</summary>
    public byte[] Extract(MimeNode root)
    {
        var bytes = ExtractFull(root);
        if (PartialStart is null)
        {
            return bytes;
        }

        var start = (int)Math.Min(PartialStart.Value, bytes.Length);
        var length = (int)Math.Min(PartialLength ?? long.MaxValue, bytes.Length - start);
        return bytes.AsSpan(start, length).ToArray();
    }

    private byte[] ExtractFull(MimeNode root)
    {
        var node = Resolve(root);
        if (node is null)
        {
            return [];
        }

        // HEADER/TEXT/HEADER.FIELDS on a part refer to the message encapsulated in that message/rfc822 part.
        var message = Path.Length == 0 ? node : node.Message;
        switch (Specifier)
        {
            case "":
                return Path.Length == 0 ? node.Data.AsSpan(node.HeaderStart, node.End - node.HeaderStart).ToArray() : node.BodyBytes.ToArray();
            case "MIME":
                return node.HeaderBytes.ToArray();
            case "HEADER":
                return message?.HeaderBytes.ToArray() ?? [];
            case "TEXT":
                return message?.BodyBytes.ToArray() ?? [];
            default:
                return message is null ? [] : FilterHeaders(message.HeaderBytes, Fields, exclude: Specifier == "HEADER.FIELDS.NOT");
        }
    }

    private MimeNode? Resolve(MimeNode root)
    {
        var node = root;
        for (var i = 0; i < Path.Length; i++)
        {
            var number = Path[i];
            if (node.IsMultipart)
            {
                if (number > node.Children.Count)
                {
                    return null;
                }

                node = node.Children[number - 1];
            }
            else if (number != 1)
            {
                return null;
            }

            // Descend into an encapsulated message when more path elements follow.
            if (i < Path.Length - 1 && node.IsMessage)
            {
                if (node.Message is null)
                {
                    return null;
                }

                node = node.Message;
            }
        }

        return node;
    }

    /// <summary>Selects header fields byte-for-byte (keeping folding), followed by the terminating empty line.</summary>
    public static byte[] FilterHeaders(ReadOnlySpan<byte> headerBlock, IReadOnlyList<string> fields, bool exclude)
    {
        var wanted = new HashSet<string>(fields, StringComparer.OrdinalIgnoreCase);
        var output = new MemoryStream();
        var text = headerBlock;
        var position = 0;
        while (position < text.Length)
        {
            // A field is its first line plus continuation lines starting with whitespace.
            var fieldStart = position;
            do
            {
                var newline = text[position..].IndexOf((byte)'\n');
                position = newline < 0 ? text.Length : position + newline + 1;
            }
            while (position < text.Length && text[position] is (byte)' ' or (byte)'\t');

            var field = text[fieldStart..position];
            var colon = field.IndexOf((byte)':');
            if (colon <= 0)
            {
                continue; // the empty line terminating the block, or garbage
            }

            var name = Encoding.ASCII.GetString(field[..colon]).Trim();
            if (wanted.Contains(name) != exclude)
            {
                output.Write(field);
            }
        }

        output.Write("\r\n"u8);
        return output.ToArray();
    }

    [GeneratedRegex(@"^BODY(?<peek>\.PEEK)?\[(?<section>[^\]]*)\](?:<(?<start>\d+)(?:\.(?<length>\d+))?>)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();
}
