using System.Text;

namespace Mailserver.AntiSpam;

/// <summary>Removes header fields from a raw message without touching any other byte.</summary>
public static class HeaderEditor
{
    public static byte[] RemoveFields(byte[] message, Func<string, string, bool> shouldRemove)
    {
        var output = new MemoryStream(message.Length);
        var position = 0;
        while (position < message.Length)
        {
            var fieldStart = position;
            do
            {
                var newline = Array.IndexOf(message, (byte)'\n', position);
                position = newline < 0 ? message.Length : newline + 1;
            }
            while (position < message.Length && message[position] is (byte)' ' or (byte)'\t');

            var field = message.AsSpan(fieldStart, position - fieldStart);
            var colon = field.IndexOf((byte)':');
            var isEndOfHeader = field.Length <= 2 && field.Trim("\r\n"u8).Length == 0;
            if (isEndOfHeader || colon <= 0)
            {
                // Body (or malformed line): copy the rest unchanged.
                output.Write(message.AsSpan(fieldStart));
                return output.ToArray();
            }

            var name = Encoding.ASCII.GetString(field[..colon]).Trim();
            var value = Encoding.UTF8.GetString(field[(colon + 1)..]);
            if (!shouldRemove(name, value))
            {
                output.Write(field);
            }
        }

        return output.ToArray();
    }
}
