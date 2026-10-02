using Mailserver.Core.Data;
using Microsoft.Data.Sqlite;

namespace Mailserver.Core.Accounts;

/// <summary>
/// Domains, accounts and aliases.
/// </summary>
public sealed class AccountStore(Database database)
{
    private const int MaxAliasDepth = 10;

    private const string AccountColumns = "a.id, a.local_part, d.name, a.quota_bytes, a.enabled";

    // ---- Domains ----

    public Domain AddDomain(string name, string? dkimSelector = null)
    {
        name = EmailAddress.NormalizeDomain(name);
        using var connection = database.Open();
        connection.Execute(
            "INSERT INTO domains (name, dkim_selector, created_utc) VALUES ($name, $selector, $now)",
            ("$name", name), ("$selector", dkimSelector), ("$now", DateTimeOffset.UtcNow.ToDbTime()));
        return GetDomain(name) ?? throw new InvalidOperationException("Domain was not created.");
    }

    public void SetDkimSelector(string domain, string? selector)
    {
        using var connection = database.Open();
        connection.Execute("UPDATE domains SET dkim_selector = $selector WHERE name = $name",
            ("$selector", selector), ("$name", EmailAddress.NormalizeDomain(domain)));
    }

    public bool RemoveDomain(string name)
    {
        using var connection = database.Open();
        return connection.Execute("DELETE FROM domains WHERE name = $name", ("$name", EmailAddress.NormalizeDomain(name))) > 0;
    }

    public Domain? GetDomain(string name)
    {
        if (!EmailAddress.TryNormalizeDomain(name, out var normalized))
        {
            return null;
        }

        using var connection = database.Open();
        return connection.Query("SELECT id, name, dkim_selector FROM domains WHERE name = $name", ReadDomain, ("$name", normalized))
            .SingleOrDefault();
    }

    public IReadOnlyList<Domain> ListDomains()
    {
        using var connection = database.Open();
        return connection.Query("SELECT id, name, dkim_selector FROM domains ORDER BY name", ReadDomain);
    }

    public bool IsLocalDomain(string domain) => GetDomain(domain) is not null;

    // ---- Accounts ----

    public Account AddAccount(EmailAddress address, string password, long quotaBytes = 0)
    {
        if (FindAlias(address) is not null)
        {
            throw new InvalidOperationException($"{address} is already an alias.");
        }

        var domain = GetDomain(address.Domain) ?? throw new InvalidOperationException($"Domain {address.Domain} is not configured.");
        using var connection = database.Open();
        connection.Execute(
            """
            INSERT INTO accounts (domain_id, local_part, password_hash, quota_bytes, created_utc)
            VALUES ($domain, $local, $hash, $quota, $now)
            """,
            ("$domain", domain.Id), ("$local", address.LocalPart), ("$hash", PasswordHasher.Hash(password)),
            ("$quota", quotaBytes), ("$now", DateTimeOffset.UtcNow.ToDbTime()));
        return FindAccount(address) ?? throw new InvalidOperationException("Account was not created.");
    }

    public bool SetPassword(EmailAddress address, string password) =>
        UpdateAccount(address, "password_hash = $value", PasswordHasher.Hash(password));

    public bool SetQuota(EmailAddress address, long quotaBytes) => UpdateAccount(address, "quota_bytes = $value", quotaBytes);

    public bool SetEnabled(EmailAddress address, bool enabled) => UpdateAccount(address, "enabled = $value", enabled ? 1 : 0);

    public bool RemoveAccount(EmailAddress address)
    {
        using var connection = database.Open();
        return connection.Execute(
            "DELETE FROM accounts WHERE local_part = $local AND domain_id = (SELECT id FROM domains WHERE name = $domain)",
            ("$local", address.LocalPart), ("$domain", address.Domain)) > 0;
    }

    public Account? FindAccount(EmailAddress address)
    {
        using var connection = database.Open();
        return FindAccount(connection, address);
    }

    public Account? FindAccount(long id)
    {
        using var connection = database.Open();
        return connection.Query(
            $"SELECT {AccountColumns} FROM accounts a JOIN domains d ON d.id = a.domain_id WHERE a.id = $id",
            ReadAccount, ("$id", id)).SingleOrDefault();
    }

    public IReadOnlyList<Account> ListAccounts(string? domain = null)
    {
        using var connection = database.Open();
        return domain is null
            ? connection.Query($"SELECT {AccountColumns} FROM accounts a JOIN domains d ON d.id = a.domain_id ORDER BY d.name, a.local_part", ReadAccount)
            : connection.Query(
                $"SELECT {AccountColumns} FROM accounts a JOIN domains d ON d.id = a.domain_id WHERE d.name = $domain ORDER BY a.local_part",
                ReadAccount, ("$domain", EmailAddress.NormalizeDomain(domain)));
    }

    /// <summary>Returns the account when the address and password match and the account is enabled.</summary>
    public Account? Authenticate(string username, string password)
    {
        string? hash = null;
        Account? account = null;
        if (EmailAddress.TryParse(username, out var address))
        {
            using var connection = database.Open();
            var row = connection.Query(
                $"SELECT {AccountColumns}, a.password_hash FROM accounts a JOIN domains d ON d.id = a.domain_id WHERE a.local_part = $local AND d.name = $domain",
                r => (Account: ReadAccount(r), Hash: r.GetString(5)),
                ("$local", address.LocalPart), ("$domain", address.Domain)).SingleOrDefault();
            account = row.Account;
            hash = row.Hash;
        }

        // Always run the hash so response times do not reveal whether the user exists.
        var valid = PasswordHasher.Verify(password, hash);
        return valid && account is { Enabled: true } ? account : null;
    }

    // ---- Aliases ----

    public Alias AddAlias(EmailAddress address, IEnumerable<EmailAddress> targets)
    {
        var targetList = targets.Distinct().ToList();
        if (targetList.Count == 0)
        {
            throw new ArgumentException("An alias needs at least one target.", nameof(targets));
        }

        if (!IsLocalDomain(address.Domain))
        {
            throw new InvalidOperationException($"Domain {address.Domain} is not configured.");
        }

        if (FindAccount(address) is not null)
        {
            throw new InvalidOperationException($"{address} is already an account.");
        }

        using var connection = database.Open();
        connection.Execute(
            """
            INSERT INTO aliases (address, targets) VALUES ($address, $targets)
            ON CONFLICT (address) DO UPDATE SET targets = excluded.targets
            """,
            ("$address", address.ToString()), ("$targets", string.Join(",", targetList)));
        return new Alias(address, targetList);
    }

    public bool RemoveAlias(EmailAddress address)
    {
        using var connection = database.Open();
        return connection.Execute("DELETE FROM aliases WHERE address = $address", ("$address", address.ToString())) > 0;
    }

    public Alias? FindAlias(EmailAddress address)
    {
        using var connection = database.Open();
        return FindAlias(connection, address);
    }

    public IReadOnlyList<Alias> ListAliases()
    {
        using var connection = database.Open();
        return connection.Query("SELECT address, targets FROM aliases ORDER BY address", ReadAlias);
    }

    // ---- Routing helpers ----

    /// <summary>
    /// Expands aliases recursively. Returns an empty resolution if the address is not known locally.
    /// </summary>
    public RecipientResolution Resolve(EmailAddress address)
    {
        using var connection = database.Open();
        var accounts = new List<Account>();
        var external = new List<EmailAddress>();
        var visited = new HashSet<EmailAddress>();
        var localDomains = new Dictionary<string, bool>(StringComparer.Ordinal);

        void Visit(EmailAddress current, int depth)
        {
            if (depth > MaxAliasDepth || !visited.Add(current))
            {
                return;
            }

            if (!localDomains.TryGetValue(current.Domain, out var isLocal))
            {
                isLocal = connection.Scalar("SELECT 1 FROM domains WHERE name = $name", ("$name", current.Domain)) is not null;
                localDomains[current.Domain] = isLocal;
            }

            if (!isLocal)
            {
                // Only reachable through an alias: forwarding to an external mailbox.
                if (depth > 0)
                {
                    external.Add(current);
                }

                return;
            }

            var account = FindAccount(connection, current);
            if (account is not null)
            {
                if (account.Enabled && !accounts.Contains(account))
                {
                    accounts.Add(account);
                }

                return;
            }

            var alias = FindAlias(connection, current);
            if (alias is null)
            {
                return;
            }

            foreach (var target in alias.Targets)
            {
                Visit(target, depth + 1);
            }
        }

        Visit(address, 0);
        return new RecipientResolution(accounts, external);
    }

    /// <summary>
    /// True if <paramref name="account"/> may use <paramref name="address"/> as sender:
    /// its own address, or an alias that delivers to it.
    /// </summary>
    public bool MaySendAs(Account account, EmailAddress address)
    {
        if (address.Equals(account.Address))
        {
            return true;
        }

        return FindAlias(address) is not null && Resolve(address).LocalAccounts.Any(a => a.Id == account.Id);
    }

    private bool UpdateAccount(EmailAddress address, string assignment, object value)
    {
        using var connection = database.Open();
        return connection.Execute(
            $"UPDATE accounts SET {assignment} WHERE local_part = $local AND domain_id = (SELECT id FROM domains WHERE name = $domain)",
            ("$value", value), ("$local", address.LocalPart), ("$domain", address.Domain)) > 0;
    }

    private static Account? FindAccount(SqliteConnection connection, EmailAddress address) =>
        connection.Query(
            $"SELECT {AccountColumns} FROM accounts a JOIN domains d ON d.id = a.domain_id WHERE a.local_part = $local AND d.name = $domain",
            ReadAccount, ("$local", address.LocalPart), ("$domain", address.Domain)).SingleOrDefault();

    private static Alias? FindAlias(SqliteConnection connection, EmailAddress address) =>
        connection.Query("SELECT address, targets FROM aliases WHERE address = $address", ReadAlias, ("$address", address.ToString()))
            .SingleOrDefault();

    private static Domain ReadDomain(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));

    private static Account ReadAccount(SqliteDataReader r) =>
        new(r.GetInt64(0), EmailAddress.Create(r.GetString(1), r.GetString(2)), r.GetInt64(3), r.GetInt64(4) != 0);

    private static Alias ReadAlias(SqliteDataReader r) =>
        new(EmailAddress.Parse(r.GetString(0)), r.GetString(1).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(EmailAddress.Parse).ToList());
}
