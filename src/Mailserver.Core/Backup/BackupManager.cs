using System.Globalization;
using System.Text.Json;
using Mailserver.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailserver.Core.Backup;

public sealed record BackupRun(long Id, DateTimeOffset Started, DateTimeOffset? Finished, bool? Success, string? Snapshot,
    long FilesCopied, long BytesCopied, string? Message);

public sealed record BackupSnapshot(string Name, DateTimeOffset Created, BackupManifest Manifest);

/// <summary>What a snapshot contains; uploaded last, so a snapshot without it is incomplete.</summary>
public sealed record BackupManifest(string Version, string Hostname, DateTimeOffset Created, int Accounts, long Messages);

/// <summary>
/// Backs up the data directory into a backup store (folder, OneDrive, pCloud):
/// <code>
/// snapshots/2026-10-05_030000/   database (consistent copy), settings.json, dkim, acme, queue, push, secrets, manifest.json
/// mail/                          mirror of data\mail, uploaded incrementally
/// </code>
/// The mail files never change once written, so only new ones are uploaded each night; which ones are already there is
/// kept in the table backup_files. Files deleted on the server stay in the store for <see cref="BackupOptions.KeepDays"/>,
/// so every kept snapshot can be restored completely. data\keys and data\cloud are not backed up: they only work on this
/// Windows installation.
/// </summary>
public sealed class BackupManager(
    Database database,
    DataPaths paths,
    IOptions<MailserverOptions> options,
    TimeProvider timeProvider,
    ILogger<BackupManager> logger,
    BackupStores? stores = null)
{
    public const string SnapshotsFolder = "snapshots";
    public const string MailFolder = "mail";
    public const string ManifestFile = "manifest.json";
    private static readonly string[] ConfigFolders = ["dkim", "acme", "queue", "push", "secrets"];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly BackupStores _stores = stores ?? new BackupStores(paths, new CloudTokens(paths));

    public bool Running => _gate.CurrentCount == 0;

    public BackupStores Stores => _stores;

    /// <summary>Why no backup can be made with the current settings, or null.</summary>
    public string? Problem(BackupOptions? settings = null) => _stores.Problem(settings ?? options.Value.Backup);

    /// <summary>Makes a backup now. Only one runs at a time; a second call returns the failed run right away.</summary>
    /// <param name="settings">Settings just saved (the options pick them up a moment later); null = current options.</param>
    public async Task<BackupRun> RunAsync(BackupOptions? settings = null, CancellationToken cancellationToken = default)
    {
        settings ??= options.Value.Backup;
        var started = timeProvider.GetUtcNow();
        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            return new BackupRun(0, started, started, false, null, 0, 0, "Es läuft bereits eine Sicherung.");
        }

        long id;
        using (var connection = database.Open())
        {
            id = (long)connection.Scalar("INSERT INTO backup_runs (started_utc) VALUES ($started) RETURNING id", ("$started", started.ToDbTime()))!;
        }

        var counters = new Counters();
        string? snapshot = null;
        string message;
        var success = false;
        var temp = Path.Combine(paths.Root, "backup-temp");
        try
        {
            using var store = _stores.Open(settings);
            snapshot = started.ToLocalTime().ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
            var local = Path.Combine(temp, snapshot);
            await Task.Run(() => CreateSnapshot(local, started, cancellationToken), cancellationToken);

            // Order matters: snapshot files, then the mails it refers to, and the manifest last – only then is it complete.
            foreach (var file in Directory.EnumerateFiles(local, "*", SearchOption.AllDirectories).Where(f => Path.GetFileName(f) != ManifestFile))
            {
                await store.UploadAsync(file, $"{SnapshotsFolder}/{snapshot}/{Relative(local, file)}", cancellationToken);
                counters.Add(new FileInfo(file).Length);
            }

            await MirrorMailAsync(store, settings.KeepDays, started, counters, cancellationToken);
            await store.UploadAsync(Path.Combine(local, ManifestFile), $"{SnapshotsFolder}/{snapshot}/{ManifestFile}", cancellationToken);
            var removed = await RemoveOldSnapshotsAsync(store, settings.KeepDays, started, snapshot, cancellationToken);
            success = true;
            message = $"{counters.Files} Dateien ({Format(counters.Bytes)}) nach {store.Description} übertragen" +
                      (removed > 0 ? $", {removed} alte Sicherung(en) entfernt" : "") + ".";
            logger.LogInformation("Backup {Snapshot} finished: {Message}", snapshot, message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BackupException or SqliteException
                                       or System.ComponentModel.Win32Exception or HttpRequestException or JsonException)
        {
            message = ex is HttpRequestException ? $"Verbindungsfehler: {Reasons(ex)}" : ex.Message;
            if (counters.Files > 0)
            {
                message += $" ({counters.Files} Dateien wurden schon übertragen und werden beim nächsten Mal nicht erneut übertragen.)";
            }

            logger.LogError(ex, "Backup failed");
        }
        catch (OperationCanceledException)
        {
            message = "Abgebrochen (Dienst wurde beendet).";
        }
        finally
        {
            TryDelete(temp);
            _gate.Release();
        }

        var finished = timeProvider.GetUtcNow();
        using (var connection = database.Open())
        {
            connection.Execute(
                "UPDATE backup_runs SET finished_utc = $finished, success = $success, snapshot = $snapshot, files_copied = $files, bytes_copied = $bytes, message = $message WHERE id = $id",
                ("$finished", finished.ToDbTime()), ("$success", success ? 1 : 0), ("$snapshot", success ? snapshot : null), ("$files", counters.Files),
                ("$bytes", counters.Bytes), ("$message", message), ("$id", id));
            connection.Execute("DELETE FROM backup_runs WHERE id NOT IN (SELECT id FROM backup_runs ORDER BY id DESC LIMIT 100)");
        }

        return new BackupRun(id, started, finished, success, success ? snapshot : null, counters.Files, counters.Bytes, message);
    }

    public IReadOnlyList<BackupRun> RecentRuns(int count = 20)
    {
        using var connection = database.Open();
        return connection.Query(
            "SELECT id, started_utc, finished_utc, success, snapshot, files_copied, bytes_copied, message FROM backup_runs ORDER BY id DESC LIMIT $count",
            r => new BackupRun(r.GetInt64(0), r.GetDbTime(1), r.IsDBNull(2) ? null : r.GetDbTime(2), r.IsDBNull(3) ? null : r.GetInt64(3) == 1,
                r.IsDBNull(4) ? null : r.GetString(4), r.GetInt64(5), r.GetInt64(6), r.IsDBNull(7) ? null : r.GetString(7)),
            ("$count", count));
    }

    /// <summary>The last successful backup, or null.</summary>
    public BackupRun? LastSuccess()
    {
        using var connection = database.Open();
        return connection.Query(
            "SELECT id, started_utc, finished_utc, snapshot, files_copied, bytes_copied, message FROM backup_runs WHERE success = 1 ORDER BY id DESC LIMIT 1",
            r => new BackupRun(r.GetInt64(0), r.GetDbTime(1), r.GetDbTime(2), true, r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4), r.GetInt64(5),
                r.IsDBNull(6) ? null : r.GetString(6))).FirstOrDefault();
    }

    /// <summary>Complete snapshots in the store, newest first.</summary>
    public static async Task<IReadOnlyList<BackupSnapshot>> ListSnapshotsAsync(IBackupStore store, CancellationToken cancellationToken = default)
    {
        var result = new List<BackupSnapshot>();
        foreach (var name in await store.ListFoldersAsync(SnapshotsFolder, cancellationToken))
        {
            if (ParseManifest(await store.ReadTextAsync($"{SnapshotsFolder}/{name}/{ManifestFile}", cancellationToken)) is { } manifest)
            {
                result.Add(new BackupSnapshot(name, manifest.Created, manifest));
            }
        }

        return result.OrderByDescending(s => s.Created).ToList();
    }

    public static BackupManifest? ParseManifest(string? json)
    {
        try
        {
            return json is null ? null : JsonSerializer.Deserialize<BackupManifest>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Puts a snapshot back into the data directory. The service must be stopped. The mails come from the mirror in the
    /// store; mails already in the data directory are kept.
    /// </summary>
    /// <param name="snapshot">Name of the snapshot; null = the newest.</param>
    public static async Task<RestoreResult> RestoreAsync(IBackupStore store, string? snapshot, DataPaths target, Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var snapshots = await ListSnapshotsAsync(store, cancellationToken);
        var chosen = snapshot is null ? snapshots.FirstOrDefault() : snapshots.FirstOrDefault(s => s.Name == snapshot);
        if (chosen is null)
        {
            throw new BackupException(snapshot is null
                ? $"In {store.Description} gibt es keine vollständige Sicherung."
                : $"Die Sicherung {snapshot} gibt es in {store.Description} nicht (oder sie ist unvollständig).");
        }

        progress?.Invoke($"Spiele die Sicherung {chosen.Name} zurück ({chosen.Manifest.Accounts} Postfächer, {chosen.Manifest.Messages} Mails) …");
        var temp = Path.Combine(target.Root, "restore-temp");
        TryDelete(temp);
        try
        {
            foreach (var file in await store.ListFilesAsync($"{SnapshotsFolder}/{chosen.Name}", cancellationToken))
            {
                await store.DownloadAsync($"{SnapshotsFolder}/{chosen.Name}/{file.Path}", Path.Combine(temp, Local(file.Path)), cancellationToken);
            }

            Directory.CreateDirectory(target.Root);
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                File.Delete(target.DatabaseFile + suffix);
            }

            File.Copy(Path.Combine(temp, "mailserver.db"), target.DatabaseFile);
            if (File.Exists(Path.Combine(temp, "settings.json")))
            {
                File.Copy(Path.Combine(temp, "settings.json"), target.SettingsFile, overwrite: true);
            }

            foreach (var folder in ConfigFolders)
            {
                var source = Path.Combine(temp, folder);
                if (Directory.Exists(source))
                {
                    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                    {
                        var destination = Path.Combine(target.Root, folder, Path.GetRelativePath(source, file));
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Copy(file, destination, overwrite: true);
                    }
                }
            }
        }
        finally
        {
            TryDelete(temp);
        }

        progress?.Invoke("Datenbank und Einstellungen zurückgespielt, übertrage die Mails …");
        long files = 0, bytes = 0;
        var mails = await store.ListFilesAsync(MailFolder, cancellationToken);
        foreach (var mail in mails)
        {
            var destination = Path.Combine(target.MailRoot, Local(mail.Path));
            if (File.Exists(destination) && new FileInfo(destination).Length == mail.Size)
            {
                continue;
            }

            await store.DownloadAsync($"{MailFolder}/{mail.Path}", destination, cancellationToken);
            files++;
            bytes += mail.Size;
            if (files % 1000 == 0)
            {
                progress?.Invoke($"  {files} von {mails.Count} Mails …");
            }
        }

        return new RestoreResult(chosen.Name, files, bytes, mails.Count > 0);
    }

    public sealed record RestoreResult(string Snapshot, long MailFiles, long MailBytes, bool MirrorFound);

    private void CreateSnapshot(string folder, DateTimeOffset started, CancellationToken cancellationToken)
    {
        TryDelete(folder);
        Directory.CreateDirectory(folder);

        // SQLite's online backup gives a consistent copy while the server keeps running.
        var databaseCopy = Path.Combine(folder, "mailserver.db");
        using (var source = database.Open())
        using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databaseCopy, Pooling = false }.ToString()))
        {
            destination.Open();
            source.BackupDatabase(destination);
        }

        if (File.Exists(paths.SettingsFile))
        {
            File.Copy(paths.SettingsFile, Path.Combine(folder, "settings.json"));
        }

        var appSettings = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (File.Exists(appSettings))
        {
            File.Copy(appSettings, Path.Combine(folder, "appsettings.json"));
        }

        foreach (var sub in ConfigFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = Path.Combine(paths.Root, sub);
            if (!Directory.Exists(source))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(folder, sub, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                try
                {
                    File.Copy(file, destination);
                }
                catch (FileNotFoundException)
                {
                    // deleted meanwhile, e.g. a delivered queue entry
                }
            }
        }

        using var connection = database.Open();
        var manifest = new BackupManifest(BuildInfo.Version, options.Value.Hostname, started,
            Convert.ToInt32(connection.Scalar("SELECT COUNT(*) FROM accounts")), Convert.ToInt64(connection.Scalar("SELECT COUNT(*) FROM messages")));
        File.WriteAllText(Path.Combine(folder, ManifestFile), JsonSerializer.Serialize(manifest, Json));
    }

    private async Task MirrorMailAsync(IBackupStore store, int keepDays, DateTimeOffset now, Counters counters, CancellationToken cancellationToken)
    {
        var key = store.Key;
        Dictionary<string, (long Size, DateTimeOffset? MissingSince)> index;
        using (var connection = database.Open())
        {
            // Only one target is tracked; switching back later starts from the listing below.
            connection.Execute("DELETE FROM backup_files WHERE store_key <> $key", ("$key", key));
            index = connection.Query("SELECT path, size, missing_since_utc FROM backup_files WHERE store_key = $key",
                    r => (Path: r.GetString(0), Size: r.GetInt64(1), Missing: r.IsDBNull(2) ? (DateTimeOffset?)null : r.GetDbTime(2)), ("$key", key))
                .ToDictionary(r => r.Path, r => (r.Size, r.Missing), StringComparer.Ordinal);
        }

        // The target was emptied (or is new): what is really there decides.
        if (index.Count > 0 && !(await store.ListFoldersAsync("", cancellationToken)).Contains(MailFolder))
        {
            index.Clear();
            Execute("DELETE FROM backup_files WHERE store_key = $key", ("$key", key));
        }

        if (index.Count == 0)
        {
            var existing = await store.ListFilesAsync(MailFolder, cancellationToken);
            foreach (var file in existing)
            {
                index[file.Path] = (file.Size, null);
                Execute("INSERT OR REPLACE INTO backup_files (store_key, path, size) VALUES ($key, $path, $size)",
                    ("$key", key), ("$path", file.Path), ("$size", file.Size));
            }
        }

        var source = Directory.Exists(paths.MailRoot)
            ? new DirectoryInfo(paths.MailRoot).EnumerateFiles("*", SearchOption.AllDirectories)
                .ToDictionary(f => Relative(paths.MailRoot, f.FullName), f => f.Length, StringComparer.Ordinal)
            : [];

        var upload = source.Where(f => !index.TryGetValue(f.Key, out var known) || known.Size != f.Value).ToList();
        await Parallel.ForEachAsync(upload,
            new ParallelOptions { MaxDegreeOfParallelism = store switch { FolderBackupStore => 1, PCloudStore => 2, _ => 4 }, CancellationToken = cancellationToken },
            async (file, token) =>
            {
                try
                {
                    await store.UploadAsync(Path.Combine(paths.MailRoot, Local(file.Key)), $"{MailFolder}/{file.Key}", token);
                }
                catch (FileNotFoundException)
                {
                    return; // deleted in the meantime
                }

                Execute("INSERT OR REPLACE INTO backup_files (store_key, path, size) VALUES ($key, $path, $size)",
                    ("$key", key), ("$path", file.Key), ("$size", file.Value));
                counters.Add(file.Value);
            });

        // Files gone from the server are kept as long as a snapshot may still refer to them: only snapshots made before
        // the file was first missed can, and those are removed after keepDays.
        foreach (var (path, known) in index)
        {
            if (source.ContainsKey(path))
            {
                if (known.MissingSince is not null)
                {
                    Execute("UPDATE backup_files SET missing_since_utc = NULL WHERE store_key = $key AND path = $path", ("$key", key), ("$path", path));
                }

                continue;
            }

            if (known.MissingSince is null)
            {
                Execute("UPDATE backup_files SET missing_since_utc = $now WHERE store_key = $key AND path = $path",
                    ("$now", now.ToDbTime()), ("$key", key), ("$path", path));
            }
            else if (now - known.MissingSince > TimeSpan.FromDays(keepDays))
            {
                await store.DeleteAsync($"{MailFolder}/{path}", cancellationToken);
                Execute("DELETE FROM backup_files WHERE store_key = $key AND path = $path", ("$key", key), ("$path", path));
            }
        }
    }

    private static async Task<int> RemoveOldSnapshotsAsync(IBackupStore store, int keepDays, DateTimeOffset now, string current,
        CancellationToken cancellationToken)
    {
        var complete = await ListSnapshotsAsync(store, cancellationToken);
        var removed = 0;
        foreach (var snapshot in complete.Skip(1).Where(s => now - s.Created > TimeSpan.FromDays(keepDays)))
        {
            await store.DeleteAsync($"{SnapshotsFolder}/{snapshot.Name}", cancellationToken);
            removed++;
        }

        // Leftovers of runs that were interrupted (no manifest).
        foreach (var folder in await store.ListFoldersAsync(SnapshotsFolder, cancellationToken))
        {
            if (folder != current && complete.All(s => s.Name != folder))
            {
                await store.DeleteAsync($"{SnapshotsFolder}/{folder}", cancellationToken);
            }
        }

        return removed;
    }

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = database.Open();
        connection.Execute(sql, parameters);
    }

    private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');

    private static string Local(string path) => path.Replace('/', Path.DirectorySeparatorChar);

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    public static string Format(long bytes) => (bytes switch
    {
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    }).Replace('.', ',');

    private sealed class Counters
    {
        private long _files;
        private long _bytes;

        public long Files => Interlocked.Read(ref _files);
        public long Bytes => Interlocked.Read(ref _bytes);

        public void Add(long bytes)
        {
            Interlocked.Increment(ref _files);
            Interlocked.Add(ref _bytes, bytes);
        }
    }

    /// <summary>The message with its causes ("Error while copying content to a stream." alone says nothing about why).</summary>
    private static string Reasons(Exception ex)
    {
        var messages = new List<string>();
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (!messages.Contains(current.Message))
            {
                messages.Add(current.Message);
            }
        }

        return string.Join(" → ", messages);
    }
}

public class BackupException(string message) : Exception(message);
