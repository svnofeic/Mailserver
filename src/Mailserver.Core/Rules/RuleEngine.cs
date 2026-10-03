using System.Globalization;
using System.Text.RegularExpressions;
using Mailserver.Core.Storage;
using MimeKit;

namespace Mailserver.Core.Rules;

/// <summary>What happens to a message for one mailbox after rules and spam verdict were applied.</summary>
/// <param name="NotSpam">A rule explicitly allowed the message ("inbox"), overriding the spam filter.</param>
public sealed record DeliveryDecision(string? Folder, bool Discard, bool NotSpam, IReadOnlyList<string> Flags, IReadOnlyList<string> MatchedRules);

/// <summary>The parts of a message that rules look at, extracted once per message.</summary>
public sealed class RuleSubject
{
    private const int MaxBodyChars = 256 * 1024;
    private readonly MimeMessage _message;
    private string? _body;

    public RuleSubject(MimeMessage message, double spamScore)
    {
        _message = message;
        SpamScore = spamScore;
    }

    public double SpamScore { get; }

    public string Subject => _message.Subject ?? "";

    public string From => _message.From.ToString();

    public string To => string.Join(", ", _message.To.Concat(_message.Cc).Select(a => a.ToString()));

    public string Body => _body ??= ExtractBody();

    public IEnumerable<string> Header(string name) =>
        _message.Headers.Where(h => h.Field.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value);

    private string ExtractBody()
    {
        var text = _message.TextBody ?? StripHtml(_message.HtmlBody ?? "");
        return text.Length > MaxBodyChars ? text[..MaxBodyChars] : text;
    }

    private static string StripHtml(string html) =>
        System.Net.WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", " ", RegexOptions.None, TimeSpan.FromSeconds(1)));
}

/// <summary>
/// Applies filter rules in order. Matching is case-insensitive; "contains" matches word groups anywhere in the text.
/// </summary>
public static class RuleEngine
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    public static DeliveryDecision Decide(IReadOnlyList<MailRule> rules, RuleSubject message, bool isSpam)
    {
        string? folder = null;
        var discard = false;
        var notSpam = false;
        var flags = new List<string>();
        var matched = new List<string>();

        foreach (var rule in rules.Where(r => r.Enabled))
        {
            if (!Matches(rule, message))
            {
                continue;
            }

            matched.Add(rule.Name);
            switch (rule.Action)
            {
                case RuleAction.Junk:
                    folder = "Junk";
                    break;
                case RuleAction.Delete:
                    discard = true;
                    break;
                case RuleAction.Move:
                    folder = rule.Argument;
                    break;
                case RuleAction.Inbox:
                    folder = MailboxStore.Inbox;
                    notSpam = true;
                    break;
                case RuleAction.MarkRead:
                    flags.Add(MessageFlags.Seen);
                    break;
                case RuleAction.Flag:
                    flags.Add(MessageFlags.Flagged);
                    break;
            }

            if (rule.Stop || discard)
            {
                break;
            }
        }

        if (discard)
        {
            return new DeliveryDecision(null, true, false, [], matched);
        }

        folder ??= isSpam && !notSpam ? "Junk" : MailboxStore.Inbox;
        return new DeliveryDecision(folder, false, notSpam, flags, matched);
    }

    public static bool Matches(MailRule rule, RuleSubject message)
    {
        var results = rule.Conditions.Select(c => Matches(c, message));
        return rule.MatchAll ? results.All(r => r) : results.Any(r => r);
    }

    private static bool Matches(RuleCondition condition, RuleSubject message)
    {
        if (condition.Field == RuleField.SpamScore)
        {
            var threshold = double.Parse(condition.Value, CultureInfo.InvariantCulture);
            return condition.Operator switch
            {
                RuleOperator.Greater => message.SpamScore > threshold,
                RuleOperator.Less => message.SpamScore < threshold,
                _ => Math.Abs(message.SpamScore - threshold) < 0.001,
            };
        }

        var values = condition.Field switch
        {
            RuleField.Subject => [message.Subject],
            RuleField.From => [message.From],
            RuleField.To => [message.To],
            RuleField.Body => [message.Body],
            RuleField.Header => message.Header(condition.HeaderName ?? "").ToList(),
            _ => new List<string>(),
        };

        // "does not contain" holds when no value contains the text, also when the header is missing.
        if (condition.Operator == RuleOperator.NotContains)
        {
            return !values.Any(v => Compare(v, RuleOperator.Contains, condition.Value));
        }

        return values.Any(v => Compare(v, condition.Operator, condition.Value));
    }

    private static bool Compare(string text, RuleOperator op, string value)
    {
        text = Normalize(text);
        return op switch
        {
            RuleOperator.Contains => text.Contains(Normalize(value), StringComparison.OrdinalIgnoreCase),
            RuleOperator.Equals => text.Trim().Equals(Normalize(value).Trim(), StringComparison.OrdinalIgnoreCase),
            RuleOperator.StartsWith => text.TrimStart().StartsWith(Normalize(value), StringComparison.OrdinalIgnoreCase),
            RuleOperator.EndsWith => text.TrimEnd().EndsWith(Normalize(value), StringComparison.OrdinalIgnoreCase),
            RuleOperator.Regex => SafeRegex(text, value),
            _ => false,
        };
    }

    private static bool SafeRegex(string text, string pattern)
    {
        try
        {
            return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>Collapses whitespace so a word group matches across line breaks and double spaces.</summary>
    private static string Normalize(string text) => Regex.Replace(text, @"\s+", " ", RegexOptions.None, RegexTimeout);
}
