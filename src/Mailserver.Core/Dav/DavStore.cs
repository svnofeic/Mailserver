using System.Security.Cryptography;
using System.Text;
using Mailserver.Core.Data;
using Microsoft.Data.Sqlite;

namespace Mailserver.Core.Dav;

/// <summary>A calendar or an address book of a mailbox.</summary>
/// <param name="Slug">The last segment of its URL.</param>
/// <param name="SyncRev">Grows with every change; the sync token and "ctag" of the collection.</param>
public sealed record DavCollection(long Id, long AccountId, string Kind, string Slug, string Name, string? Color, string? Description, long SyncRev);

/// <summary>An event or task (iCalendar text) in a calendar, or a contact (vCard text) in an address book.</summary>
/// <param name="Href">The file name in the collection, e.g. "1234.ics".</param>
/// <param name="Start">Start of the first occurrence (UTC); null if it has none (a task without dates).</param>
/// <param name="End">End of the last occurrence; null if it repeats without end or has no dates.</param>
public sealed record DavObject(long Id, long CollectionId, string Href, string Uid, string ETag, string Data, string? Component,
    DateTimeOffset? Start, DateTimeOffset? End, long Rev, DateTimeOffset Modified);

/// <summary>What to store about an object besides its text.</summary>
public sealed record DavObjectInfo(string Uid, string? Component, DateTimeOffset? Start, DateTimeOffset? End);

public sealed class DavConflictException(string message) : Exception(message);

/// <summary>Storage of calendars and address books (CalDAV/CardDAV), with the change history clients sync with.</summary>
public sealed class DavStore(Database database, TimeProvider timeProvider)
{
    public const string Calendar = "calendar";
    public const string AddressBook = "addressbook";
    public const string DefaultSlug = "default";
    public const int MaxCollections = 50;

    private const string CollectionColumns = "id, account_id, kind, slug, name, color, description, sync_rev";
    private const string ObjectColumns = "id, collection_id, href, uid, etag, data, component, start_utc, end_utc, rev, modified_utc";

    /// <summary>The calendars (or address books) of the mailbox; the first call creates the default one.</summary>
    public IReadOnlyList<DavCollection> Collections(long accountId, string kind)
    {
        using var connection = database.Open();
        var list = connection.Query($"SELECT {CollectionColumns} FROM dav_collections WHERE account_id = $account AND kind = $kind ORDER BY id",
            ReadCollection, ("$account", accountId), ("$kind", kind));
        if (list.Count > 0)
        {
            return list;
        }

        // Created once; deleting it later leaves the mailbox without (until the user adds one).
        if (connection.Execute("INSERT OR IGNORE INTO app_state (key, value) VALUES ($key, '1')", ("$key", $"dav-default:{kind}:{accountId}")) == 0)
        {
            return list;
        }

        connection.Execute(
            "INSERT OR IGNORE INTO dav_collections (account_id, kind, slug, name, color, created_utc) VALUES ($account, $kind, $slug, $name, $color, $now)",
            ("$account", accountId), ("$kind", kind), ("$slug", DefaultSlug), ("$name", kind == Calendar ? "Kalender" : "Kontakte"),
            ("$color", kind == Calendar ? "#7C5CFFFF" : null), ("$now", timeProvider.GetUtcNow().ToDbTime()));
        return connection.Query($"SELECT {CollectionColumns} FROM dav_collections WHERE account_id = $account AND kind = $kind ORDER BY id",
            ReadCollection, ("$account", accountId), ("$kind", kind));
    }

    public DavCollection? Collection(long accountId, string kind, string slug)
    {
        Collections(accountId, kind);
        using var connection = database.Open();
        return connection.Query($"SELECT {CollectionColumns} FROM dav_collections WHERE account_id = $account AND kind = $kind AND slug = $slug",
            ReadCollection, ("$account", accountId), ("$kind", kind), ("$slug", slug)).SingleOrDefault();
    }

    public DavCollection? CollectionById(long accountId, long id)
    {
        using var connection = database.Open();
        return connection.Query($"SELECT {CollectionColumns} FROM dav_collections WHERE account_id = $account AND id = $id",
            ReadCollection, ("$account", accountId), ("$id", id)).SingleOrDefault();
    }

    /// <summary>Throws <see cref="DavConflictException"/> if the slug is taken or there are too many.</summary>
    public DavCollection CreateCollection(long accountId, string kind, string slug, string name, string? color = null, string? description = null)
    {
        if (!IsValidSegment(slug))
        {
            throw new ArgumentException("Ungültiger Name.");
        }

        using var connection = database.Open();
        if (Convert.ToInt32(connection.Scalar("SELECT COUNT(*) FROM dav_collections WHERE account_id = $account AND kind = $kind",
                ("$account", accountId), ("$kind", kind))) >= MaxCollections)
        {
            throw new DavConflictException($"Höchstens {MaxCollections} Stück.");
        }

        if (connection.Execute(
                "INSERT OR IGNORE INTO dav_collections (account_id, kind, slug, name, color, description, created_utc) VALUES ($account, $kind, $slug, $name, $color, $description, $now)",
                ("$account", accountId), ("$kind", kind), ("$slug", slug), ("$name", string.IsNullOrWhiteSpace(name) ? slug : name.Trim()),
                ("$color", color), ("$description", description), ("$now", timeProvider.GetUtcNow().ToDbTime())) == 0)
        {
            throw new DavConflictException("Gibt es schon.");
        }

        return connection.Query($"SELECT {CollectionColumns} FROM dav_collections WHERE account_id = $account AND kind = $kind AND slug = $slug",
            ReadCollection, ("$account", accountId), ("$kind", kind), ("$slug", slug)).Single();
    }

    /// <summary>Changes name, color or description (null leaves a value as it is).</summary>
    public void UpdateCollection(long id, string? name = null, string? color = null, string? description = null)
    {
        using var connection = database.Open();
        connection.Execute(
            """
            UPDATE dav_collections SET name = COALESCE($name, name), color = COALESCE($color, color),
                description = COALESCE($description, description), sync_rev = sync_rev + 1 WHERE id = $id
            """,
            ("$id", id), ("$name", string.IsNullOrWhiteSpace(name) ? null : name.Trim()), ("$color", color), ("$description", description));
    }

    public void DeleteCollection(long id)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM dav_collections WHERE id = $id", ("$id", id));
    }

    public IReadOnlyList<DavObject> Objects(long collectionId)
    {
        using var connection = database.Open();
        return connection.Query($"SELECT {ObjectColumns} FROM dav_objects WHERE collection_id = $id ORDER BY id", ReadObject, ("$id", collectionId));
    }

    /// <summary>Objects that may have an occurrence between <paramref name="from"/> and <paramref name="to"/> (either may be open).</summary>
    public IReadOnlyList<DavObject> ObjectsInRange(long collectionId, DateTimeOffset? from, DateTimeOffset? to, string? component = null)
    {
        using var connection = database.Open();
        return connection.Query(
            $"""
            SELECT {ObjectColumns} FROM dav_objects WHERE collection_id = $id AND ($component IS NULL OR component = $component)
              AND ($to IS NULL OR start_utc IS NULL OR start_utc < $to)
              AND ($from IS NULL OR (end_utc IS NULL AND start_utc IS NULL) OR end_utc IS NULL OR end_utc > $from)
            ORDER BY start_utc
            """, ReadObject, ("$id", collectionId), ("$component", component), ("$from", from?.ToDbTime()), ("$to", to?.ToDbTime()));
    }

    public DavObject? Object(long collectionId, string href)
    {
        using var connection = database.Open();
        return connection.Query($"SELECT {ObjectColumns} FROM dav_objects WHERE collection_id = $id AND href = $href", ReadObject,
            ("$id", collectionId), ("$href", href)).SingleOrDefault();
    }

    public DavObject? ObjectByUid(long collectionId, string uid)
    {
        using var connection = database.Open();
        return connection.Query($"SELECT {ObjectColumns} FROM dav_objects WHERE collection_id = $id AND uid = $uid", ReadObject,
            ("$id", collectionId), ("$uid", uid)).FirstOrDefault();
    }

    /// <summary>
    /// Adds or replaces an object; returns it with its new ETag. Throws <see cref="DavConflictException"/> if another object of
    /// the collection has the same UID.
    /// </summary>
    public DavObject Put(long collectionId, string href, string data, DavObjectInfo info)
    {
        if (!IsValidSegment(href))
        {
            throw new ArgumentException("Ungültiger Dateiname.");
        }

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        if (connection.Scalar("SELECT href FROM dav_objects WHERE collection_id = $id AND uid = $uid AND href <> $href", transaction,
                ("$id", collectionId), ("$uid", info.Uid), ("$href", href)) is string other)
        {
            throw new DavConflictException($"Die UID gehört schon zu {other}.");
        }

        var rev = NextRev(connection, transaction, collectionId);
        connection.Execute(
            """
            INSERT INTO dav_objects (collection_id, href, uid, etag, data, component, start_utc, end_utc, rev, modified_utc)
            VALUES ($id, $href, $uid, $etag, $data, $component, $start, $end, $rev, $now)
            ON CONFLICT (collection_id, href) DO UPDATE SET uid = $uid, etag = $etag, data = $data, component = $component,
                start_utc = $start, end_utc = $end, rev = $rev, modified_utc = $now
            """,
            transaction, ("$id", collectionId), ("$href", href), ("$uid", info.Uid), ("$etag", ETag(data)), ("$data", data),
            ("$component", info.Component), ("$start", info.Start?.ToDbTime()), ("$end", info.End?.ToDbTime()), ("$rev", rev),
            ("$now", timeProvider.GetUtcNow().ToDbTime()));
        connection.Execute("DELETE FROM dav_tombstones WHERE collection_id = $id AND href = $href", transaction, ("$id", collectionId), ("$href", href));
        transaction.Commit();
        return Object(collectionId, href)!;
    }

    public bool Delete(long collectionId, string href)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        if (connection.Execute("DELETE FROM dav_objects WHERE collection_id = $id AND href = $href", transaction,
                ("$id", collectionId), ("$href", href)) == 0)
        {
            return false;
        }

        var rev = NextRev(connection, transaction, collectionId);
        connection.Execute("INSERT OR REPLACE INTO dav_tombstones (collection_id, href, rev) VALUES ($id, $href, $rev)", transaction,
            ("$id", collectionId), ("$href", href), ("$rev", rev));
        transaction.Commit();
        return true;
    }

    /// <summary>
    /// What changed after <paramref name="sinceRev"/> (0: everything): objects added or changed, and file names deleted.
    /// </summary>
    public (IReadOnlyList<DavObject> Changed, IReadOnlyList<string> Deleted, long Rev) Changes(long collectionId, long sinceRev)
    {
        using var connection = database.Open();
        var changed = connection.Query($"SELECT {ObjectColumns} FROM dav_objects WHERE collection_id = $id AND rev > $rev ORDER BY rev",
            ReadObject, ("$id", collectionId), ("$rev", sinceRev));
        IReadOnlyList<string> deleted = sinceRev == 0
            ? []
            : connection.Query("SELECT href FROM dav_tombstones WHERE collection_id = $id AND rev > $rev ORDER BY rev", r => r.GetString(0),
                ("$id", collectionId), ("$rev", sinceRev));
        var rev = Convert.ToInt64(connection.Scalar("SELECT sync_rev FROM dav_collections WHERE id = $id", ("$id", collectionId)) ?? 0L);
        return (changed, deleted, rev);
    }

    /// <summary>A URL segment we accept as a name: no slashes, dots-only or control characters.</summary>
    public static bool IsValidSegment(string segment) =>
        segment.Length is > 0 and <= 200 && segment.Trim('.').Length > 0 && !segment.Any(c => c is '/' or '\\' || char.IsControl(c));

    public static string ETag(string data) =>
        "\"" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(data)))[..32] + "\"";

    private static long NextRev(SqliteConnection connection, SqliteTransaction transaction, long collectionId) =>
        Convert.ToInt64(connection.Scalar("UPDATE dav_collections SET sync_rev = sync_rev + 1 WHERE id = $id RETURNING sync_rev", transaction,
            ("$id", collectionId)));

    private static DavCollection ReadCollection(SqliteDataReader r) => new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetInt64(7));

    private static DavObject ReadObject(SqliteDataReader r) => new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4),
        r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetDbTime(7), r.IsDBNull(8) ? null : r.GetDbTime(8),
        r.GetInt64(9), r.GetDbTime(10));
}
