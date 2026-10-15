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
        """
        ALTER TABLE push_subscriptions ADD COLUMN last_error TEXT;
        """,
        // Webmail lists a folder newest first, one page at a time.
        """
        CREATE INDEX ix_messages_folder_date ON messages (folder_id, internal_date_utc, uid);
        """,
        // "Passwort vergessen": an external address per mailbox (confirmed by a link) and one-time reset links.
        // Only SHA-256 hashes of the links are stored.
        """
        CREATE TABLE recovery_addresses (
            account_id        INTEGER PRIMARY KEY REFERENCES accounts(id) ON DELETE CASCADE,
            address           TEXT NOT NULL,
            verified_utc      TEXT,
            token_hash        TEXT,
            token_expires_utc TEXT
        );
        CREATE TABLE password_resets (
            token_hash  TEXT PRIMARY KEY,
            account_id  INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            expires_utc TEXT NOT NULL
        );
        """,
        // Mail addresses at other providers: fetched by IMAP into a folder of the mailbox, sent through the provider's SMTP
        // server. Passwords are encrypted (SecretProtector). last_uid NULL: on the first fetch only remember where the inbox
        // stands; 0: fetch everything already there.
        """
        CREATE TABLE external_accounts (
            id             INTEGER PRIMARY KEY,
            account_id     INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            address        TEXT NOT NULL COLLATE NOCASE,
            folder         TEXT NOT NULL,
            imap_host      TEXT NOT NULL,
            imap_port      INTEGER NOT NULL,
            imap_security  TEXT NOT NULL,
            smtp_host      TEXT,
            smtp_port      INTEGER,
            smtp_security  TEXT,
            user_name      TEXT NOT NULL,
            password       TEXT NOT NULL,
            enabled        INTEGER NOT NULL DEFAULT 1,
            uid_validity   INTEGER,
            last_uid       INTEGER,
            fetched        INTEGER NOT NULL DEFAULT 0,
            last_fetch_utc TEXT,
            last_error     TEXT,
            created_utc    TEXT NOT NULL,
            UNIQUE (account_id, address)
        );
        """,
        // Outlook.com and friends sign in through Microsoft: then "password" holds the (encrypted) refresh token.
        """
        ALTER TABLE external_accounts ADD COLUMN auth TEXT NOT NULL DEFAULT 'Password';
        """,
        // Two-factor sign-in for the web interface: TOTP secret (encrypted), one-time recovery codes and devices that may
        // skip the code for a while. Only hashes of recovery codes and device tokens are stored.
        """
        CREATE TABLE totp (
            account_id  INTEGER PRIMARY KEY REFERENCES accounts(id) ON DELETE CASCADE,
            secret      TEXT NOT NULL,
            enabled_utc TEXT NOT NULL,
            last_step   INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE recovery_codes (
            account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            code_hash  TEXT NOT NULL,
            PRIMARY KEY (account_id, code_hash)
        ) WITHOUT ROWID;
        CREATE TABLE trusted_devices (
            id            INTEGER PRIMARY KEY,
            account_id    INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            token_hash    TEXT NOT NULL UNIQUE,
            stamp         TEXT NOT NULL,
            device        TEXT NOT NULL,
            created_utc   TEXT NOT NULL,
            last_used_utc TEXT NOT NULL,
            expires_utc   TEXT NOT NULL
        );
        CREATE INDEX ix_trusted_devices_account ON trusted_devices (account_id);
        """,
        // Webmail signatures, one per sender address (own address, aliases, other providers' accounts).
        """
        CREATE TABLE signatures (
            account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            address    TEXT NOT NULL COLLATE NOCASE,
            text       TEXT NOT NULL,
            in_replies INTEGER NOT NULL DEFAULT 1,
            PRIMARY KEY (account_id, address)
        ) WITHOUT ROWID;
        """,
        // Optional address per domain that receives mail for unknown recipients ("Sammeladresse").
        """
        ALTER TABLE domains ADD COLUMN catch_all TEXT;
        """,
        // Full-text search for the webmail: one document per message file (copies share it), filled in the background.
        // The trigram tokenizer also finds parts of words ("rechnung" in "Stromrechnung").
        """
        CREATE TABLE search_docs (
            id             INTEGER PRIMARY KEY,
            file_name      TEXT NOT NULL UNIQUE,
            has_attachment INTEGER NOT NULL DEFAULT 0
        );
        CREATE VIRTUAL TABLE search_text USING fts5(subject, sender, recipients, body, attachments, tokenize = 'trigram');
        CREATE INDEX ix_messages_file ON messages (file_name);
        """,
        // Address book: saved contacts and the recipients of sent mail (for suggestions while typing).
        """
        CREATE TABLE contacts (
            id            INTEGER PRIMARY KEY,
            account_id    INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            address       TEXT NOT NULL COLLATE NOCASE,
            name          TEXT,
            saved         INTEGER NOT NULL DEFAULT 0,
            times_used    INTEGER NOT NULL DEFAULT 0,
            last_used_utc TEXT,
            UNIQUE (account_id, address)
        );
        CREATE TABLE contacts_seeded (
            account_id INTEGER PRIMARY KEY REFERENCES accounts(id) ON DELETE CASCADE
        );
        """,
        // DMARC aggregate reports from other providers: who sends in the name of our domains, and does SPF/DKIM pass.
        """
        CREATE TABLE dmarc_reports (
            id           INTEGER PRIMARY KEY,
            org          TEXT NOT NULL,
            report_id    TEXT NOT NULL,
            domain       TEXT NOT NULL COLLATE NOCASE,
            begin_utc    TEXT NOT NULL,
            end_utc      TEXT NOT NULL,
            policy       TEXT NOT NULL,
            received_utc TEXT NOT NULL,
            UNIQUE (org, report_id)
        );
        CREATE INDEX ix_dmarc_reports_end ON dmarc_reports (end_utc);
        CREATE TABLE dmarc_rows (
            report      INTEGER NOT NULL REFERENCES dmarc_reports(id) ON DELETE CASCADE,
            source_ip   TEXT NOT NULL,
            count       INTEGER NOT NULL,
            disposition TEXT NOT NULL,
            dkim        TEXT NOT NULL,
            spf         TEXT NOT NULL,
            header_from TEXT NOT NULL,
            dkim_domain TEXT,
            spf_domain  TEXT
        );
        CREATE INDEX ix_dmarc_rows_report ON dmarc_rows (report);
        """,
        // Small values the server remembers between starts (e.g. when the weekly report was sent).
        """
        CREATE TABLE app_state (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        ) WITHOUT ROWID;
        """,
        // TLS reports (RFC 8460): could the other servers deliver to us over TLS, and if not, why.
        """
        CREATE TABLE tls_reports (
            id           INTEGER PRIMARY KEY,
            org          TEXT NOT NULL,
            report_id    TEXT NOT NULL,
            domain       TEXT NOT NULL COLLATE NOCASE,
            policy_type  TEXT NOT NULL,
            begin_utc    TEXT NOT NULL,
            end_utc      TEXT NOT NULL,
            successful   INTEGER NOT NULL,
            failed       INTEGER NOT NULL,
            received_utc TEXT NOT NULL,
            UNIQUE (org, report_id, domain)
        );
        CREATE INDEX ix_tls_reports_end ON tls_reports (end_utc);
        CREATE TABLE tls_failures (
            report       INTEGER NOT NULL REFERENCES tls_reports(id) ON DELETE CASCADE,
            result_type  TEXT NOT NULL,
            sending_ip   TEXT,
            receiving_mx TEXT,
            count        INTEGER NOT NULL
        );
        CREATE INDEX ix_tls_failures_report ON tls_failures (report);
        """,
        // Mail to send later (the message waits in the folder "Geplant") and mail to show again later ("Wiedervorlage").
        // Both are found by their file, which stays the same when a message is moved.
        """
        CREATE TABLE scheduled_sends (
            id          INTEGER PRIMARY KEY,
            account_id  INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            file_name   TEXT NOT NULL,
            send_utc    TEXT NOT NULL,
            created_utc TEXT NOT NULL,
            last_error  TEXT
        );
        CREATE INDEX ix_scheduled_sends_due ON scheduled_sends (send_utc);
        CREATE TABLE follow_ups (
            id            INTEGER PRIMARY KEY,
            account_id    INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            file_name     TEXT NOT NULL,
            return_folder TEXT NOT NULL,
            due_utc       TEXT NOT NULL,
            created_utc   TEXT NOT NULL
        );
        CREATE INDEX ix_follow_ups_due ON follow_ups (due_utc);
        """,
        // Conversations: which messages belong together (Message-ID, References, In-Reply-To), and webmail preferences.
        """
        CREATE TABLE conversation_docs (
            file_name   TEXT PRIMARY KEY,
            account_id  INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            thread_id   INTEGER NOT NULL,
            message_id  TEXT,
            subject_key TEXT NOT NULL,
            date_utc    TEXT NOT NULL
        );
        CREATE INDEX ix_conversation_docs_thread ON conversation_docs (thread_id);
        CREATE INDEX ix_conversation_docs_subject ON conversation_docs (account_id, subject_key, date_utc);
        CREATE TABLE conversation_ids (
            account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            message_id TEXT NOT NULL,
            thread_id  INTEGER NOT NULL,
            PRIMARY KEY (account_id, message_id)
        ) WITHOUT ROWID;
        CREATE INDEX ix_conversation_ids_thread ON conversation_ids (thread_id);
        CREATE TABLE account_prefs (
            account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            name       TEXT NOT NULL,
            value      TEXT NOT NULL,
            PRIMARY KEY (account_id, name)
        ) WITHOUT ROWID;
        """,
        // Calendars and address books (CalDAV/CardDAV): collections, their objects (iCalendar/vCard text) and deleted ones for sync.
        """
        CREATE TABLE dav_collections (
            id          INTEGER PRIMARY KEY,
            account_id  INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            kind        TEXT NOT NULL,
            slug        TEXT NOT NULL,
            name        TEXT NOT NULL,
            color       TEXT,
            description TEXT,
            sync_rev    INTEGER NOT NULL DEFAULT 0,
            created_utc TEXT NOT NULL,
            UNIQUE (account_id, kind, slug)
        );
        CREATE TABLE dav_objects (
            id            INTEGER PRIMARY KEY,
            collection_id INTEGER NOT NULL REFERENCES dav_collections(id) ON DELETE CASCADE,
            href          TEXT NOT NULL,
            uid           TEXT NOT NULL,
            etag          TEXT NOT NULL,
            data          TEXT NOT NULL,
            component     TEXT,
            start_utc     TEXT,
            end_utc       TEXT,
            rev           INTEGER NOT NULL,
            modified_utc  TEXT NOT NULL,
            UNIQUE (collection_id, href)
        );
        CREATE INDEX ix_dav_objects_uid ON dav_objects (collection_id, uid);
        CREATE INDEX ix_dav_objects_rev ON dav_objects (collection_id, rev);
        CREATE TABLE dav_tombstones (
            collection_id INTEGER NOT NULL REFERENCES dav_collections(id) ON DELETE CASCADE,
            href          TEXT NOT NULL,
            rev           INTEGER NOT NULL,
            PRIMARY KEY (collection_id, href)
        ) WITHOUT ROWID;
        """,
        // Saved contacts belong to a vCard of an address book (CardDAV), so the phone and the web interface show the same.
        """
        ALTER TABLE contacts ADD COLUMN card_collection INTEGER;
        ALTER TABLE contacts ADD COLUMN card_href TEXT;
        CREATE INDEX ix_contacts_card ON contacts (card_collection, card_href);
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
