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
        """
        -- Incremented on every change to a folder or its messages, so IMAP sessions can skip resyncing unchanged folders.
        ALTER TABLE folders ADD COLUMN change_counter INTEGER NOT NULL DEFAULT 0;
        """,
        """
        -- Messages taken over from another server, so repeated imports only copy what is new.
        CREATE TABLE import_log (
            account_id    INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            source_folder TEXT NOT NULL,
            uid_validity  INTEGER NOT NULL,
            uid           INTEGER NOT NULL,
            imported_utc  TEXT NOT NULL,
            PRIMARY KEY (account_id, source_folder, uid_validity, uid)
        );
        """,
        """
        -- Filter rules. scope: '*' (all mailboxes), a domain, or a mailbox address.
        CREATE TABLE rules (
            id          INTEGER PRIMARY KEY,
            scope       TEXT NOT NULL COLLATE NOCASE,
            name        TEXT NOT NULL,
            priority    INTEGER NOT NULL DEFAULT 100,
            enabled     INTEGER NOT NULL DEFAULT 1,
            match_all   INTEGER NOT NULL DEFAULT 1,
            conditions  TEXT NOT NULL,
            action      TEXT NOT NULL,
            argument    TEXT,
            stop        INTEGER NOT NULL DEFAULT 1,
            created_utc TEXT NOT NULL
        );

        CREATE TABLE greylist (
            triplet        TEXT PRIMARY KEY,
            first_seen_utc TEXT NOT NULL,
            last_seen_utc  TEXT NOT NULL,
            passed         INTEGER NOT NULL DEFAULT 0
        );
        """,
        """
        -- Every spam decision, for tuning the filter (see mailadmin spamlog).
        CREATE TABLE spam_log (
            id          INTEGER PRIMARY KEY,
            time_utc    TEXT NOT NULL,
            session     TEXT,
            stage       TEXT NOT NULL,
            action      TEXT NOT NULL,
            client_ip   TEXT,
            reverse_dns TEXT,
            helo        TEXT,
            mail_from   TEXT,
            recipient   TEXT,
            header_from TEXT,
            subject     TEXT,
            message_id  TEXT,
            score       REAL,
            tests       TEXT,
            spf         TEXT,
            dkim        TEXT,
            dmarc       TEXT,
            folder      TEXT,
            rules       TEXT,
            detail      TEXT
        );

        CREATE INDEX ix_spam_log_time ON spam_log (time_utc);
        CREATE INDEX ix_spam_log_session ON spam_log (session);
        CREATE INDEX ix_spam_log_message_id ON spam_log (message_id);
        """,
        """
        ALTER TABLE accounts ADD COLUMN is_admin INTEGER NOT NULL DEFAULT 0;
        CREATE INDEX ix_spam_log_recipient ON spam_log (recipient);
        """,
        """
        CREATE TABLE sent_copies (
            account_id  INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            message_id  TEXT NOT NULL,
            folder_id   INTEGER NOT NULL,
            uid         INTEGER NOT NULL,
            created_utc TEXT NOT NULL,
            PRIMARY KEY (account_id, message_id)
        );
        """,
        """
        CREATE TABLE mailbox_settings (
            account_id              INTEGER PRIMARY KEY REFERENCES accounts(id) ON DELETE CASCADE,
            forward_to              TEXT NOT NULL DEFAULT '',
            forward_keep_copy       INTEGER NOT NULL DEFAULT 1,
            autoreply_enabled       INTEGER NOT NULL DEFAULT 0,
            autoreply_subject       TEXT NOT NULL DEFAULT '',
            autoreply_body          TEXT NOT NULL DEFAULT '',
            autoreply_from          TEXT NULL,
            autoreply_until         TEXT NULL,
            autoreply_interval_days INTEGER NOT NULL DEFAULT 7
        );

        CREATE TABLE autoreply_log (
            account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            sender     TEXT NOT NULL,
            sent_utc   TEXT NOT NULL,
            PRIMARY KEY (account_id, sender)
        );
        """,
        """
        ALTER TABLE mailbox_settings ADD COLUMN send_limit_hour INTEGER NULL;
        ALTER TABLE mailbox_settings ADD COLUMN send_limit_day INTEGER NULL;
        ALTER TABLE mailbox_settings ADD COLUMN send_blocked_utc TEXT NULL;
        ALTER TABLE mailbox_settings ADD COLUMN send_blocked_reason TEXT NULL;

        CREATE TABLE send_log (
            sender_key TEXT NOT NULL,
            sent_utc   TEXT NOT NULL,
            recipients INTEGER NOT NULL
        );
        CREATE INDEX ix_send_log ON send_log (sender_key, sent_utc);
        """,
        """
        CREATE TABLE backup_runs (
            id           INTEGER PRIMARY KEY,
            started_utc  TEXT NOT NULL,
            finished_utc TEXT,
            success      INTEGER,
            snapshot     TEXT,
            files_copied INTEGER NOT NULL DEFAULT 0,
            bytes_copied INTEGER NOT NULL DEFAULT 0,
            message      TEXT
        );
        """,
        """
        -- Addresses or networks that are always blocked or never locked out.
        CREATE TABLE ip_rules (
            id          INTEGER PRIMARY KEY,
            network     TEXT NOT NULL,
            kind        TEXT NOT NULL,
            comment     TEXT,
            created_utc TEXT NOT NULL,
            expires_utc TEXT
        );
        """,
        """
        -- Mail files already in the backup store, so only new ones are uploaded (a cloud cannot be listed cheaply every night).
        CREATE TABLE backup_files (
            store_key         TEXT NOT NULL,
            path              TEXT NOT NULL,
            size              INTEGER NOT NULL,
            missing_since_utc TEXT,
            PRIMARY KEY (store_key, path)
        ) WITHOUT ROWID;
        """,
        """
        -- Browsers and installed apps that get a push notification for new mail.
        CREATE TABLE push_subscriptions (
            id            INTEGER PRIMARY KEY,
            account_id    INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            endpoint      TEXT NOT NULL UNIQUE,
            p256dh        TEXT NOT NULL,
            auth          TEXT NOT NULL,
            device        TEXT NOT NULL,
            created_utc   TEXT NOT NULL,
            last_sent_utc TEXT,
            failures      INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX ix_push_subscriptions_account ON push_subscriptions (account_id);
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
