using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Mailserver.Core.Backup;

/// <summary>
/// Logs on to a network share with its own credentials for the duration of a backup. The Windows service runs as
/// LocalSystem, which has no access to shares that require a user name (e.g. a storage box of the hosting provider).
/// </summary>
internal sealed class NetworkShare : IDisposable
{
    private readonly string _share;

    private NetworkShare(string share) => _share = share;

    /// <summary>Connects when credentials are configured for a UNC path; otherwise returns null (nothing to do).</summary>
    public static NetworkShare? Connect(BackupOptions settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Username) || settings.Directory is not { } directory || !directory.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new BackupException("Anmeldung an Netzwerkfreigaben ist nur unter Windows möglich.");
        }

        // \\server\freigabe\unterordner → \\server\freigabe
        var parts = directory.TrimStart('\\').Split('\\', 3);
        if (parts.Length < 2)
        {
            throw new BackupException($"{directory} ist keine Netzwerkfreigabe der Form \\\\server\\freigabe.");
        }

        var share = $@"\\{parts[0]}\{parts[1]}";
        var resource = new NetResource { Type = 1 /* RESOURCETYPE_DISK */, RemoteName = share };
        var result = WNetAddConnection2(ref resource, settings.Password, settings.Username, 0);
        if (result == 1219) // ERROR_SESSION_CREDENTIAL_CONFLICT: already connected, e.g. by the previous run
        {
            return null;
        }

        if (result != 0)
        {
            throw new BackupException($"Anmeldung an {share} als {settings.Username} fehlgeschlagen: {new Win32Exception(result).Message}");
        }

        return new NetworkShare(share);
    }

    public void Dispose() => _ = WNetCancelConnection2(_share, 0, true);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }

#pragma warning disable SYSLIB1054 // the struct with strings needs the classic marshaller
    [DllImport("mpr.dll", EntryPoint = "WNetAddConnection2W", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NetResource resource, string? password, string? username, int flags);

    [DllImport("mpr.dll", EntryPoint = "WNetCancelConnection2W", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, int flags, bool force);
#pragma warning restore SYSLIB1054
}
