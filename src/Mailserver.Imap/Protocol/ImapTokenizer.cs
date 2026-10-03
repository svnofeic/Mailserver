using System.Text;

namespace Mailserver.Imap.Protocol;

/// <summary>
/// Splits a command line into tokens. Literals were already read by <see cref="ImapConnection"/>: the line keeps the
/// "{n}" markers and the literal contents are taken from <paramref name="literals"/> in order.
/// </summary>
public sealed class ImapTokenizer(string line, Queue<byte[]> literals)
{
    private int _position;

    public static List<ImapToken> Tokenize(string line, Queue<byte[]> literals)
    {
        var tokenizer = new ImapTokenizer(line, literals);
        var tokens = tokenizer.ReadList(topLevel: true);
        return tokens;
    }

    private List<ImapToken> ReadList(bool topLevel)
    {
        var items = new List<ImapToken>();
        while (true)
        {
            SkipSpaces();
            if (_position >= line.Length)
            {
                return topLevel ? items : throw new ImapParseException("Missing ')'");
            }

            var c = line[_position];
            switch (c)
            {
                case ')':
                    if (topLevel)
                    {
                        throw new ImapParseException("Unexpected ')'");
                    }

                    _position++;
                    return items;
                case '(':
                    _position++;
                    items.Add(new ListToken(ReadList(topLevel: false)));
                    break;
                case '"':
                    items.Add(ReadQuoted());
                    break;
                case '{':
                    items.Add(ReadLiteral());
                    break;
                default:
                    items.Add(ReadAtom());
                    break;
            }
        }
    }

    private void SkipSpaces()
    {
        while (_position < line.Length && line[_position] == ' ')
        {
            _position++;
        }
    }

    private StringToken ReadQuoted()
    {
        var builder = new StringBuilder();
        _position++;
        while (_position < line.Length)
        {
            var c = line[_position++];
            if (c == '"')
            {
                return new StringToken(Encoding.UTF8.GetBytes(builder.ToString()));
            }

            if (c == '\\' && _position < line.Length)
            {
                c = line[_position++];
            }

            builder.Append(c);
        }

        throw new ImapParseException("Unterminated quoted string");
    }

    private StringToken ReadLiteral()
    {
        var close = line.IndexOf('}', _position);
        if (close < 0)
        {
            throw new ImapParseException("Invalid literal");
        }

        _position = close + 1;
        return literals.TryDequeue(out var bytes) ? new StringToken(bytes) : throw new ImapParseException("Missing literal data");
    }

    private AtomToken ReadAtom()
    {
        var start = _position;
        var depth = 0;
        while (_position < line.Length)
        {
            var c = line[_position];
            if (c == '[')
            {
                depth++;
            }
            else if (c == ']')
            {
                depth--;
            }
            else if (depth == 0 && (c == ' ' || c == '(' || c == ')'))
            {
                // "BODY[...]<0.10>" ends at a space or paren once the brackets are balanced.
                break;
            }

            _position++;
        }

        if (depth != 0)
        {
            throw new ImapParseException("Unbalanced '['");
        }

        if (start == _position)
        {
            throw new ImapParseException("Unexpected character");
        }

        return new AtomToken(line[start.._position]);
    }
}
