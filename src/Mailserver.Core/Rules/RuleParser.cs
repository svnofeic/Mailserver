namespace Mailserver.Core.Rules;

/// <summary>Parsed form of "--if &lt;feld&gt; &lt;operator&gt; &lt;wert&gt; ... --then &lt;aktion&gt; [ordner]" from the command line.</summary>
public sealed record RuleDefinition(
    string Name,
    IReadOnlyList<RuleCondition> Conditions,
    bool MatchAll,
    RuleAction Action,
    string? Argument,
    bool Stop,
    int Priority);

/// <summary>Command-line syntax for rules. German and English keywords are accepted.</summary>
public static class RuleParser
{
    private static readonly Dictionary<string, RuleField> Fields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["subject"] = RuleField.Subject, ["betreff"] = RuleField.Subject,
        ["from"] = RuleField.From, ["von"] = RuleField.From, ["absender"] = RuleField.From,
        ["to"] = RuleField.To, ["an"] = RuleField.To, ["empfänger"] = RuleField.To, ["empfaenger"] = RuleField.To,
        ["body"] = RuleField.Body, ["text"] = RuleField.Body, ["inhalt"] = RuleField.Body,
        ["score"] = RuleField.SpamScore, ["spamscore"] = RuleField.SpamScore,
    };

    private static readonly Dictionary<string, RuleOperator> Operators = new(StringComparer.OrdinalIgnoreCase)
    {
        ["contains"] = RuleOperator.Contains, ["enthält"] = RuleOperator.Contains, ["enthaelt"] = RuleOperator.Contains,
        ["notcontains"] = RuleOperator.NotContains, ["enthält-nicht"] = RuleOperator.NotContains, ["enthaelt-nicht"] = RuleOperator.NotContains,
        ["is"] = RuleOperator.Equals, ["equals"] = RuleOperator.Equals, ["ist"] = RuleOperator.Equals,
        ["startswith"] = RuleOperator.StartsWith, ["beginnt"] = RuleOperator.StartsWith,
        ["endswith"] = RuleOperator.EndsWith, ["endet"] = RuleOperator.EndsWith,
        ["regex"] = RuleOperator.Regex,
        [">"] = RuleOperator.Greater, ["greater"] = RuleOperator.Greater, ["über"] = RuleOperator.Greater, ["ueber"] = RuleOperator.Greater,
        ["<"] = RuleOperator.Less, ["less"] = RuleOperator.Less, ["unter"] = RuleOperator.Less,
    };

    private static readonly Dictionary<string, RuleAction> Actions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["junk"] = RuleAction.Junk, ["spam"] = RuleAction.Junk,
        ["delete"] = RuleAction.Delete, ["löschen"] = RuleAction.Delete, ["loeschen"] = RuleAction.Delete,
        ["move"] = RuleAction.Move, ["verschieben"] = RuleAction.Move,
        ["inbox"] = RuleAction.Inbox, ["kein-spam"] = RuleAction.Inbox, ["posteingang"] = RuleAction.Inbox,
        ["read"] = RuleAction.MarkRead, ["gelesen"] = RuleAction.MarkRead,
        ["flag"] = RuleAction.Flag, ["markieren"] = RuleAction.Flag,
    };

    public const string Syntax =
        """
        --if <feld> <operator> <wert> [--if ...] [--any] --then <aktion> [ordner] [--name <name>] [--continue] [--priority <n>]
          feld:     betreff | von | an | text | header:<Name> | score
          operator: enthält | enthält-nicht | ist | beginnt | endet | regex | über | unter
          aktion:   spam | löschen | verschieben <Ordner> | kein-spam | gelesen | markieren
          --any       eine Bedingung reicht (Standard: alle müssen zutreffen)
          --continue  nach dieser Regel weitere Regeln prüfen (Standard: hier aufhören)
        """;

    /// <param name="args">The arguments after the scope, e.g. ["--if", "betreff", "enthält", "Gewinn", "--then", "spam"].</param>
    public static RuleDefinition Parse(IReadOnlyList<string> args)
    {
        var conditions = new List<RuleCondition>();
        RuleAction? action = null;
        string? argument = null;
        string? name = null;
        var matchAll = true;
        var stop = true;
        var priority = 100;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--if":
                {
                    if (i + 3 >= args.Count)
                    {
                        throw new ArgumentException("--if braucht Feld, Operator und Wert, z. B. --if betreff enthält \"Gewinnspiel\"");
                    }

                    var fieldText = args[i + 1];
                    string? header = null;
                    RuleField field;
                    if (fieldText.StartsWith("header:", StringComparison.OrdinalIgnoreCase) && fieldText.Length > 7)
                    {
                        field = RuleField.Header;
                        header = fieldText[7..];
                    }
                    else if (!Fields.TryGetValue(fieldText, out field))
                    {
                        throw new ArgumentException($"Unbekanntes Feld '{fieldText}'.");
                    }

                    if (!Operators.TryGetValue(args[i + 2], out var op))
                    {
                        throw new ArgumentException($"Unbekannter Operator '{args[i + 2]}'.");
                    }

                    var value = args[i + 3];
                    if (field == RuleField.SpamScore && !double.TryParse(value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out _))
                    {
                        throw new ArgumentException("Der Score muss eine Zahl sein, z. B. 8 oder 7.5.");
                    }

                    if (value.Length == 0)
                    {
                        throw new ArgumentException("Der Wert darf nicht leer sein.");
                    }

                    conditions.Add(new RuleCondition(field, op, value, header));
                    i += 3;
                    break;
                }
                case "--then":
                    if (i + 1 >= args.Count || !Actions.TryGetValue(args[i + 1], out var parsed))
                    {
                        throw new ArgumentException("--then braucht eine Aktion: spam, löschen, verschieben <Ordner>, kein-spam, gelesen, markieren.");
                    }

                    action = parsed;
                    i++;
                    if (parsed == RuleAction.Move)
                    {
                        if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        {
                            throw new ArgumentException("verschieben braucht einen Ordner, z. B. --then verschieben \"Rechnungen\"");
                        }

                        argument = args[++i];
                    }

                    break;
                case "--any":
                    matchAll = false;
                    break;
                case "--continue":
                    stop = false;
                    break;
                case "--name":
                    name = i + 1 < args.Count ? args[++i] : throw new ArgumentException("--name braucht einen Namen.");
                    break;
                case "--priority":
                    priority = i + 1 < args.Count && int.TryParse(args[++i], out var p) ? p : throw new ArgumentException("--priority braucht eine Zahl.");
                    break;
                default:
                    throw new ArgumentException($"Unerwartetes Argument '{args[i]}'.");
            }
        }

        if (conditions.Count == 0 || action is null)
        {
            throw new ArgumentException("Eine Regel braucht mindestens ein --if und genau ein --then.");
        }

        name ??= $"{string.Join(matchAll ? " und " : " oder ", conditions)} → {RuleActionText.Describe(action.Value, argument)}";
        return new RuleDefinition(name, conditions, matchAll, action.Value, argument, stop, priority);
    }
}
