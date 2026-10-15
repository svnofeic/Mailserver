using Mailserver.Core.Data;

namespace Mailserver.Core.Accounts;

/// <summary>Small webmail preferences of a mailbox (they apply on every device), e.g. whether the list shows conversations.</summary>
public sealed class AccountPrefs(Database database)
{
    public const string Conversations = "conversations";

    public string? Get(long accountId, string name)
    {
        using var connection = database.Open();
        return connection.Scalar("SELECT value FROM account_prefs WHERE account_id = $account AND name = $name",
            ("$account", accountId), ("$name", name)) as string;
    }

    public bool GetBool(long accountId, string name, bool fallback) => Get(accountId, name) switch
    {
        "1" => true,
        "0" => false,
        _ => fallback,
    };

    public void Set(long accountId, string name, string value)
    {
        using var connection = database.Open();
        connection.Execute("INSERT OR REPLACE INTO account_prefs (account_id, name, value) VALUES ($account, $name, $value)",
            ("$account", accountId), ("$name", name), ("$value", value));
    }

    public void SetBool(long accountId, string name, bool value) => Set(accountId, name, value ? "1" : "0");
}
