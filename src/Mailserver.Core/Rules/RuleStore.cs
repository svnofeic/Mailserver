using System.Text.Json;
using System.Text.Json.Serialization;
using Mailserver.Core.Data;
using Microsoft.Data.Sqlite;

namespace Mailserver.Core.Rules;

public sealed class RuleStore(Database database)
{
    public const string GlobalScope = "*";

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public MailRule Add(string scope, string name, IReadOnlyList<RuleCondition> conditions, RuleAction action, string? argument = null,
        bool matchAll = true, bool stop = true, int priority = 100)
    {
        scope = NormalizeScope(scope);
        Validate(conditions, action, argument);

        using var connection = database.Open();
        var id = (long)connection.Scalar(
            """
            INSERT INTO rules (scope, name, priority, enabled, match_all, conditions, action, argument, stop, created_utc)
            VALUES ($scope, $name, $priority, 1, $all, $conditions, $action, $argument, $stop, $now) RETURNING id
            """,
            ("$scope", scope), ("$name", name), ("$priority", priority), ("$all", matchAll ? 1 : 0),
            ("$conditions", JsonSerializer.Serialize(conditions, Json)), ("$action", action.ToString()), ("$argument", argument),
            ("$stop", stop ? 1 : 0), ("$now", DateTimeOffset.UtcNow.ToDbTime()))!;
        return Get(id)!;
    }

    /// <summary>Replaces a rule's definition; scope and id stay.</summary>
    public MailRule Update(long id, string name, IReadOnlyList<RuleCondition> conditions, RuleAction action, string? argument,
        bool matchAll, bool stop, int priority, bool enabled)
    {
        Validate(conditions, action, argument);
        using var connection = database.Open();
        connection.Execute(
            """
            UPDATE rules SET name = $name, priority = $priority, enabled = $enabled, match_all = $all, conditions = $conditions,
                             action = $action, argument = $argument, stop = $stop
            WHERE id = $id
            """,
            ("$id", id), ("$name", name), ("$priority", priority), ("$enabled", enabled ? 1 : 0), ("$all", matchAll ? 1 : 0),
            ("$conditions", JsonSerializer.Serialize(conditions, Json)), ("$action", action.ToString()), ("$argument", argument),
            ("$stop", stop ? 1 : 0));
        return Get(id) ?? throw new InvalidOperationException("Regel nicht gefunden.");
    }

    public MailRule? Get(long id)
    {
        using var connection = database.Open();
        return connection.Query($"{Select} WHERE id = $id", Read, ("$id", id)).SingleOrDefault();
    }

    public bool Remove(long id)
    {
        using var connection = database.Open();
        return connection.Execute("DELETE FROM rules WHERE id = $id", ("$id", id)) > 0;
    }

    public bool SetEnabled(long id, bool enabled)
    {
        using var connection = database.Open();
        return connection.Execute("UPDATE rules SET enabled = $enabled WHERE id = $id", ("$enabled", enabled ? 1 : 0), ("$id", id)) > 0;
    }

    /// <summary>All rules, or those of one scope.</summary>
    public IReadOnlyList<MailRule> List(string? scope = null)
    {
        using var connection = database.Open();
        return scope is null
            ? connection.Query($"{Select} ORDER BY scope, priority, id", Read)
            : connection.Query($"{Select} WHERE scope = $scope ORDER BY priority, id", Read, ("$scope", NormalizeScope(scope)));
    }

    /// <summary>Enabled rules that apply to a mailbox: global first, then its domain, then the mailbox itself.</summary>
    public IReadOnlyList<MailRule> GetRulesFor(EmailAddress address)
    {
        using var connection = database.Open();
        var rules = connection.Query(
            $"{Select} WHERE enabled = 1 AND scope IN ($global, $domain, $address)",
            Read, ("$global", GlobalScope), ("$domain", address.Domain), ("$address", address.ToString()));
        return rules
            .OrderBy(r => r.Scope == GlobalScope ? 0 : r.Scope.Contains('@') ? 2 : 1)
            .ThenBy(r => r.Priority)
            .ThenBy(r => r.Id)
            .ToList();
    }

    private static void Validate(IReadOnlyList<RuleCondition> conditions, RuleAction action, string? argument)
    {
        if (conditions.Count == 0)
        {
            throw new ArgumentException("Eine Regel braucht mindestens eine Bedingung.", nameof(conditions));
        }

        if (action == RuleAction.Move && string.IsNullOrWhiteSpace(argument))
        {
            throw new ArgumentException("Zum Verschieben muss ein Ordner angegeben werden.", nameof(argument));
        }

        foreach (var condition in conditions)
        {
            if (string.IsNullOrEmpty(condition.Value))
            {
                throw new ArgumentException("Jede Bedingung braucht einen Wert.", nameof(conditions));
            }

            if (condition.Field == RuleField.Header && string.IsNullOrWhiteSpace(condition.HeaderName))
            {
                throw new ArgumentException("Für eine Kopfzeilen-Bedingung muss der Name der Kopfzeile angegeben werden.", nameof(conditions));
            }

            if (condition.Field == RuleField.SpamScore && !double.TryParse(condition.Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                throw new ArgumentException("Der Spam-Score muss eine Zahl sein (z. B. 7.5).", nameof(conditions));
            }

            if (condition.Operator == RuleOperator.Regex)
            {
                // Fail early on invalid patterns instead of at delivery time.
                _ = new System.Text.RegularExpressions.Regex(condition.Value);
            }
        }
    }

    public static string NormalizeScope(string scope)
    {
        scope = scope.Trim();
        if (scope == GlobalScope)
        {
            return scope;
        }

        return scope.Contains('@') ? EmailAddress.Parse(scope).ToString() : EmailAddress.NormalizeDomain(scope);
    }

    private const string Select = "SELECT id, scope, name, priority, enabled, match_all, conditions, action, argument, stop FROM rules";

    private static MailRule Read(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt64(4) != 0, r.GetInt64(5) != 0,
        JsonSerializer.Deserialize<List<RuleCondition>>(r.GetString(6), Json) ?? [],
        Enum.Parse<RuleAction>(r.GetString(7)), r.IsDBNull(8) ? null : r.GetString(8), r.GetInt64(9) != 0);
}
