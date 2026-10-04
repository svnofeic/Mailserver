namespace Mailserver.Core;

/// <summary>
/// Resolves the on-disk layout below the data directory.
/// </summary>
public sealed class DataPaths
{
    public DataPaths(string dataDirectory)
    {
        // A Windows service starts in C:\Windows\System32, so relative paths are anchored to the application directory.
        Root = Path.IsPathRooted(dataDirectory)
            ? dataDirectory
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, dataDirectory));
    }

    public string Root { get; }

    public string DatabaseFile => Path.Combine(Root, "mailserver.db");

    public string MailRoot => Path.Combine(Root, "mail");

    public string QueueRoot => Path.Combine(Root, "queue");

    public string DkimRoot => Path.Combine(Root, "dkim");

    /// <summary>Let's Encrypt account key, issued certificate and status.</summary>
    public string AcmeRoot => Path.Combine(Root, "acme");

    /// <summary>Settings changed in the web interface; overrides appsettings.json and is reloaded while running.</summary>
    public string SettingsFile => Path.Combine(Root, "settings.json");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(MailRoot);
        Directory.CreateDirectory(QueueRoot);
        Directory.CreateDirectory(DkimRoot);
    }
}
