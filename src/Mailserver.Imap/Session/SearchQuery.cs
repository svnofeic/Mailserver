using System.Globalization;
using Mailserver.Core.Storage;
using Mailserver.Imap.Protocol;
using MimeKit;

namespace Mailserver.Imap.Session;

internal sealed record SearchContext(int Sequence, StoredMessage Message, int Count, long MaxUid, MessageCache Cache, CancellationToken CancellationToken)
{
    private MessageContent? _content;

    public async Task<MimeMessage?> GetParsedAsync()
    {
        _content ??= await Cache.GetAsync(Message, CancellationToken);
        return _content?.Parsed;
    }
}

internal sealed class UnsupportedCharsetException : Exception;

/// <summary>SEARCH criteria of RFC 3501 section 6.4.4, compiled into a predicate tree.</summary>
internal sealed class SearchQuery
{
    private readonly Func<SearchContext, ValueTask<bool>> _predicate;

    private SearchQuery(Func<SearchContext, ValueTask<bool>> predicate) => _predicate = predicate;

    public ValueTask<bool> MatchesAsync(SearchContext context) => _predicate(context);

    public static SearchQuery Parse(IReadOnlyList<ImapToken> tokens)
    {
        var position = 0;
        if (tokens.Count >= 2 && tokens[0] is AtomToken { Value: var first } && first.Equals("CHARSET", StringComparison.OrdinalIgnoreCase))
        {
            var charset = tokens[1].AsString();
            if (!charset.Equals("UTF-8", StringComparison.OrdinalIgnoreCase) && !charset.Equals("US-ASCII", StringComparison.OrdinalIgnoreCase))
            {
                throw new UnsupportedCharsetException();
            }

            position = 2;
        }

        var criteria = new List<Func<SearchContext, ValueTask<bool>>>();
        while (position < tokens.Count)
        {
            criteria.Add(ParseKey(tokens, ref position));
        }

        if (criteria.Count == 0)
        {
            throw new ImapParseException("Missing search criteria");
        }

        return new SearchQuery(All(criteria));
    }

    private static Func<SearchContext, ValueTask<bool>> ParseKey(IReadOnlyList<ImapToken> tokens, ref int position)
    {
        var token = tokens[position++];
        if (token is ListToken list)
        {
            var inner = 0;
            var criteria = new List<Func<SearchContext, ValueTask<bool>>>();
            while (inner < list.Items.Count)
            {
                criteria.Add(ParseKey(list.Items, ref inner));
            }

            return All(criteria);
        }

        var key = token.AsAtom().ToUpperInvariant();
        string Next(ref int p) => p < tokens.Count ? tokens[p++].AsString() : throw new ImapParseException($"{key} needs an argument");

        switch (key)
        {
            case "ALL":
            case "OLD":
                return _ => ValueTask.FromResult(true);
            case "RECENT":
                return _ => ValueTask.FromResult(false);
            case "NEW":
            case "UNSEEN":
                return Flag(MessageFlags.Seen, expected: false);
            case "SEEN":
                return Flag(MessageFlags.Seen, expected: true);
            case "ANSWERED":
            case "UNANSWERED":
                return Flag(MessageFlags.Answered, key == "ANSWERED");
            case "DELETED":
            case "UNDELETED":
                return Flag(MessageFlags.Deleted, key == "DELETED");
            case "DRAFT":
            case "UNDRAFT":
                return Flag(MessageFlags.Draft, key == "DRAFT");
            case "FLAGGED":
            case "UNFLAGGED":
                return Flag(MessageFlags.Flagged, key == "FLAGGED");
            case "KEYWORD":
            case "UNKEYWORD":
                return Flag(Next(ref position), key == "KEYWORD");
            case "LARGER":
            {
                var size = ParseNumber(Next(ref position));
                return c => ValueTask.FromResult(c.Message.Size > size);
            }
            case "SMALLER":
            {
                var size = ParseNumber(Next(ref position));
                return c => ValueTask.FromResult(c.Message.Size < size);
            }
            case "BEFORE":
            case "ON":
            case "SINCE":
            {
                var date = ParseDate(Next(ref position));
                return c => ValueTask.FromResult(CompareDate(DateOnly.FromDateTime(c.Message.InternalDate.UtcDateTime), date, key));
            }
            case "SENTBEFORE":
            case "SENTON":
            case "SENTSINCE":
            {
                var date = ParseDate(Next(ref position));
                var mode = key[4..];
                return async c =>
                {
                    var message = await c.GetParsedAsync();
                    return message is not null && message.Headers.Contains(HeaderId.Date) &&
                           CompareDate(DateOnly.FromDateTime(message.Date.DateTime), date, mode);
                };
            }
            case "FROM":
            case "TO":
            case "CC":
            case "BCC":
            case "SUBJECT":
                return Header(key, Next(ref position));
            case "HEADER":
            {
                var field = Next(ref position);
                return Header(field, Next(ref position));
            }
            case "BODY":
            {
                var text = Next(ref position);
                return async c => await c.GetParsedAsync() is { } m && BodyContains(m, text);
            }
            case "TEXT":
            {
                var text = Next(ref position);
                return async c => await c.GetParsedAsync() is { } m &&
                                  (m.Headers.Any(h => Contains(h.Value, text)) || BodyContains(m, text));
            }
            case "UID":
            {
                var set = SequenceSet.Parse(Next(ref position));
                return c => ValueTask.FromResult(set.Contains(c.Message.Uid, c.MaxUid));
            }
            case "NOT":
            {
                var inner = ParseKey(tokens, ref position);
                return async c => !await inner(c);
            }
            case "OR":
            {
                var left = ParseKey(tokens, ref position);
                var right = ParseKey(tokens, ref position);
                return async c => await left(c) || await right(c);
            }
            default:
                if (SequenceSet.TryParse(key, out var sequences))
                {
                    return c => ValueTask.FromResult(sequences!.Contains(c.Sequence, c.Count));
                }

                throw new ImapParseException($"Unknown search key {key}");
        }
    }

    private static Func<SearchContext, ValueTask<bool>> All(List<Func<SearchContext, ValueTask<bool>>> criteria) => async c =>
    {
        foreach (var criterion in criteria)
        {
            if (!await criterion(c))
            {
                return false;
            }
        }

        return true;
    };

    private static Func<SearchContext, ValueTask<bool>> Flag(string flag, bool expected) =>
        c => ValueTask.FromResult(c.Message.HasFlag(flag) == expected);

    private static Func<SearchContext, ValueTask<bool>> Header(string field, string text) => async c =>
    {
        var message = await c.GetParsedAsync();
        if (message is null)
        {
            return false;
        }

        var values = message.Headers.Where(h => h.Field.Equals(field, StringComparison.OrdinalIgnoreCase)).ToList();
        // An empty search string matches every message that has the field (RFC 3501 6.4.4).
        return values.Count > 0 && (text.Length == 0 || values.Any(h => Contains(h.Value, text)));
    };

    private static bool BodyContains(MimeMessage message, string text) =>
        message.BodyParts.OfType<TextPart>().Any(part => Contains(part.Text, text));

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static bool CompareDate(DateOnly value, DateOnly reference, string mode) => mode switch
    {
        "BEFORE" => value < reference,
        "ON" => value == reference,
        _ => value >= reference,
    };

    private static DateOnly ParseDate(string value) =>
        DateOnly.TryParseExact(value.Trim(), "d-MMM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new ImapParseException($"Invalid date {value}");

    private static long ParseNumber(string value) =>
        long.TryParse(value, out var number) ? number : throw new ImapParseException($"Invalid number {value}");
}
