namespace Mailserver.Core.Backup;

/// <summary>Opens the backup store selected in the settings and explains why one cannot be used.</summary>
public sealed class BackupStores(DataPaths paths, CloudTokens tokens)
{
    public CloudTokens Tokens => tokens;

    /// <summary>Why no backup can be made with these settings, or null.</summary>
    public string? Problem(BackupOptions settings)
    {
        if (settings.KeepDays < 1)
        {
            return "Die Aufbewahrung muss mindestens einen Tag betragen.";
        }

        if (settings.IsCloud)
        {
            if (string.IsNullOrWhiteSpace(settings.RemoteFolder) || settings.RemoteFolder.Split('/', '\\').Any(p => p is "." or ".."))
            {
                return "Bitte einen Ordner in der Cloud angeben, z. B. Mailserver-Sicherung.";
            }

            return settings.Target switch
            {
                _ when Is(settings, BackupOptions.OneDriveTarget) =>
                    tokens.Load<OneDriveToken>(OneDriveStore.Provider) is null ? "Noch nicht mit OneDrive verbunden." : null,
                _ when Is(settings, BackupOptions.PCloudTarget) =>
                    tokens.Load<PCloudToken>(PCloudStore.Provider) is null ? "Noch nicht mit pCloud verbunden." : null,
                _ => $"Unbekanntes Sicherungsziel „{settings.Target}“.",
            };
        }

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
        return target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || root.StartsWith(target, StringComparison.OrdinalIgnoreCase)
            ? "Der Zielordner darf nicht im Datenordner liegen (und umgekehrt)."
            : null;
    }

    /// <exception cref="BackupException">Not usable (see <see cref="Problem"/>).</exception>
    public IBackupStore Open(BackupOptions settings)
    {
        if (Problem(settings) is { } problem)
        {
            throw new BackupException(problem);
        }

        if (Is(settings, BackupOptions.OneDriveTarget))
        {
            return new OneDriveStore(settings, tokens);
        }

        if (Is(settings, BackupOptions.PCloudTarget))
        {
            return new PCloudStore(settings, tokens);
        }

        return new FolderBackupStore(settings.Directory!, settings);
    }

    /// <summary>Short description of the target for messages, without connecting.</summary>
    public string Describe(BackupOptions settings) =>
        Is(settings, BackupOptions.OneDriveTarget) ? $"OneDrive: /{settings.RemoteFolder.Trim('/')}"
        : Is(settings, BackupOptions.PCloudTarget) ? $"pCloud: /{settings.RemoteFolder.Trim('/')}"
        : settings.Directory ?? "";

    private static bool Is(BackupOptions settings, string target) => string.Equals(settings.Target, target, StringComparison.OrdinalIgnoreCase);
}
