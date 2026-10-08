using System.Security.Cryptography;
using System.Text;

namespace Mailserver.Core.Security;

/// <summary>
/// Encrypts passwords the server has to store in readable form (e.g. for external mail accounts) with AES-GCM. The key is
/// in data\secrets and is part of the backup, so a restored server can still use the passwords; a copy of the database
/// alone does not reveal them.
/// </summary>
public sealed class SecretProtector(DataPaths paths)
{
    private const string Prefix = "v1:";
    private readonly Lock _lock = new();
    private byte[]? _key;

    public string Folder => Path.Combine(paths.Root, "secrets");

    public string Protect(string plain)
    {
        var data = Encoding.UTF8.GetBytes(plain);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[data.Length];
        using (var aes = new AesGcm(Key(), tag.Length))
        {
            aes.Encrypt(nonce, data, cipher, tag);
        }

        return Prefix + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    /// <summary>The plain text, or null if the value cannot be decrypted (e.g. the key file was lost).</summary>
    public string? Unprotect(string stored)
    {
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(stored[Prefix.Length..]);
            if (bytes.Length < 28)
            {
                return null;
            }

            var plain = new byte[bytes.Length - 28];
            using var aes = new AesGcm(Key(), 16);
            aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }

    private byte[] Key()
    {
        lock (_lock)
        {
            if (_key is not null)
            {
                return _key;
            }

            var file = Path.Combine(Folder, "key.bin");
            if (File.Exists(file) && File.ReadAllBytes(file) is { Length: 32 } existing)
            {
                return _key = existing;
            }

            Directory.CreateDirectory(Folder);
            var key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(file + ".tmp", key);
            File.Move(file + ".tmp", file, overwrite: true);
            return _key = key;
        }
    }
}
