using System.Security.Cryptography;
using System.Text;
using Mailserver.Core.Data;

namespace Mailserver.Core.Accounts;

/// <summary>The external address a mailbox's reset links go to ("Passwort vergessen").</summary>
public sealed record RecoveryAddress(string Address, bool Verified);

/// <summary>
/// "Passwort vergessen": a second, external address per mailbox, confirmed with a link sent to it, and one-time links that
/// let the owner set a new password. Links are random 256-bit values; only their SHA-256 hash is stored, so a copy of the
/// database does not contain usable links.
/// </summary>
public sealed class PasswordRecovery(Database database, AccountStore accounts, TimeProvider timeProvider)
{
    public static readonly TimeSpan ResetLifetime = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan ConfirmLifetime = TimeSpan.FromDays(2);

    public RecoveryAddress? Get(long accountId)
    {
        using var connection = database.Open();
        return connection.Query("SELECT address, verified_utc FROM recovery_addresses WHERE account_id = $id",
            r => new RecoveryAddress(r.GetString(0), !r.IsDBNull(1)), ("$id", accountId)).SingleOrDefault();
    }

    /// <summary>Stores an address that still has to be confirmed; returns the confirmation link token.</summary>
    public string SetAddress(Account account, string address)
    {
        var normalized = Check(account, address);
        var token = NewToken();
        using var connection = database.Open();
        connection.Execute(
            """
            INSERT INTO recovery_addresses (account_id, address, verified_utc, token_hash, token_expires_utc)
            VALUES ($id, $address, NULL, $hash, $expires)
            ON CONFLICT (account_id) DO UPDATE SET address = $address, verified_utc = NULL, token_hash = $hash, token_expires_utc = $expires
            """,
            ("$id", account.Id), ("$address", normalized), ("$hash", Hash(token)),
            ("$expires", timeProvider.GetUtcNow().Add(ConfirmLifetime).ToDbTime()));
        return token;
    }

    /// <summary>Set by an administrator: counts as confirmed right away.</summary>
    public void SetConfirmedAddress(Account account, string address)
    {
        var normalized = Check(account, address);
        using var connection = database.Open();
        connection.Execute(
            """
            INSERT INTO recovery_addresses (account_id, address, verified_utc) VALUES ($id, $address, $now)
            ON CONFLICT (account_id) DO UPDATE SET address = $address, verified_utc = $now, token_hash = NULL, token_expires_utc = NULL
            """,
            ("$id", account.Id), ("$address", normalized), ("$now", timeProvider.GetUtcNow().ToDbTime()));
    }

    public void Remove(long accountId)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM recovery_addresses WHERE account_id = $id", ("$id", accountId));
        connection.Execute("DELETE FROM password_resets WHERE account_id = $id", ("$id", accountId));
    }

    /// <summary>Confirms the address the link was sent to; returns the mailbox or null if the link is unknown or expired.</summary>
    public Account? Confirm(string token)
    {
        using var connection = database.Open();
        var accountId = connection.Query(
            "SELECT account_id FROM recovery_addresses WHERE token_hash = $hash AND token_expires_utc > $now",
            r => r.GetInt64(0), ("$hash", Hash(token)), ("$now", timeProvider.GetUtcNow().ToDbTime())).SingleOrDefault();
        if (accountId == 0)
        {
            return null;
        }

        connection.Execute("UPDATE recovery_addresses SET verified_utc = $now, token_hash = NULL, token_expires_utc = NULL WHERE account_id = $id",
            ("$now", timeProvider.GetUtcNow().ToDbTime()), ("$id", accountId));
        return accounts.FindAccount(accountId);
    }

    /// <summary>
    /// A reset link for the mailbox and the address to send it to – or null if there is none: no confirmed address, a disabled
    /// mailbox, or an administrator (they reset their password on the server with mailadmin, so a lost external mailbox cannot
    /// cost the whole server). Earlier links of the mailbox stop working.
    /// </summary>
    public (string Token, string Address)? CreateReset(Account account)
    {
        if (account.IsAdmin || !account.Enabled || Get(account.Id) is not { Verified: true } recovery)
        {
            return null;
        }

        var token = NewToken();
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        connection.Execute("DELETE FROM password_resets WHERE account_id = $id OR expires_utc <= $now", transaction,
            ("$id", account.Id), ("$now", timeProvider.GetUtcNow().ToDbTime()));
        connection.Execute("INSERT INTO password_resets (token_hash, account_id, expires_utc) VALUES ($hash, $id, $expires)", transaction,
            ("$hash", Hash(token)), ("$id", account.Id), ("$expires", timeProvider.GetUtcNow().Add(ResetLifetime).ToDbTime()));
        transaction.Commit();
        return (token, recovery.Address);
    }

    /// <summary>The mailbox a reset link belongs to, while it is valid.</summary>
    public Account? FindReset(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        using var connection = database.Open();
        var accountId = connection.Query("SELECT account_id FROM password_resets WHERE token_hash = $hash AND expires_utc > $now",
            r => r.GetInt64(0), ("$hash", Hash(token)), ("$now", timeProvider.GetUtcNow().ToDbTime())).SingleOrDefault();
        return accountId == 0 ? null : accounts.FindAccount(accountId) is { Enabled: true, IsAdmin: false } account ? account : null;
    }

    /// <summary>Sets the new password with a valid link and invalidates it (and every other link of the mailbox).</summary>
    public Account? Reset(string? token, string password)
    {
        if (FindReset(token) is not { } account)
        {
            return null;
        }

        accounts.SetPassword(account.Address, password);
        using var connection = database.Open();
        connection.Execute("DELETE FROM password_resets WHERE account_id = $id", ("$id", account.Id));
        return account;
    }

    /// <summary>"s***@live.com" – enough to recognise the address without revealing it.</summary>
    public static string Mask(string address)
    {
        var at = address.IndexOf('@');
        return at <= 0 ? "***" : $"{address[0]}***{address[at..]}";
    }

    private string Check(Account account, string address)
    {
        if (!EmailAddress.TryParse(address.Trim(), out var parsed))
        {
            throw new ArgumentException("Bitte eine gültige E-Mail-Adresse eingeben.");
        }

        if (parsed == account.Address || accounts.FindAlias(parsed)?.Targets.Contains(account.Address) == true)
        {
            throw new ArgumentException("Die Ersatz-Adresse muss eine andere Adresse sein – an dieses Postfach käme man ohne Passwort ja nicht heran.");
        }

        return parsed.ToString();
    }

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
