using System.Text;
using Mailserver.Core.Accounts;
using Mailserver.Core.Data;

namespace Mailserver.Core.Dav;

/// <summary>
/// Keeps the address book of the web interface (saved contacts, recipient suggestions) and the vCards of CardDAV the same:
/// a contact saved on the iPhone appears in the webmail suggestions, one saved in the webmail appears on the iPhone. The
/// vCard keeps everything else (phone numbers, addresses, photo); the web interface only touches name and e-mail.
/// </summary>
public sealed class ContactCards(Database database, DavStore store, Contacts contacts)
{
    /// <summary>A vCard of an address book was added, changed (<paramref name="text"/>) or deleted (null).</summary>
    public void Changed(long accountId, DavCollection book, string href, string? text)
    {
        var cards = text is null ? [] : VCards.Read(text);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var linked = connection.Query("SELECT address FROM contacts WHERE account_id = $account AND card_collection = $book AND card_href = $href",
            transaction, r => r.GetString(0), ("$account", accountId), ("$book", book.Id), ("$href", href));
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, address) in cards)
        {
            if (!addresses.Add(address))
            {
                continue;
            }

            connection.Execute(
                """
                INSERT INTO contacts (account_id, address, name, saved, card_collection, card_href) VALUES ($account, $address, $name, 1, $book, $href)
                ON CONFLICT (account_id, address) DO UPDATE SET name = COALESCE($name, name), saved = 1, card_collection = $book, card_href = $href
                """,
                transaction, ("$account", accountId), ("$address", address), ("$name", name), ("$book", book.Id), ("$href", href));
        }

        foreach (var gone in linked.Where(a => !addresses.Contains(a)))
        {
            Unlink(connection, transaction, accountId, gone);
        }

        transaction.Commit();
    }

    /// <summary>An address book was deleted: its contacts are no longer saved ones (collected addresses stay suggestions).</summary>
    public void BookDeleted(long accountId, long bookId)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var address in connection.Query("SELECT address FROM contacts WHERE account_id = $account AND card_collection = $book", transaction,
                     r => r.GetString(0), ("$account", accountId), ("$book", bookId)))
        {
            Unlink(connection, transaction, accountId, address);
        }

        transaction.Commit();
    }

    /// <summary>
    /// Saved contacts from before CardDAV (or saved while no address book existed) get a vCard in the default address book.
    /// </summary>
    public void EnsureCards(long accountId)
    {
        var books = store.Collections(accountId, DavStore.AddressBook);
        if (books.Count == 0)
        {
            return;
        }

        List<(long Id, string Address, string? Name)> missing;
        using (var connection = database.Open())
        {
            missing = connection.Query(
                "SELECT id, address, name FROM contacts WHERE account_id = $account AND saved = 1 AND card_href IS NULL",
                r => (r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)), ("$account", accountId));
        }

        foreach (var (_, address, name) in missing)
        {
            var uid = Guid.NewGuid().ToString("D").ToUpperInvariant();
            var text = Build(uid, name, address);
            store.Put(books[0].Id, uid + ".vcf", text, VCardData.Inspect(text));
            Changed(accountId, books[0], uid + ".vcf", text);
        }
    }

    /// <summary>Saves a contact from the web interface and its vCard. Throws <see cref="ArgumentException"/> with a German message.</summary>
    public Contact Save(long accountId, string address, string? name, long? id = null)
    {
        var before = id is { } existing ? contacts.Get(accountId, existing) : null;
        var link = before is null ? null : Link(accountId, before.Id);
        var saved = contacts.Save(accountId, address, name, id);
        var books = store.Collections(accountId, DavStore.AddressBook);
        if (link is { } l && store.CollectionById(accountId, l.Book) is { } book && store.Object(book.Id, l.Href) is { } card)
        {
            var text = Update(card.Data, before!.Address, saved.Address, saved.Name);
            store.Put(book.Id, card.Href, text, VCardData.Inspect(text));
            Changed(accountId, book, card.Href, text);
        }
        else if (books.Count > 0)
        {
            // Saved before under another card (an address the phone knows): that card gets the name only if it has none.
            if (Link(accountId, saved.Id) is { } other && store.CollectionById(accountId, other.Book) is { } otherBook &&
                store.Object(otherBook.Id, other.Href) is not null)
            {
                return contacts.Get(accountId, saved.Id)!;
            }

            var uid = Guid.NewGuid().ToString("D").ToUpperInvariant();
            var text = Build(uid, saved.Name, saved.Address);
            store.Put(books[0].Id, uid + ".vcf", text, VCardData.Inspect(text));
            Changed(accountId, books[0], uid + ".vcf", text);
        }

        return contacts.Get(accountId, saved.Id)!;
    }

    /// <summary>Deletes a contact; its address is removed from the vCard (the vCard goes when it has no address left).</summary>
    public bool Delete(long accountId, long id)
    {
        if (contacts.Get(accountId, id) is not { } contact)
        {
            return false;
        }

        if (Link(accountId, id) is { } link && store.CollectionById(accountId, link.Book) is { } book && store.Object(book.Id, link.Href) is { } card)
        {
            if (RemoveAddress(card.Data, contact.Address) is { } rest)
            {
                store.Put(book.Id, card.Href, rest, VCardData.Inspect(rest));
                Changed(accountId, book, card.Href, rest);
            }
            else
            {
                store.Delete(book.Id, card.Href);
                Changed(accountId, book, card.Href, null);
            }
        }

        return contacts.Delete(accountId, id);
    }

    /// <summary>Imports a .vcf file (one or many cards) into the default address book; returns the number of cards.</summary>
    public int Import(long accountId, string text)
    {
        var books = store.Collections(accountId, DavStore.AddressBook);
        var count = 0;
        foreach (var single in SplitCards(text))
        {
            if (books.Count == 0)
            {
                // No address book (deleted on a device): at least the addresses.
                foreach (var (name, address) in VCards.Read(single))
                {
                    contacts.Save(accountId, address, name);
                }

                count++;
                continue;
            }

            var card = VCardData.Property(single, "UID") is { Length: > 0 } ? single : AddUid(single);
            DavObjectInfo info;
            try
            {
                info = VCardData.Inspect(card);
            }
            catch (FormatException)
            {
                continue;
            }

            var href = store.ObjectByUid(books[0].Id, info.Uid)?.Href ?? SafeName(info.Uid) + ".vcf";
            try
            {
                store.Put(books[0].Id, href, card, info);
            }
            catch (DavConflictException)
            {
                continue;
            }

            Changed(accountId, books[0], href, card);
            count++;
        }

        return count;
    }

    /// <summary>All vCards of all address books, plus saved contacts that have none.</summary>
    public string Export(long accountId)
    {
        EnsureCards(accountId);
        var builder = new StringBuilder();
        foreach (var book in store.Collections(accountId, DavStore.AddressBook))
        {
            foreach (var card in store.Objects(book.Id))
            {
                builder.Append(card.Data.TrimEnd()).Append("\r\n");
            }
        }

        if (builder.Length == 0)
        {
            builder.Append(VCards.Write(contacts.List(accountId).Where(c => c.Saved)));
        }

        return builder.ToString();
    }

    public static IEnumerable<string> SplitCards(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        StringBuilder? current = null;
        foreach (var line in lines)
        {
            if (line.TrimEnd().Equals("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase))
            {
                current = new StringBuilder();
            }

            current?.Append(line.TrimEnd('\r')).Append("\r\n");
            if (current is not null && line.TrimEnd().Equals("END:VCARD", StringComparison.OrdinalIgnoreCase))
            {
                yield return current.ToString();
                current = null;
            }
        }
    }

    public static string Build(string uid, string? name, string address)
    {
        var display = string.IsNullOrWhiteSpace(name) ? address : name.Trim();
        var parts = display.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var structured = parts.Length > 1 ? $"{Escape(parts[^1])};{Escape(string.Join(' ', parts[..^1]))};;;" : $"{Escape(display)};;;;";
        return $"BEGIN:VCARD\r\nVERSION:3.0\r\nPRODID:-//Mailserver//Adressbuch//DE\r\nUID:{uid}\r\nFN:{Escape(display)}\r\nN:{structured}\r\n" +
               $"EMAIL;TYPE=INTERNET:{address}\r\nREV:{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}\r\nEND:VCARD\r\n";
    }

    /// <summary>The vCard with a new name and the address changed; every other line as it was.</summary>
    public static string Update(string text, string oldAddress, string newAddress, string? name)
    {
        var lines = VCardData.Unfold(text).Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();
        var replaced = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var (property, value) = Split(lines[i]);
            if (property == "EMAIL" && Address(value).Equals(oldAddress, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = lines[i][..(lines[i].IndexOf(':') + 1)] + newAddress;
                replaced = true;
            }
            else if (property == "FN" && !string.IsNullOrWhiteSpace(name))
            {
                lines[i] = "FN:" + Escape(name.Trim());
            }
            else if (property == "REV")
            {
                lines[i] = $"REV:{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}";
            }
        }

        var end = lines.FindLastIndex(l => l.Trim().Equals("END:VCARD", StringComparison.OrdinalIgnoreCase));
        if (!replaced && end >= 0)
        {
            lines.Insert(end, "EMAIL;TYPE=INTERNET:" + newAddress);
        }

        if (!lines.Any(l => Split(l).Property == "FN") && end >= 0)
        {
            lines.Insert(lines.FindLastIndex(l => l.Trim().Equals("END:VCARD", StringComparison.OrdinalIgnoreCase)),
                "FN:" + Escape(string.IsNullOrWhiteSpace(name) ? newAddress : name.Trim()));
        }

        return string.Join("\r\n", lines) + "\r\n";
    }

    /// <summary>The vCard without that address; null if it then has no address left.</summary>
    public static string? RemoveAddress(string text, string address)
    {
        var lines = VCardData.Unfold(text).Replace("\r\n", "\n").TrimEnd('\n').Split('\n')
            .Where(l => !(Split(l).Property == "EMAIL" && Address(Split(l).Value).Equals(address, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return lines.Any(l => Split(l).Property == "EMAIL") ? string.Join("\r\n", lines) + "\r\n" : null;
    }

    private (long Book, string Href)? Link(long accountId, long contactId)
    {
        using var connection = database.Open();
        return connection.Query(
            "SELECT card_collection, card_href FROM contacts WHERE account_id = $account AND id = $id AND card_href IS NOT NULL",
            r => (r.GetInt64(0), r.GetString(1)), ("$account", accountId), ("$id", contactId)).Select(x => ((long, string)?)x).SingleOrDefault();
    }

    private static void Unlink(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction transaction, long accountId,
        string address)
    {
        // Still a suggestion if mail was sent to it; otherwise gone.
        connection.Execute("DELETE FROM contacts WHERE account_id = $account AND address = $address AND times_used = 0", transaction,
            ("$account", accountId), ("$address", address));
        connection.Execute("UPDATE contacts SET saved = 0, card_collection = NULL, card_href = NULL WHERE account_id = $account AND address = $address",
            transaction, ("$account", accountId), ("$address", address));
    }

    private static (string Property, string Value) Split(string line)
    {
        var colon = line.IndexOf(':');
        if (colon <= 0)
        {
            return ("", "");
        }

        var property = line[..colon].Split(';')[0];
        property = property[(property.LastIndexOf('.') + 1)..].ToUpperInvariant();
        return (property, line[(colon + 1)..].Trim());
    }

    private static string Address(string value) => value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? value[7..] : value;

    private static string AddUid(string card)
    {
        var index = card.IndexOf("\r\n", StringComparison.Ordinal);
        // After BEGIN:VCARD (and VERSION, if it follows).
        var afterVersion = card.IndexOf("\r\n", index + 2, StringComparison.Ordinal);
        var insertAt = card[(index + 2)..].StartsWith("VERSION:", StringComparison.OrdinalIgnoreCase) && afterVersion > 0 ? afterVersion + 2 : index + 2;
        return card.Insert(insertAt, $"UID:{Guid.NewGuid().ToString("D").ToUpperInvariant()}\r\n");
    }

    private static string SafeName(string uid)
    {
        var name = string.Concat(uid.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_')).Trim('.');
        return name.Length is > 0 and <= 120
            ? name
            : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(uid)))[..32];
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace(",", "\\,").Replace(";", "\\;").Replace("\n", "\\n");
}
