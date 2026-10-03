using Mailserver.Core.Rules;

namespace Mailserver.Web;

/// <summary>Form model for creating and editing a rule (fixed number of condition rows, so no JavaScript is needed).</summary>
public sealed class RuleForm
{
    public const int ConditionRows = 5;

    public string Scope { get; set; } = "";
    public string? Name { get; set; }
    public int Priority { get; set; } = 100;
    public bool Enabled { get; set; } = true;
    public bool MatchAll { get; set; } = true;
    public bool Stop { get; set; } = true;
    public RuleAction Action { get; set; } = RuleAction.Junk;
    public string? Folder { get; set; }
    public List<ConditionRow> Conditions { get; set; } = [];

    public sealed class ConditionRow
    {
        public RuleField Field { get; set; } = RuleField.Subject;
        public RuleOperator Operator { get; set; } = RuleOperator.Contains;
        public string? Value { get; set; }
        public string? HeaderName { get; set; }
    }

    public static RuleForm FromRule(MailRule rule)
    {
        var form = new RuleForm
        {
            Scope = rule.Scope, Name = rule.Name, Priority = rule.Priority, Enabled = rule.Enabled, MatchAll = rule.MatchAll, Stop = rule.Stop,
            Action = rule.Action, Folder = rule.Argument,
            Conditions = rule.Conditions.Select(c => new ConditionRow { Field = c.Field, Operator = c.Operator, Value = c.Value, HeaderName = c.HeaderName }).ToList(),
        };
        return form.Padded();
    }

    public RuleForm Padded()
    {
        while (Conditions.Count < ConditionRows)
        {
            Conditions.Add(new ConditionRow());
        }

        return this;
    }

    public List<RuleCondition> ToConditions() => Conditions
        .Where(c => !string.IsNullOrWhiteSpace(c.Value))
        .Select(c => new RuleCondition(c.Field, c.Operator, c.Value!.Trim(), c.Field == RuleField.Header ? c.HeaderName?.Trim() : null))
        .ToList();

    public string ResolveName(IReadOnlyList<RuleCondition> conditions) =>
        string.IsNullOrWhiteSpace(Name) || Name.Contains('→')
            ? $"{string.Join(MatchAll ? " und " : " oder ", conditions)} → {RuleActionText.Describe(Action, Action == RuleAction.Move ? Folder : null)}"
            : Name.Trim();

    public static readonly (RuleField Value, string Label)[] FieldOptions =
    [
        (RuleField.Subject, "Betreff"), (RuleField.From, "Absender"), (RuleField.To, "Empfänger (An/Cc)"), (RuleField.Body, "Text"),
        (RuleField.Header, "Kopfzeile …"), (RuleField.SpamScore, "Spam-Score"),
    ];

    public static readonly (RuleOperator Value, string Label)[] OperatorOptions =
    [
        (RuleOperator.Contains, "enthält"), (RuleOperator.NotContains, "enthält nicht"), (RuleOperator.Equals, "ist genau"),
        (RuleOperator.StartsWith, "beginnt mit"), (RuleOperator.EndsWith, "endet mit"), (RuleOperator.Regex, "passt auf Regex"),
        (RuleOperator.Greater, "ist größer als"), (RuleOperator.Less, "ist kleiner als"),
    ];

    public static readonly (RuleAction Value, string Label)[] ActionOptions =
    [
        (RuleAction.Junk, "in den Spam-Ordner verschieben"), (RuleAction.Delete, "endgültig löschen"),
        (RuleAction.Move, "in Ordner verschieben …"), (RuleAction.Inbox, "nie als Spam behandeln (Posteingang)"),
        (RuleAction.MarkRead, "als gelesen markieren"), (RuleAction.Flag, "mit Fähnchen markieren"),
    ];
}
