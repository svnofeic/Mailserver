namespace Mailserver.Core.Data;

/// <summary>Small values the server remembers between starts.</summary>
public sealed class AppState(Database database)
{
    public string? Get(string key)
    {
        using var connection = database.Open();
        return connection.Scalar("SELECT value FROM app_state WHERE key = $key", ("$key", key)) as string;
    }

    public void Set(string key, string value)
    {
        using var connection = database.Open();
        connection.Execute("INSERT INTO app_state (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = $value",
            ("$key", key), ("$value", value));
    }
}
