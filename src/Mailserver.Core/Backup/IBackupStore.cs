namespace Mailserver.Core.Backup;

/// <summary>A file in a backup store; the path is relative to the listed folder and uses "/".</summary>
public sealed record StoredFile(string Path, long Size);

/// <summary>
/// Where backups go: a folder (disk, USB drive, network share) or a cloud storage (OneDrive, pCloud). Paths are relative
/// to the store's root and use "/".
/// </summary>
public interface IBackupStore : IDisposable
{
    /// <summary>Shown to the user, e.g. "D:\Sicherung" or "OneDrive: /Mailserver-Sicherung".</summary>
    string Description { get; }

    /// <summary>Identifies the target for the list of uploaded mail files; changes when the target changes.</summary>
    string Key { get; }

    /// <summary>Names of the direct subfolders; empty if the folder does not exist.</summary>
    Task<IReadOnlyList<string>> ListFoldersAsync(string folder, CancellationToken cancellationToken);

    /// <summary>All files below the folder (recursive), paths relative to it; empty if the folder does not exist.</summary>
    Task<IReadOnlyList<StoredFile>> ListFilesAsync(string folder, CancellationToken cancellationToken);

    /// <summary>Uploads a file, replacing an existing one and creating missing folders.</summary>
    Task UploadAsync(string localFile, string path, CancellationToken cancellationToken);

    Task DownloadAsync(string path, string localFile, CancellationToken cancellationToken);

    /// <summary>The content of a small text file, or null if it does not exist.</summary>
    Task<string?> ReadTextAsync(string path, CancellationToken cancellationToken);

    /// <summary>Deletes a file or a folder with everything in it; a missing path is no error.</summary>
    Task DeleteAsync(string path, CancellationToken cancellationToken);
}
