using System.Text;
using MimeKit;

namespace Mailserver.Imap.Mime;

/// <summary>
/// MIME structure with exact byte offsets into the raw message. IMAP's BODY[section] must return the original bytes,
/// so the tree is built directly on the raw data; MimeKit is only used to parse individual header values.
/// </summary>
public sealed class MimeNode
{
    private const int MaxDepth = 32;

    private MimeNode(byte[] data, int headerStart, int bodyStart, int end, HeaderList headers, ContentType contentType)
    {
        Data = data;
        HeaderStart = headerStart;
        BodyStart = bodyStart;
        End = end;
        Headers = headers;
        ContentType = contentType;
    }

    public byte[] Data { get; }
    public int HeaderStart { get; }
    public int BodyStart { get; }
    public int End { get; }
    public HeaderList Headers { get; }
    public ContentType ContentType { get; }
    public List<MimeNode> Children { get; } = [];

    /// <summary>The encapsulated message of a message/rfc822 part.</summary>
    public MimeNode? Message { get; private set; }

    public bool IsMultipart => ContentType.MediaType.Equals("multipart", StringComparison.OrdinalIgnoreCase);

    public bool IsMessage =>
        ContentType.MediaType.Equals("message", StringComparison.OrdinalIgnoreCase) &&
        ContentType.MediaSubtype.Equals("rfc822", StringComparison.OrdinalIgnoreCase);

    public ReadOnlySpan<byte> HeaderBytes => Data.AsSpan(HeaderStart, BodyStart - HeaderStart);

    public ReadOnlySpan<byte> BodyBytes => Data.AsSpan(BodyStart, End - BodyStart);

    public int BodyLineCount => BodyBytes.Count((byte)'\n') + (End > BodyStart && Data[End - 1] != '\n' ? 1 : 0);

    public string ContentTransferEncoding => Headers[HeaderId.ContentTransferEncoding]?.Trim() is { Length: > 0 } value ? value.ToUpperInvariant() : "7BIT";

    public static MimeNode Parse(byte[] data) => Parse(data, 0, data.Length, defaultType: null, depth: 0);

    private static MimeNode Parse(byte[] data, int start, int end, ContentType? defaultType, int depth)
    {
        var bodyStart = FindBodyStart(data, start, end);
        var headers = LoadHeaders(data, start, bodyStart);
        var contentType = headers[HeaderId.ContentType] is { } value && ContentType.TryParse(value, out var parsed)
            ? parsed
            : defaultType ?? new ContentType("text", "plain") { Charset = "us-ascii" };

        var node = new MimeNode(data, start, bodyStart, end, headers, contentType);
        if (depth >= MaxDepth)
        {
            return node;
        }

        if (node.IsMultipart && contentType.Boundary is { Length: > 0 } boundary)
        {
            // Parts of multipart/digest default to message/rfc822 (RFC 2046 5.1.5).
            var childDefault = contentType.MediaSubtype.Equals("digest", StringComparison.OrdinalIgnoreCase)
                ? new ContentType("message", "rfc822")
                : null;
            foreach (var (partStart, partEnd) in SplitParts(data, bodyStart, end, boundary))
            {
                node.Children.Add(Parse(data, partStart, partEnd, childDefault, depth + 1));
            }
        }
        else if (node.IsMessage && node.ContentTransferEncoding is "7BIT" or "8BIT" or "BINARY")
        {
            node.Message = Parse(data, bodyStart, end, null, depth + 1);
        }

        return node;
    }

    /// <summary>Position after the empty line that ends the header block (or <paramref name="end"/> if there is none).</summary>
    private static int FindBodyStart(byte[] data, int start, int end)
    {
        var lineStart = start;
        for (var i = start; i < end; i++)
        {
            if (data[i] != '\n')
            {
                continue;
            }

            var length = i - lineStart;
            if (length == 0 || (length == 1 && data[lineStart] == '\r'))
            {
                return i + 1;
            }

            lineStart = i + 1;
        }

        // No empty line: the whole entity is header.
        return end;
    }

    private static HeaderList LoadHeaders(byte[] data, int start, int bodyStart)
    {
        if (bodyStart <= start)
        {
            return [];
        }

        try
        {
            using var stream = new MemoryStream(data, start, bodyStart - start, writable: false);
            return HeaderList.Load(stream);
        }
        catch (FormatException)
        {
            return [];
        }
    }

    /// <summary>
    /// Splits a multipart body at its boundary lines. The CRLF before a boundary line belongs to the boundary (RFC 2046 5.1.1).
    /// </summary>
    private static List<(int Start, int End)> SplitParts(byte[] data, int start, int end, string boundary)
    {
        var delimiter = Encoding.ASCII.GetBytes("--" + boundary);
        var parts = new List<(int, int)>();
        int? partStart = null;
        var lineStart = start;

        while (lineStart < end)
        {
            var newline = Array.IndexOf(data, (byte)'\n', lineStart, end - lineStart);
            var lineEnd = newline < 0 ? end : newline + 1;

            if (StartsWith(data, lineStart, lineEnd, delimiter))
            {
                var afterDelimiter = lineStart + delimiter.Length;
                var isClose = afterDelimiter + 1 < lineEnd && data[afterDelimiter] == '-' && data[afterDelimiter + 1] == '-';
                if (IsDelimiterLine(data, isClose ? afterDelimiter + 2 : afterDelimiter, lineEnd))
                {
                    if (partStart is not null)
                    {
                        parts.Add((partStart.Value, PrecedingLineBreak(data, partStart.Value, lineStart)));
                    }

                    if (isClose)
                    {
                        return parts;
                    }

                    partStart = lineEnd;
                }
            }

            lineStart = lineEnd;
        }

        // Missing close delimiter: the last part runs to the end.
        if (partStart is not null && partStart.Value <= end)
        {
            parts.Add((partStart.Value, end));
        }

        return parts;
    }

    private static bool StartsWith(byte[] data, int start, int end, byte[] prefix) =>
        end - start >= prefix.Length && data.AsSpan(start, prefix.Length).SequenceEqual(prefix);

    /// <summary>Only optional whitespace may follow the boundary on its line.</summary>
    private static bool IsDelimiterLine(byte[] data, int position, int lineEnd)
    {
        for (var i = position; i < lineEnd; i++)
        {
            if (data[i] is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                return false;
            }
        }

        return true;
    }

    private static int PrecedingLineBreak(byte[] data, int partStart, int delimiterLineStart)
    {
        var end = delimiterLineStart;
        if (end > partStart && data[end - 1] == '\n')
        {
            end--;
            if (end > partStart && data[end - 1] == '\r')
            {
                end--;
            }
        }

        return end;
    }
}
