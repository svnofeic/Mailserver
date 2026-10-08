using Mailserver.Core.Accounts;
using Mailserver.Core.Data;
using Mailserver.Core.Security;
using Mailserver.Core.Storage;

namespace Mailserver.Core.External;

/// <summary>How the connection to the provider is secured.</summary>
public enum MailSecurity
{
    /// <summary>TLS from the start (IMAP 993, SMTP 465).</summary>
    Ssl,
    /// <summary>Plain connection upgraded with STARTTLS (IMAP 143, SMTP 587).</summary>
    StartTls,
    /// <summary>Unencrypted; only for servers in the own network.</summary>
    None,
}

public sealed record MailServerAddress(string Host, int Port, MailSecurity Security)
{
    public override string ToString() => $"{Host}:{Port}";
}

/// <summary>What the user enters for an address at another provider.</summary>
/// <param name="Smtp">Null: the address is only fetched, not used as sender.</param>
public sealed record ExternalAccountSettings(string Address, string Folder, MailServerAddress Imap, MailServerAddress? Smtp, string UserName);

public sealed record ExternalAccount(
    long Id,
    long AccountId,
    ExternalAccountSettings Settings,
    bool Enabled,
    long? UidValidity,
    long? LastUid,
    long Fetched,
    DateTimeOffset? LastFetch,
    string? LastError,
    DateTimeOffset Created)
{
    public string Address => Settings.Address;
    public bool CanSend => Settings.Smtp is not null;
}

/// <summary>Talks to the provider (implemented with MailKit in Mailserver.Smtp).</summary>
public interface IExternalMail
{
    /// <summary>Logs in to IMAP (and SMTP, if given). Returns a German error text, or null if both work.</summary>
    Task<string?> TestAsync(ExternalAccountSettings settings, string password, CancellationToken cancellationToken);

    /// <summary>Sends a finished message through the provider's SMTP server. Throws <see cref="ExternalMailException"/>.</summary>
    Task SendAsync(ExternalAccount account, byte[] message, IReadOnlyList<EmailAddress> recipients, CancellationToken cancellationToken);

    /// <summary>Fetches new mail of one address now. Returns the number of new messages; errors are stored with the account.</summary>
    Task<FetchResult> FetchAsync(ExternalAccount account, CancellationToken cancellationToken);
}

/// <param name="Error">German error text, or null.</param>
public sealed record FetchResult(int Fetched, string? Error);

public sealed class ExternalMailException(string message) : Exception(message);

/// <summary>Addresses at other providers per mailbox.</summary>
public sealed class ExternalAccountStore(Database database, AccountStore accounts, SecretProtector secrets, TimeProvider timeProvider)
{
    public const int MaxPerMailbox = 10;

    private const string Columns = "id, account_id, address, folder, imap_host, imap_port, imap_security, smtp_host, smtp_port, smtp_security, " +
                                   "user_name, enabled, uid_validity, last_uid, fetched, last_fetch_utc, last_error, created_utc";

    public IReadOnlyList<ExternalAccount> List(long accountId)
    {
        using var connection = database.Open();
        return connection.Query($"SELECT {Columns} FROM external_accounts WHERE account_id = $account ORDER BY address", Read, ("$account", accountId));
    }

    public IReadOnlyList<ExternalAccount> ListEnabled()
    {
        using var connection = database.Open();
        return connection.Query($"SELECT {Columns} FROM external_accounts WHERE enabled = 1 ORDER BY id", Read);
    }

    public ExternalAccount? Find(long id)
    {
        using var connection = database.Open();
        return connection.Query($"SELECT {Columns} FROM external_accounts WHERE id = $id", Read, ("$id", id)).SingleOrDefault();
    }

    public ExternalAccount? Get(long accountId, long id) => Find(id) is { } found && found.AccountId == accountId ? found : null;

    /// <summary>The address the mailbox may send as through its provider, or null.</summary>
    public ExternalAccount? FindSender(long accountId, string address) =>
        List(accountId).FirstOrDefault(a => a.CanSend && a.Address.Equals(address, StringComparison.OrdinalIgnoreCase));

    /// <summary>The decrypted password, or null if it cannot be read any more (then the user enters it again).</summary>
    public string? Password(long id)
    {
        using var connection = database.Open();
        return connection.Scalar("SELECT password FROM external_accounts WHERE id = $id", ("$id", id)) is string stored
            ? secrets.Unprotect(stored)
            : null;
    }

    /// <param name="fetchExisting">Also fetch the mail already in the provider's inbox; otherwise only what arrives from now on.</param>
    public ExternalAccount Add(Account owner, ExternalAccountSettings settings, string password, bool fetchExisting)
    {
        settings = Check(owner, settings, id: null);
        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("Bitte das Passwort für das Konto beim Anbieter angeben.");
        }

        if (List(owner.Id).Count >= MaxPerMailbox)
        {
            throw new ArgumentException($"Höchstens {MaxPerMailbox} fremde Adressen pro Postfach.");
        }

        using var connection = database.Open();
        var id = (long)connection.Scalar(
            """
            INSERT INTO external_accounts (account_id, address, folder, imap_host, imap_port, imap_security, smtp_host, smtp_port, smtp_security,
                                           user_name, password, last_uid, created_utc)
            VALUES ($account, $address, $folder, $imapHost, $imapPort, $imapSecurity, $smtpHost, $smtpPort, $smtpSecurity, $user, $password, $lastUid, $now)
            RETURNING id
            """,
            [.. Parameters(settings), ("$account", owner.Id), ("$password", secrets.Protect(password)),
             ("$lastUid", fetchExisting ? 0L : null), ("$now", timeProvider.GetUtcNow().ToDbTime())])!;
        return Find(id)!;
    }

    /// <param name="password">Null or empty: keep the stored one.</param>
    public ExternalAccount Update(Account owner, long id, ExternalAccountSettings settings, string? password)
    {
        var existing = Get(owner.Id, id) ?? throw new ArgumentException("Dieses Konto gibt es nicht.");
        settings = Check(owner, settings, id);
        using var connection = database.Open();
        // Another server or mailbox at the provider: the fetch position there means nothing for the new one.
        var sameInbox = existing.Settings.Imap.Host.Equals(settings.Imap.Host, StringComparison.OrdinalIgnoreCase) &&
                        existing.Settings.UserName.Equals(settings.UserName, StringComparison.OrdinalIgnoreCase);
        connection.Execute(
            $"""
            UPDATE external_accounts SET address = $address, folder = $folder, imap_host = $imapHost, imap_port = $imapPort,
                imap_security = $imapSecurity, smtp_host = $smtpHost, smtp_port = $smtpPort, smtp_security = $smtpSecurity, user_name = $user,
                last_error = NULL{(string.IsNullOrEmpty(password) ? "" : ", password = $password")}
                {(sameInbox ? "" : ", uid_validity = NULL, last_uid = NULL")}
            WHERE id = $id
            """,
            [.. Parameters(settings), ("$id", id), ("$password", string.IsNullOrEmpty(password) ? null : secrets.Protect(password))]);
        return Find(id)!;
    }

    public bool SetEnabled(long accountId, long id, bool enabled)
    {
        using var connection = database.Open();
        return connection.Execute("UPDATE external_accounts SET enabled = $enabled, last_error = NULL WHERE id = $id AND account_id = $account",
            ("$enabled", enabled), ("$id", id), ("$account", accountId)) > 0;
    }

    public bool Remove(long accountId, long id)
    {
        using var connection = database.Open();
        return connection.Execute("DELETE FROM external_accounts WHERE id = $id AND account_id = $account", ("$id", id), ("$account", accountId)) > 0;
    }

    /// <summary>Remembers how far the provider's inbox has been fetched (after each message, so nothing is fetched twice).</summary>
    public void SavePosition(long id, long uidValidity, long lastUid, int newlyFetched)
    {
        using var connection = database.Open();
        connection.Execute("UPDATE external_accounts SET uid_validity = $validity, last_uid = $uid, fetched = fetched + $count WHERE id = $id",
            ("$validity", uidValidity), ("$uid", lastUid), ("$count", newlyFetched), ("$id", id));
    }

    public void RecordAttempt(long id, string? error)
    {
        using var connection = database.Open();
        connection.Execute("UPDATE external_accounts SET last_fetch_utc = $now, last_error = $error WHERE id = $id",
            ("$now", timeProvider.GetUtcNow().ToDbTime()), ("$error", error), ("$id", id));
    }

    /// <summary>Checks the input and fills in defaults (folder and user name = the address). Throws <see cref="ArgumentException"/>.</summary>
    /// <param name="id">The entry being edited, or null for a new one.</param>
    public ExternalAccountSettings Check(Account owner, ExternalAccountSettings settings, long? id)
    {
        if (!EmailAddress.TryParse(settings.Address.Trim(), out var address))
        {
            throw new ArgumentException("Bitte eine gültige E-Mail-Adresse angeben.");
        }

        if (accounts.IsLocalDomain(address.Domain))
        {
            throw new ArgumentException($"{address.Domain} wird von diesem Server verwaltet – dafür ist kein fremdes Konto nötig.");
        }

        if (List(owner.Id).Any(a => a.Id != id && a.Address.Equals(address.ToString(), StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"{address} ist bereits eingetragen.");
        }

        var folder = string.IsNullOrWhiteSpace(settings.Folder) ? address.ToString() : settings.Folder.Trim();
        if (MailboxStore.ValidateFolderName(folder) is { } folderError)
        {
            throw new ArgumentException(folderError);
        }

        if (MailboxStore.IsSystemFolder(folder) && !folder.Equals(MailboxStore.Inbox, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"In den Ordner „{folder}“ kann nicht abgerufen werden – bitte einen eigenen Ordner oder den Posteingang wählen.");
        }

        var userName = string.IsNullOrWhiteSpace(settings.UserName) ? address.ToString() : settings.UserName.Trim();
        return new ExternalAccountSettings(address.ToString(), folder, CheckServer(settings.Imap, "IMAP"),
            settings.Smtp is null ? null : CheckServer(settings.Smtp, "SMTP"), userName);
    }

    private static MailServerAddress CheckServer(MailServerAddress server, string kind)
    {
        var host = server.Host.Trim();
        if (host.Length is 0 or > 253 || Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            throw new ArgumentException($"Bitte einen gültigen {kind}-Server angeben (z. B. {kind.ToLowerInvariant()}.anbieter.de).");
        }

        if (server.Port is < 1 or > 65535)
        {
            throw new ArgumentException($"Der {kind}-Port muss zwischen 1 und 65535 liegen.");
        }

        return server with { Host = host };
    }

    private static (string, object?)[] Parameters(ExternalAccountSettings settings) =>
    [
        ("$address", settings.Address), ("$folder", settings.Folder), ("$user", settings.UserName),
        ("$imapHost", settings.Imap.Host), ("$imapPort", settings.Imap.Port), ("$imapSecurity", settings.Imap.Security.ToString()),
        ("$smtpHost", settings.Smtp?.Host), ("$smtpPort", settings.Smtp?.Port), ("$smtpSecurity", settings.Smtp?.Security.ToString()),
    ];

    private static ExternalAccount Read(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        r.GetInt64(0),
        r.GetInt64(1),
        new ExternalAccountSettings(r.GetString(2), r.GetString(3),
            new MailServerAddress(r.GetString(4), r.GetInt32(5), Enum.Parse<MailSecurity>(r.GetString(6))),
            r.IsDBNull(7) ? null : new MailServerAddress(r.GetString(7), r.GetInt32(8), Enum.Parse<MailSecurity>(r.GetString(9))),
            r.GetString(10)),
        r.GetInt64(11) != 0,
        r.IsDBNull(12) ? null : r.GetInt64(12),
        r.IsDBNull(13) ? null : r.GetInt64(13),
        r.GetInt64(14),
        r.IsDBNull(15) ? null : r.GetDbTime(15),
        r.IsDBNull(16) ? null : r.GetString(16),
        r.GetDbTime(17));
}
