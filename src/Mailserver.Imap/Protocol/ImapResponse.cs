using System.Text;

namespace Mailserver.Imap.Protocol;

/// <summary>
/// Builds one response line (or several) with proper string quoting and literals.
/// </summary>
public sealed class ImapResponse
{
    private readonly MemoryStream _buffer = new();

    public ImapResponse Raw(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        _buffer.Write(bytes);
        return this;
    }

    /// <summary>A string as quoted string when safe, otherwise as literal.</summary>
    public ImapResponse String(string value)
    {
        if (value.Length < 1024 && value.All(c => c >= 0x20 && c < 0x7f))
        {
            return Raw("\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"");
        }

        return Literal(Encoding.UTF8.GetBytes(value));
    }

    public ImapResponse NString(string? value) => value is null ? Raw("NIL") : String(value);

    public ImapResponse Literal(ReadOnlySpan<byte> data)
    {
        Raw($"{{{data.Length}}}\r\n");
        _buffer.Write(data);
        return this;
    }

    public ImapResponse Line() => Raw("\r\n");

    public bool IsEmpty => _buffer.Length == 0;

    public ReadOnlyMemory<byte> ToMemory() => _buffer.GetBuffer().AsMemory(0, (int)_buffer.Length);
}
