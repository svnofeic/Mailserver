using System.Security.Cryptography;
using System.Text.Json;

namespace Mailserver.Core.Backup;

/// <summary>
/// Access to the cloud storages (OneDrive refresh token, pCloud auth token) in data\cloud. On Windows the files are
/// encrypted for this machine (DPAPI). They are deliberately not part of the backup: after a reinstallation the
/// connection is made again (Admin → Datensicherung or mailadmin backup connect).
/// </summary>
public sealed class CloudTokens(DataPaths paths)
{
    private static readonly byte[] Entropy = "Mailserver.CloudTokens"u8.ToArray();
    private readonly Lock _lock = new();

    public string Folder => Path.Combine(paths.Root, "cloud");

    public T? Load<T>(string provider) where T : class
    {
        lock (_lock)
        {
            var file = FileFor(provider);
            if (!File.Exists(file))
            {
                return null;
            }

            try
            {
                var bytes = File.ReadAllBytes(file);
                if (OperatingSystem.IsWindows())
                {
                    bytes = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.LocalMachine);
                }

                return JsonSerializer.Deserialize<T>(bytes);
            }
            catch (Exception ex) when (ex is JsonException or CryptographicException)
            {
                return null; // e.g. copied from another machine: connect again
            }
        }
    }

    public void Save<T>(string provider, T value)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Folder);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            if (OperatingSystem.IsWindows())
            {
                bytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.LocalMachine);
            }

            var file = FileFor(provider);
            File.WriteAllBytes(file + ".tmp", bytes);
            File.Move(file + ".tmp", file, overwrite: true);
        }
    }

    public void Delete(string provider)
    {
        lock (_lock)
        {
            File.Delete(FileFor(provider));
        }
    }

    private string FileFor(string provider) => Path.Combine(Folder, provider.ToLowerInvariant() + (OperatingSystem.IsWindows() ? ".bin" : ".json"));
}
