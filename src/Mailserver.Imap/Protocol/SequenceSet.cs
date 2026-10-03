namespace Mailserver.Imap.Protocol;

/// <summary>
/// An IMAP sequence set such as "1:5,7,10:*". Resolved against the largest sequence number or UID, where "*" means that maximum.
/// </summary>
public sealed class SequenceSet
{
    private readonly List<(long From, long To)> _ranges;

    private SequenceSet(List<(long, long)> ranges) => _ranges = ranges;

    /// <summary>"*" is stored as <see cref="long.MaxValue"/> and resolved in <see cref="Contains"/>.</summary>
    public static SequenceSet Parse(string value)
    {
        var ranges = new List<(long, long)>();
        foreach (var part in value.Split(','))
        {
            var bounds = part.Split(':');
            if (bounds.Length is < 1 or > 2)
            {
                throw new ImapParseException($"Invalid sequence set: {value}");
            }

            var from = ParseNumber(bounds[0], value);
            var to = bounds.Length == 2 ? ParseNumber(bounds[1], value) : from;
            ranges.Add(from <= to ? (from, to) : (to, from));
        }

        return new SequenceSet(ranges);
    }

    public static bool TryParse(string value, out SequenceSet? set)
    {
        try
        {
            set = Parse(value);
            return true;
        }
        catch (ImapParseException)
        {
            set = null;
            return false;
        }
    }

    /// <param name="max">Largest existing number; "*" stands for it.</param>
    public bool Contains(long number, long max)
    {
        foreach (var (from, to) in _ranges)
        {
            var low = from == long.MaxValue ? max : from;
            var high = to == long.MaxValue ? max : to;
            if (low > high)
            {
                (low, high) = (high, low);
            }

            if (number >= low && number <= high)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Formats numbers compactly, e.g. 1,2,3,7 as "1:3,7" (for COPYUID/APPENDUID).</summary>
    public static string Format(IEnumerable<long> numbers)
    {
        var parts = new List<string>();
        long? start = null, previous = null;
        foreach (var n in numbers)
        {
            if (previous is not null && n == previous + 1)
            {
                previous = n;
                continue;
            }

            if (start is not null)
            {
                parts.Add(start == previous ? $"{start}" : $"{start}:{previous}");
            }

            start = previous = n;
        }

        if (start is not null)
        {
            parts.Add(start == previous ? $"{start}" : $"{start}:{previous}");
        }

        return string.Join(',', parts);
    }

    private static long ParseNumber(string text, string value) =>
        text == "*" ? long.MaxValue :
        long.TryParse(text, out var number) && number > 0 ? number :
        throw new ImapParseException($"Invalid sequence set: {value}");
}
