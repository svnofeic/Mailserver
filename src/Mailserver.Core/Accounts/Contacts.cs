using Mailserver.Core.Data;
using Mailserver.Core.Storage;
using Microsoft.Data.Sqlite;
using MimeKit;

namespace Mailserver.Core.Accounts;

/// <param name="Saved">Added or edited by the user; the others were collected from sent mail.</param>
public sealed record Contact(long Id, string Address, string? Name, bool Saved, int TimesUsed, DateTimeOffset? LastUsed)
{
    /// <summary>"Name &lt;address&gt;" or just the address, as it goes into a recipient field.</summary>
    public string Display => string.IsNullOrWhiteSpace(Name) ? Address : new MailboxAddress(Name, Address).ToString();
}

/// <summary>
/// The address book of a mailbox: contacts the user saved, plus every address mail was sent to (from the webmail or a mail
/// program) – those feed the suggestions while typing a recipient.
/// </summary>
public sealed class Contacts(Database database, MailboxStore mailboxes, TimeProvider timeProvider)
{
    public const int MaxNameLength = 200;
    private const int SeedMessages = 3000;

    private const string Columns = "id, address, name, saved, times_used, last_used_utc";

    public IReadOnlyList<Contact> List(long accountId, string? filter = null)
    {
        using var connection = database.Open();
        var like = $"%{Escape(filter?.Trim() ?? "")}%";
        return connection.Query(
            $"""
            SELECT {Columns} FROM contacts WHERE account_id = $id AND (address LIKE $like ESCAPE '\' OR name LIKE $like ESCAPE '\')
            ORDER BY COALESCE(NULLIF(name, ''), address) COLLATE NOCASE
            """, Read, ("$id", accountId), ("$like", like));
    }

    public Contact? Get(long accountId, long id)
    {
        using var connection = database.Open();
        return connection.Query($"SELECT {Columns} FROM contacts WHERE account_id = $account AND id = $id", Read,
            ("$account", accountId), ("$id", id)).SingleOrDefault();
    }

    /// <summary>Suggestions for a recipient field: names and addresses containing <paramref name="term"/>, the most used first.</summary>
    public IReadOnlyList<Contact> Suggest(long accountId, string term, int max = 8)
    {
        term = term.Trim();
        if (term.Length == 0)
        {
            return [];
        }

        EnsureSeeded(accountId);
        using var connection = database.Open();
        return connection.Query(
            $"""
            SELECT {Columns} FROM contacts
            WHERE account_id = $id AND (address LIKE $like ESCAPE '\' OR name LIKE $like ESCAPE '\')
            ORDER BY (address LIKE $start ESCAPE '\' OR name LIKE $start ESCAPE '\') DESC, saved DESC, times_used DESC, last_used_utc DESC
            LIMIT $max
            """, Read, ("$id", accountId), ("$like", $"%{Escape(term)}%"), ("$start", $"{Escape(term)}%"), ("$max", max));
    }

    /// <summary>Adds or updates a saved contact. Throws <see cref="ArgumentException"/> with a German message.</summary>
    public Contact Save(long accountId, string address, string? name, long? id = null)
    {
        if (!EmailAddress.TryParse(address?.Trim() ?? "", out var parsed))
        {
            throw new ArgumentException("Das ist keine gültige E-Mail-Adresse.");
        }

        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (name?.Length > MaxNameLength)
        {
            throw new ArgumentException($"Der Name ist zu lang (höchstens {MaxNameLength} Zeichen).");
        }

        using var connection = database.Open();
        if (id is { } existing)
        {
            if (connection.Scalar("SELECT id FROM contacts WHERE account_id = $account AND address = $address AND id <> $id",
                    ("$account", accountId), ("$address", parsed.ToString()), ("$id", existing)) is not null)
            {
                throw new ArgumentException($"{parsed} steht schon im Adressbuch.");
            }

            connection.Execute("UPDATE contacts SET address = $address, name = $name, saved = 1 WHERE account_id = $account AND id = $id",
                ("$address", parsed.ToString()), ("$name", name), ("$account", accountId), ("$id", existing));
        }
        else
        {
            connection.Execute(
                """
                INSERT INTO contacts (account_id, address, name, saved) VALUES ($account, $address, $name, 1)
                ON CONFLICT (account_id, address) DO UPDATE SET name = COALESCE($name, name), saved = 1
                """,
                ("$account", accountId), ("$address", parsed.ToString()), ("$name", name));
        }

        return connection.Query($"SELECT {Columns} FROM contacts WHERE account_id = $account AND address = $address", Read,
            ("$account", accountId), ("$address", parsed.ToString())).Single();
    }

    public bool Delete(long accountId, long id)
    {
        using var connection = database.Open();
        return connection.Execute("DELETE FROM contacts WHERE account_id = $account AND id = $id", ("$account", accountId), ("$id", id)) > 0;
    }

    /// <summary>Remembers the recipients of a sent message (names from its To/Cc header, plus Bcc from the envelope).</summary>
    public void RecordSent(Account account, IEnumerable<EmailAddress> envelopeRecipients, HeaderList headers, IEnumerable<string>? ownAddresses = null)
    {
        var named = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in new[] { HeaderId.To, HeaderId.Cc })
        {
            if (InternetAddressList.TryParse(headers[field] ?? "", out var list))
            {
                foreach (var mailbox in list.Mailboxes)
                {
                    named.TryAdd(mailbox.Address, string.IsNullOrWhiteSpace(mailbox.Name) ? null : mailbox.Name);
                }
            }
        }

        var own = new HashSet<string>(ownAddresses ?? [], StringComparer.OrdinalIgnoreCase) { account.Address.ToString() };
        var recipients = envelopeRecipients.Select(r => r.ToString()).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(r => !own.Contains(r))
            .Select(r => (Address: r, Name: named.GetValueOrDefault(r)));
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var (address, name) in recipients)
        {
            Record(connection, transaction, account.Id, address, name, timeProvider.GetUtcNow());
        }

        transaction.Commit();
    }

    /// <summary>
    /// The first time suggestions are asked for, the recipients of the mail already in "Sent" are collected (newest first),
    /// so the address book is useful from day one.
    /// </summary>
    public void EnsureSeeded(long accountId)
    {
        using (var connection = database.Open())
        {
            if (connection.Execute("INSERT OR IGNORE INTO contacts_seeded (account_id) VALUES ($id)", ("$id", accountId)) == 0)
            {
                return;
            }
        }

        if (mailboxes.GetFolder(accountId, "Sent") is not { } sent)
        {
            return;
        }

        var (messages, _) = mailboxes.ListMessagesNewestFirst(sent.Id, 0, SeedMessages);
        using var db = database.Open();
        using var tx = db.BeginTransaction();
        foreach (var message in messages)
        {
            try
            {
                using var stream = mailboxes.OpenMessage(message);
                var headers = HeaderList.Load(stream);
                foreach (var field in new[] { HeaderId.To, HeaderId.Cc, HeaderId.Bcc })
                {
                    if (InternetAddressList.TryParse(headers[field] ?? "", out var list))
                    {
                        foreach (var mailbox in list.Mailboxes.Where(m => EmailAddress.TryParse(m.Address, out _)))
                        {
                            Record(db, tx, accountId, EmailAddress.Parse(mailbox.Address).ToString(),
                                string.IsNullOrWhiteSpace(mailbox.Name) ? null : mailbox.Name, message.InternalDate);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or FormatException)
            {
                // An unreadable message just does not contribute.
            }
        }

        tx.Commit();
    }

    private static void Record(SqliteConnection connection, SqliteTransaction transaction, long accountId, string address, string? name,
        DateTimeOffset used) =>
        // A name the user saved is kept; a collected one is filled in when it was missing.
        connection.Execute(
            """
            INSERT INTO contacts (account_id, address, name, saved, times_used, last_used_utc) VALUES ($account, $address, $name, 0, 1, $used)
            ON CONFLICT (account_id, address) DO UPDATE SET
                times_used = times_used + 1,
                last_used_utc = MAX(COALESCE(last_used_utc, ''), $used),
                name = CASE WHEN saved = 1 OR name IS NOT NULL THEN name ELSE $name END
            """,
            transaction, ("$account", accountId), ("$address", address), ("$name", name), ("$used", used.ToDbTime()));

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static Contact Read(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
        r.GetInt64(3) != 0, Convert.ToInt32(r.GetInt64(4)), r.IsDBNull(5) ? null : r.GetDbTime(5));
}
