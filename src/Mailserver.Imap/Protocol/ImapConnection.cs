using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

namespace Mailserver.Imap.Protocol;

public sealed record ImapCommand(string Tag, string Name, IReadOnlyList<ImapToken> Arguments);

public sealed class ImapProtocolException(string message) : Exception(message);

/// <summary>
/// Line and literal I/O on top of the (possibly TLS-wrapped) network stream.
/// </summary>
public sealed partial class ImapConnection(Stream stream) : IAsyncDisposable
{
    private const int MaxLineLength = 64 * 1024;

    private readonly byte[] _buffer = new byte[16 * 1024];
    private int _start;
    private int _end;

    public Stream Stream { get; private set; } = stream;

    /// <summary>Upper bound for a single literal; raised after login so APPEND can upload whole messages.</summary>
    public int MaxLiteralSize { get; set; } = 8 * 1024;

    /// <summary>Total literal bytes per command.</summary>
    public int MaxCommandSize { get; set; } = 64 * 1024;

    /// <summary>Replaces the stream after STARTTLS. Pending unread input would be a protocol violation (RFC 3501 6.2.1).</summary>
    public void UpgradeStream(Stream secured)
    {
        _start = _end = 0;
        Stream = secured;
    }

    public bool HasBufferedInput => _start < _end;

    /// <summary>Reads one command including its literals. Returns null when the client closed the connection.</summary>
    public async Task<ImapCommand?> ReadCommandAsync(CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var literals = new Queue<byte[]>();
        var total = 0;

        while (true)
        {
            var line = await ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return null;
            }

            text.Append(line);
            var match = LiteralMarker().Match(line);
            if (!match.Success)
            {
                break;
            }

            var size = long.Parse(match.Groups[1].Value);
            var synchronizing = match.Groups[2].Length == 0;
            if (size > MaxLiteralSize || total + size > Math.Max(MaxCommandSize, MaxLiteralSize))
            {
                if (synchronizing)
                {
                    // The client waits for "+" and has not sent the data; the line can be rejected cleanly.
                    var tag = text.ToString().Split(' ', 2)[0];
                    throw new TaggedParseException(tag, $"Literal too large (max {MaxLiteralSize} bytes)");
                }

                throw new ImapProtocolException("Literal too large");
            }

            if (synchronizing)
            {
                await WriteAsync("+ Ready for literal data\r\n", cancellationToken);
            }

            literals.Enqueue(await ReadExactAsync((int)size, cancellationToken));
            total += (int)size;
        }

        return Parse(text.ToString(), literals);
    }

    /// <summary>Reads a raw line without tokenizing (IDLE's "DONE", AUTHENTICATE responses).</summary>
    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = new ArrayBufferWriter<byte>();
        while (true)
        {
            if (_start == _end && !await FillAsync(cancellationToken))
            {
                return null;
            }

            var span = _buffer.AsSpan(_start, _end - _start);
            var newline = span.IndexOf((byte)'\n');
            if (newline >= 0)
            {
                line.Write(span[..newline]);
                _start += newline + 1;
                var bytes = line.WrittenSpan;
                if (bytes.Length > 0 && bytes[^1] == '\r')
                {
                    bytes = bytes[..^1];
                }

                return Encoding.UTF8.GetString(bytes);
            }

            line.Write(span);
            _start = _end;
            if (line.WrittenCount > MaxLineLength)
            {
                throw new ImapProtocolException("Line too long");
            }
        }
    }

    public async Task WriteAsync(string text, CancellationToken cancellationToken)
    {
        await Stream.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);
        await Stream.FlushAsync(cancellationToken);
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await Stream.WriteAsync(data, cancellationToken);
        await Stream.FlushAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() => Stream.DisposeAsync();

    private async Task<byte[]> ReadExactAsync(int size, CancellationToken cancellationToken)
    {
        var result = new byte[size];
        var copied = 0;
        while (copied < size)
        {
            if (_start == _end && !await FillAsync(cancellationToken))
            {
                throw new ImapProtocolException("Connection closed inside a literal");
            }

            var count = Math.Min(size - copied, _end - _start);
            Buffer.BlockCopy(_buffer, _start, result, copied, count);
            _start += count;
            copied += count;
        }

        return result;
    }

    private async Task<bool> FillAsync(CancellationToken cancellationToken)
    {
        _start = 0;
        _end = await Stream.ReadAsync(_buffer, cancellationToken);
        return _end > 0;
    }

    private static ImapCommand Parse(string line, Queue<byte[]> literals)
    {
        // The tag and command name never contain literals; parse them before tokenizing so a BAD response can carry the tag.
        var firstSpace = line.IndexOf(' ');
        if (firstSpace <= 0)
        {
            throw new ImapParseException(line.Length == 0 ? "Empty command" : "Missing command");
        }

        var tag = line[..firstSpace];
        if (tag.Any(c => c is '*' or '+' or '(' or ')' or '{' or '"' or '\\' or '%' || char.IsControl(c)))
        {
            throw new ImapParseException("Invalid tag");
        }

        var rest = line[(firstSpace + 1)..];
        var secondSpace = rest.IndexOf(' ');
        var name = secondSpace < 0 ? rest : rest[..secondSpace];
        var arguments = secondSpace < 0 ? "" : rest[(secondSpace + 1)..];
        try
        {
            return new ImapCommand(tag, name.ToUpperInvariant(), ImapTokenizer.Tokenize(arguments, literals));
        }
        catch (ImapParseException ex)
        {
            throw new TaggedParseException(tag, ex.Message);
        }
    }

    [GeneratedRegex(@"\{(\d{1,10})(\+?)\}$")]
    private static partial Regex LiteralMarker();
}

/// <summary>A syntax error in a command whose tag is known.</summary>
public sealed class TaggedParseException(string tag, string message) : Exception(message)
{
    public string Tag { get; } = tag;
}
