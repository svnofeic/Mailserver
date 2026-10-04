namespace Mailserver.Core.Backup;

/// <summary>Backup into a folder: another disk, a USB drive or a network share (optionally with its own credentials).</summary>
public sealed class FolderBackupStore : IBackupStore
{
    private readonly string _root;
    private readonly NetworkShare? _share;

    public FolderBackupStore(string root, BackupOptions? settings = null)
    {
        _root = Path.GetFullPath(root);
        _share = settings is null ? null : NetworkShare.Connect(settings);
        Directory.CreateDirectory(_root);
    }

    public string Description => _root;

    public string Key => "folder:" + _root.TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();

    public Task<IReadOnlyList<string>> ListFoldersAsync(string folder, CancellationToken cancellationToken)
    {
        var path = Full(folder);
        IReadOnlyList<string> result = Directory.Exists(path) ? Directory.EnumerateDirectories(path).Select(d => Path.GetFileName(d)).ToList() : [];
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<StoredFile>> ListFilesAsync(string folder, CancellationToken cancellationToken)
    {
        var path = Full(folder);
        IReadOnlyList<StoredFile> result = Directory.Exists(path)
            ? new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories)
                .Select(f => new StoredFile(Path.GetRelativePath(path, f.FullName).Replace(Path.DirectorySeparatorChar, '/'), f.Length))
                .ToList()
            : [];
        return Task.FromResult(result);
    }

    public Task UploadAsync(string localFile, string path, CancellationToken cancellationToken)
    {
        var target = Full(path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(localFile, target, overwrite: true);
        return Task.CompletedTask;
    }

    public Task DownloadAsync(string path, string localFile, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(localFile)!);
        File.Copy(Full(path), localFile, overwrite: true);
        return Task.CompletedTask;
    }

    public Task<string?> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        var file = Full(path);
        return Task.FromResult(File.Exists(file) ? File.ReadAllText(file) : null);
    }

    public Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        var full = Full(path);
        if (Directory.Exists(full))
        {
            Directory.Delete(full, recursive: true);
        }
        else if (File.Exists(full))
        {
            File.Delete(full);
        }

        // Empty folders left behind (e.g. a month without mails any more) are removed as well.
        for (var parent = Path.GetDirectoryName(full); parent is not null && parent.Length > _root.Length; parent = Path.GetDirectoryName(parent))
        {
            if (!Directory.Exists(parent) || Directory.EnumerateFileSystemEntries(parent).Any())
            {
                break;
            }

            Directory.Delete(parent);
        }

        return Task.CompletedTask;
    }

    public void Dispose() => _share?.Dispose();

    private string Full(string path)
    {
        var full = Path.GetFullPath(Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(_root, StringComparison.OrdinalIgnoreCase) ? full : throw new ArgumentException($"Pfad außerhalb der Sicherung: {path}");
    }
}
