using System.Text;

namespace Mailserver.Imap.Protocol;

/// <summary>Folder name encoding of RFC 3501 section 5.1.3 ("Entwürfe" ⇄ "Entw&APw-rfe").</summary>
public static class ModifiedUtf7
{
    public static string Encode(string value)
    {
        var result = new StringBuilder();
        var pending = new StringBuilder();

        void Flush()
        {
            if (pending.Length == 0)
            {
                return;
            }

            var bytes = Encoding.BigEndianUnicode.GetBytes(pending.ToString());
            result.Append('&').Append(Convert.ToBase64String(bytes).TrimEnd('=').Replace('/', ',')).Append('-');
            pending.Clear();
        }

        foreach (var c in value)
        {
            if (c >= 0x20 && c <= 0x7e)
            {
                Flush();
                result.Append(c == '&' ? "&-" : c.ToString());
            }
            else
            {
                pending.Append(c);
            }
        }

        Flush();
        return result.ToString();
    }

    public static string Decode(string value)
    {
        var result = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '&')
            {
                result.Append(value[i]);
                continue;
            }

            var end = value.IndexOf('-', i + 1);
            if (end < 0)
            {
                throw new FormatException("Invalid modified UTF-7");
            }

            if (end == i + 1)
            {
                result.Append('&');
            }
            else
            {
                var base64 = value[(i + 1)..end].Replace(',', '/');
                base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
                result.Append(Encoding.BigEndianUnicode.GetString(Convert.FromBase64String(base64)));
            }

            i = end;
        }

        return result.ToString();
    }
}
