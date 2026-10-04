using System.Globalization;
using System.Text.Json;
using Mailserver.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailserver.Core.Backup;

public sealed record BackupRun(long Id, DateTimeOffset Started, DateTimeOffset? Finished, bool? Success, string? Snapshot,
    long FilesCopied, long BytesCopied, string? Message);

public sealed record BackupSnapshot(string Name, string Path, DateTimeOffset Created, long Bytes);

/// <summary>What a snapshot contains; written last, so a snapshot without it is incomplete.</summary>
public sealed record BackupManifest(string Version, string Hostname, DateTimeOffset Created, int Accounts, long Messages);

/// <summary>
/// Backs up the data directory into a target folder:
/// <code>
/// ziel\snapshots\2026-10-05_030000\   database (consistent copy), settings.json, dkim, acme, queue, manifest.json
/// ziel\mail\                          mirror of data\mail, copied incrementally
/// </code>
/// The mail files never change once written, so only new ones are copied each night. Files deleted on the server stay in
/// the mirror for <see cref="BackupOptions.KeepDays"/>, so every kept snapshot can be restored completely.
/// data\keys is not backed up: those keys only work on this Windows installation.
/// </summary>
public sealed class BackupManager(
    Database database,
    DataPaths paths,
    IOptions<MailserverOptions> options,
    TimeProvider timeProvider,
    ILogger<BackupManager> logger)
{
    public const string SnapshotsFolder = "snapshots";
    public const string MailFolder = "mail";
    public const string ManifestFile = "manifest.json";
    private const string DeletedFile = "mail-geloescht.json";
    private static readonly string[] ConfigFolders = ["dkim", "acme", "queue"];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool Running => _gate.CurrentCount == 0;

    /// <summary>Why no backup can be made with the current settings, or null.</summary>
    public string? Problem(BackupOptions? settings = null)
    {
        settings ??= options.Value.Backup;
        if (string.IsNullOrWhiteSpace(settings.Directory))
        {
            return "Kein Zielordner angegeben.";
        }

        if (!Path.IsPathRooted(settings.Directory))
        {
            return "Der Zielordner muss ein vollständiger Pfad sein, z. B. D:\\Sicherung oder \\\\server\\freigabe.";
        }

        var target = Path.GetFullPath(settings.Directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var root = Path.GetFullPath(paths.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || root.StartsWith(target, StringComparison.OrdinalIgnoreCase))
        {
            return "Der Zielordner darf nicht im Datenordner liegen (und umgekehrt).";
        }

        return settings.KeepDays < 1 ? "Die Aufbewahrung muss mindestens einen Tag betragen." : null;
    }

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
        try
        {
            if (Problem(settings) is { } problem)
            {
                throw new BackupException(problem);
            }

            using var share = NetworkShare.Connect(settings);
            var target = settings.Directory!;
            Directory.CreateDirectory(target);
            snapshot = await Task.Run(() => CreateSnapshot(target, started, counters, cancellationToken), cancellationToken);
            await Task.Run(() => MirrorMail(target, settings.KeepDays, started, counters, cancellationToken), cancellationToken);
            var removed = RemoveOldSnapshots(target, settings.KeepDays, started);
            success = true;
            message = $"{counters.Files} Dateien ({Format(counters.Bytes)}) kopiert" + (removed > 0 ? $", {removed} alte Sicherung(en) entfernt" : "") + ".";
            logger.LogInformation("Backup {Snapshot} finished: {Message}", snapshot, message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BackupException or SqliteException or System.ComponentModel.Win32Exception)
        {
            message = ex.Message;
            logger.LogError(ex, "Backup failed");
        }
        catch (OperationCanceledException)
        {
            message = "Abgebrochen (Dienst wurde beendet).";
        }
        finally
        {
            _gate.Release();
        }

        var finished = timeProvider.GetUtcNow();
        using (var connection = database.Open())
        {
            connection.Execute(
                "UPDATE backup_runs SET finished_utc = $finished, success = $success, snapshot = $snapshot, files_copied = $files, bytes_copied = $bytes, message = $message WHERE id = $id",
                ("$finished", finished.ToDbTime()), ("$success", success ? 1 : 0), ("$snapshot", snapshot), ("$files", counters.Files),
                ("$bytes", counters.Bytes), ("$message", message), ("$id", id));
            connection.Execute("DELETE FROM backup_runs WHERE id NOT IN (SELECT id FROM backup_runs ORDER BY id DESC LIMIT 100)");
        }

        return new BackupRun(id, started, finished, success, snapshot, counters.Files, counters.Bytes, message);
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

    /// <summary>Complete snapshots in the target folder, newest first.</summary>
    public static IReadOnlyList<BackupSnapshot> ListSnapshots(string target)
    {
        var folder = Path.Combine(target, SnapshotsFolder);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return Directory.EnumerateDirectories(folder)
            .Select(path => (Path: path, Manifest: ReadManifest(path)))
            .Where(s => s.Manifest is not null)
            .Select(s => new BackupSnapshot(System.IO.Path.GetFileName(s.Path), s.Path, s.Manifest!.Created,
                new DirectoryInfo(s.Path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)))
            .OrderByDescending(s => s.Created)
            .ToList();
    }

    public static BackupManifest? ReadManifest(string snapshot)
    {
        var file = Path.Combine(snapshot, ManifestFile);
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(file)) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Puts a snapshot back into the data directory. The service must be stopped. The mails come from the mirror next to
    /// the snapshot folder; mails already in the data directory are kept.
    /// </summary>
    public static RestoreResult Restore(string snapshot, DataPaths target, Action<string>? progress = null)
    {
        if (ReadManifest(snapshot) is null)
        {
            throw new BackupException($"{snapshot} ist keine vollständige Sicherung ({ManifestFile} fehlt).");
        }

        var mirror = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(snapshot).TrimEnd(Path.DirectorySeparatorChar)))!, MailFolder);
        Directory.CreateDirectory(target.Root);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            File.Delete(target.DatabaseFile + suffix);
        }

        File.Copy(Path.Combine(snapshot, "mailserver.db"), target.DatabaseFile);
        if (File.Exists(Path.Combine(snapshot, "settings.json")))
        {
            File.Copy(Path.Combine(snapshot, "settings.json"), target.SettingsFile, overwrite: true);
        }

        foreach (var folder in ConfigFolders)
        {
            var source = Path.Combine(snapshot, folder);
            if (Directory.Exists(source))
            {
                CopyTree(source, Path.Combine(target.Root, folder), _ => true, null);
            }
        }

        progress?.Invoke("Datenbank und Einstellungen zurückgespielt, kopiere Mails …");
        var counters = new Counters();
        if (Directory.Exists(mirror))
        {
            CopyTree(mirror, target.MailRoot, _ => true, counters);
        }

        return new RestoreResult(counters.Files, counters.Bytes, Directory.Exists(mirror));
    }

    public sealed record RestoreResult(long MailFiles, long MailBytes, bool MirrorFound);

    private string CreateSnapshot(string target, DateTimeOffset started, Counters counters, CancellationToken cancellationToken)
    {
        var name = started.ToLocalTime().ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
        var folder = Path.Combine(target, SnapshotsFolder, name);
        var temp = folder + ".unvollstaendig";
        if (Directory.Exists(temp))
        {
            Directory.Delete(temp, recursive: true);
        }

        Directory.CreateDirectory(temp);

        // SQLite's online backup gives a consistent copy while the server keeps running.
        var databaseCopy = Path.Combine(temp, "mailserver.db");
        using (var source = database.Open())
        using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databaseCopy, Pooling = false }.ToString()))
        {
            destination.Open();
            source.BackupDatabase(destination);
        }

        counters.Add(new FileInfo(databaseCopy).Length);
        if (File.Exists(paths.SettingsFile))
        {
            File.Copy(paths.SettingsFile, Path.Combine(temp, "settings.json"));
            counters.Add(new FileInfo(paths.SettingsFile).Length);
        }

        var appSettings = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (File.Exists(appSettings))
        {
            File.Copy(appSettings, Path.Combine(temp, "appsettings.json"));
        }

        foreach (var sub in ConfigFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = Path.Combine(paths.Root, sub);
            if (Directory.Exists(source))
            {
                CopyTree(source, Path.Combine(temp, sub), _ => true, counters);
            }
        }

        using (var connection = database.Open())
        {
            var manifest = new BackupManifest(BuildInfo.Version, options.Value.Hostname, started,
                Convert.ToInt32(connection.Scalar("SELECT COUNT(*) FROM accounts")), Convert.ToInt64(connection.Scalar("SELECT COUNT(*) FROM messages")));
            File.WriteAllText(Path.Combine(temp, ManifestFile), JsonSerializer.Serialize(manifest, Json));
        }

        Directory.Move(temp, folder);
        return name;
    }

    private void MirrorMail(string target, int keepDays, DateTimeOffset now, Counters counters, CancellationToken cancellationToken)
    {
        var mirror = Path.Combine(target, MailFolder);
        Directory.CreateDirectory(mirror);
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(paths.MailRoot))
        {
            CopyTree(paths.MailRoot, mirror, relative =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                present.Add(relative);
                return true;
            }, counters);
        }

        // Files gone from the server are kept as long as a snapshot may still refer to them.
        var deletedFile = Path.Combine(target, DeletedFile);
        var deleted = File.Exists(deletedFile)
            ? JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(deletedFile)) ?? []
            : [];
        var kept = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(mirror, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(mirror, file);
            if (present.Contains(relative))
            {
                continue;
            }

            // Only snapshots made before the file was first missed can refer to it, and those are removed after keepDays.
            var since = deleted.TryGetValue(relative, out var value) ? value : now;
            if (now - since > TimeSpan.FromDays(keepDays))
            {
                File.Delete(file);
            }
            else
            {
                kept[relative] = since;
            }
        }

        File.WriteAllText(deletedFile, JsonSerializer.Serialize(kept, Json));
        foreach (var directory in Directory.EnumerateDirectories(mirror, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
    }

    private static int RemoveOldSnapshots(string target, int keepDays, DateTimeOffset now)
    {
        var removed = 0;
        var snapshots = ListSnapshots(target);
        foreach (var snapshot in snapshots.Skip(1).Where(s => now - s.Created > TimeSpan.FromDays(keepDays)))
        {
            Directory.Delete(snapshot.Path, recursive: true);
            removed++;
        }

        // Leftovers of runs that were interrupted.
        foreach (var incomplete in Directory.EnumerateDirectories(Path.Combine(target, SnapshotsFolder), "*.unvollstaendig"))
        {
            Directory.Delete(incomplete, recursive: true);
        }

        return removed;
    }

    /// <summary>Copies files that are missing or differ in size/time; returns nothing, counts what was copied.</summary>
    private static void CopyTree(string source, string destination, Func<string, bool> include, Counters? counters)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (!include(relative))
            {
                continue;
            }

            var info = new FileInfo(file);
            var copy = new FileInfo(Path.Combine(destination, relative));
            if (copy.Exists && copy.Length == info.Length && copy.LastWriteTimeUtc == info.LastWriteTimeUtc)
            {
                continue;
            }

            Directory.CreateDirectory(copy.DirectoryName!);
            try
            {
                File.Copy(file, copy.FullName, overwrite: true);
            }
            catch (FileNotFoundException)
            {
                continue; // deleted while copying, e.g. a delivered queue entry
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            File.SetLastWriteTimeUtc(copy.FullName, info.LastWriteTimeUtc);
            counters?.Add(info.Length);
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
        public long Files;
        public long Bytes;

        public void Add(long bytes)
        {
            Files++;
            Bytes += bytes;
        }
    }
}

public sealed class BackupException(string message) : Exception(message);
