using Microsoft.Data.Sqlite;

namespace Mailserver.Core.Data;

/// <summary>
/// SQLite access and schema migrations. Connections are pooled, so callers open one per operation.
/// </summary>
public sealed class Database
{
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE domains (
            id            INTEGER PRIMARY KEY,
            name          TEXT NOT NULL UNIQUE COLLATE NOCASE,
            dkim_selector TEXT,
            created_utc   TEXT NOT NULL
        );

        CREATE TABLE accounts (
            id            INTEGER PRIMARY KEY,
            domain_id     INTEGER NOT NULL REFERENCES domains(id) ON DELETE CASCADE,
            local_part    TEXT NOT NULL COLLATE NOCASE,
            password_hash TEXT NOT NULL,
            quota_bytes   INTEGER NOT NULL DEFAULT 0,
            enabled       INTEGER NOT NULL DEFAULT 1,
            created_utc   TEXT NOT NULL,
            UNIQUE (domain_id, local_part)
        );

        CREATE TABLE aliases (
            id      INTEGER PRIMARY KEY,
            address TEXT NOT NULL UNIQUE COLLATE NOCASE,
            targets TEXT NOT NULL
        );

        CREATE TABLE folders (
            id           INTEGER PRIMARY KEY,
            account_id   INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            name         TEXT NOT NULL,
            uid_validity INTEGER NOT NULL,
            uid_next     INTEGER NOT NULL DEFAULT 1,
            subscribed   INTEGER NOT NULL DEFAULT 1,
            UNIQUE (account_id, name)
        );

        CREATE TABLE messages (
            id                INTEGER PRIMARY KEY,
            folder_id         INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
            uid               INTEGER NOT NULL,
            flags             TEXT NOT NULL DEFAULT '',
            internal_date_utc TEXT NOT NULL,
            size              INTEGER NOT NULL,
            file_name         TEXT NOT NULL,
            UNIQUE (folder_id, uid)
        );

        CREATE TABLE queue (
            id               INTEGER PRIMARY KEY,
            message_file     TEXT NOT NULL,
            sender           TEXT NOT NULL,
            recipient        TEXT NOT NULL,
            recipient_domain TEXT NOT NULL,
            created_utc      TEXT NOT NULL,
            next_attempt_utc TEXT NOT NULL,
            attempts         INTEGER NOT NULL DEFAULT 0,
            last_error       TEXT
        );

        CREATE INDEX ix_queue_next_attempt ON queue (next_attempt_utc);
        CREATE INDEX ix_queue_message_file ON queue (message_file);
        """,
    ];

    private readonly string _connectionString;

    public Database(DataPaths paths)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        connection.Execute("PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 10000;");
        return connection;
    }

    /// <summary>Creates or upgrades the schema. Safe to call on every start.</summary>
    public void Migrate()
    {
        using var connection = Open();
        connection.Execute("PRAGMA journal_mode = WAL;");

        var version = Convert.ToInt32(connection.Scalar("PRAGMA user_version;"));
        for (var i = version; i < Migrations.Length; i++)
        {
            using var transaction = connection.BeginTransaction();
            connection.Execute(Migrations[i], transaction);
            connection.Execute($"PRAGMA user_version = {i + 1};", transaction);
            transaction.Commit();
        }
    }
}
