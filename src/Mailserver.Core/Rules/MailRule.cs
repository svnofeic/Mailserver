namespace Mailserver.Core.Rules;

/// <summary>Which part of a message a condition looks at.</summary>
public enum RuleField
{
    Subject,
    From,
    /// <summary>To and Cc.</summary>
    To,
    Body,
    /// <summary>Any header named in <see cref="RuleCondition.HeaderName"/>.</summary>
    Header,
    /// <summary>The spam score computed on receipt (compare with "greater" / "less").</summary>
    SpamScore,
}

public enum RuleOperator
{
    Contains,
    NotContains,
    Equals,
    StartsWith,
    EndsWith,
    Regex,
    Greater,
    Less,
}

public enum RuleAction
{
    /// <summary>Move to the Junk folder.</summary>
    Junk,
    /// <summary>Discard permanently; the message is never stored.</summary>
    Delete,
    /// <summary>Move to the folder in <see cref="MailRule.Argument"/> (created if missing).</summary>
    Move,
    /// <summary>Deliver to the inbox even if the spam filter flagged it (allow-listing).</summary>
    Inbox,
    MarkRead,
    Flag,
}

public sealed record RuleCondition(RuleField Field, RuleOperator Operator, string Value, string? HeaderName = null)
{
    public override string ToString() => $"{FieldName} {OperatorName} \"{Value}\"";

    private string FieldName => Field switch
    {
        RuleField.Subject => "Betreff",
        RuleField.From => "Absender",
        RuleField.To => "Empfänger",
        RuleField.Body => "Text",
        RuleField.Header => $"Header {HeaderName}",
        _ => "Spam-Score",
    };

    private string OperatorName => Operator switch
    {
        RuleOperator.Contains => "enthält",
        RuleOperator.NotContains => "enthält nicht",
        RuleOperator.Equals => "ist",
        RuleOperator.StartsWith => "beginnt mit",
        RuleOperator.EndsWith => "endet mit",
        RuleOperator.Regex => "passt auf",
        RuleOperator.Greater => "über",
        _ => "unter",
    };
}

public static class RuleActionText
{
    public static string Describe(RuleAction action, string? argument) => action switch
    {
        RuleAction.Junk => "in Spam verschieben",
        RuleAction.Delete => "endgültig löschen",
        RuleAction.Move => $"verschieben nach \"{argument}\"",
        RuleAction.Inbox => "nie als Spam behandeln",
        RuleAction.MarkRead => "als gelesen markieren",
        _ => "markieren",
    };
}

/// <param name="Scope">"*" for every mailbox, a domain, or a mailbox address.</param>
/// <param name="MatchAll">True: all conditions must match; false: any condition is enough.</param>
/// <param name="Stop">True: later rules are not evaluated after this one matched.</param>
public sealed record MailRule(
    long Id,
    string Scope,
    string Name,
    int Priority,
    bool Enabled,
    bool MatchAll,
    IReadOnlyList<RuleCondition> Conditions,
    RuleAction Action,
    string? Argument,
    bool Stop);
