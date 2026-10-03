using System.Text;
using System.Text.RegularExpressions;
using Mailserver.Core;

namespace Mailserver.Imap.Protocol;

/// <summary>
/// Protocol trace for diagnosing mail programs (Imap:Trace): every command and response line, one file per day in
/// data\logs. Passwords are masked, message contents are shortened. Meant to be switched on only while investigating.
/// </summary>
public sealed partial class ImapTrace(DataPaths paths)
{
    private const int MaxLineLength = 300;
    private const int MaxLinesPerWrite = 40;

    private readonly Lock _lock = new();
    private int _sessions;

    public string Directory => Path.Combine(paths.Root, "logs");

    public ImapSessionTrace Start(System.Net.IPAddress? remote)
    {
        var id = Interlocked.Increment(ref _sessions);
        var trace = new ImapSessionTrace(this, id);
        trace.Note($"Verbindung von {remote}");
        return trace;
    }

    internal void Write(int session, string direction, string line)
    {
        if (line.Length > MaxLineLength)
        {
            line = line[..MaxLineLength] + $" …({line.Length} Zeichen)";
        }

        var text = $"{DateTime.Now:HH:mm:ss.fff} [{session}] {direction} {line}{Environment.NewLine}";
        lock (_lock)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(Path.Combine(Directory, $"imap-trace-{DateTime.Now:yyyyMMdd}.log"), text, Encoding.UTF8);
            }
            catch (IOException)
            {
                // Tracing must never break a session.
            }
        }
    }

    /// <summary>Hides passwords in LOGIN and AUTHENTICATE lines.</summary>
    public static string Mask(string line)
    {
        var login = LoginCommand().Match(line);
        if (login.Success)
        {
            return $"{login.Groups[1].Value} {login.Groups[2].Value} ***";
        }

        var authenticate = AuthenticateCommand().Match(line);
        return authenticate.Success ? $"{authenticate.Groups[1].Value} ***" : line;
    }

    [GeneratedRegex(@"^(\S+ LOGIN) (""(?:[^""\\]|\\.)*""|\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex LoginCommand();

    [GeneratedRegex(@"^(\S+ AUTHENTICATE \S+) \S+", RegexOptions.IgnoreCase)]
    private static partial Regex AuthenticateCommand();

    internal static int MaxLines => MaxLinesPerWrite;
}

public sealed class ImapSessionTrace(ImapTrace trace, int id)
{
    /// <summary>Set after AUTHENTICATE without initial response: the next client line carries the credentials.</summary>
    public bool MaskNextClientLine { get; set; }

    public void Note(string text) => trace.Write(id, "--", text);

    public void Client(string line)
    {
        if (MaskNextClientLine)
        {
            MaskNextClientLine = false;
            trace.Write(id, "C:", "***");
            return;
        }

        trace.Write(id, "C:", ImapTrace.Mask(line));
        if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^\S+ AUTHENTICATE \S+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            MaskNextClientLine = true;
        }
    }

    public void ClientLiteral(long size) => trace.Write(id, "C:", $"<{size} Bytes Daten>");

    public void Server(ReadOnlySpan<byte> data)
    {
        var text = Encoding.UTF8.GetString(data);
        var lines = text.Split("\r\n");
        var count = lines.Length - (text.EndsWith("\r\n", StringComparison.Ordinal) ? 1 : 0);
        for (var i = 0; i < Math.Min(count, ImapTrace.MaxLines); i++)
        {
            trace.Write(id, "S:", lines[i]);
        }

        if (count > ImapTrace.MaxLines)
        {
            trace.Write(id, "S:", $"… {count - ImapTrace.MaxLines} weitere Zeilen ({data.Length} Bytes)");
        }
    }
}
