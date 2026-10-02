using System.Security.Cryptography;

namespace Mailserver.Core.Dkim;

/// <summary>
/// DKIM private keys as PEM files: data/dkim/{domain}.{selector}.pem.
/// </summary>
public sealed class DkimKeyStore(DataPaths paths)
{
    public string GetKeyPath(string domain, string selector) =>
        Path.Combine(paths.DkimRoot, $"{EmailAddress.NormalizeDomain(domain)}.{selector}.pem");

    public bool HasKey(string domain, string selector) => File.Exists(GetKeyPath(domain, selector));

    /// <summary>Creates a 2048-bit RSA key. Existing keys are never overwritten.</summary>
    public void GenerateKey(string domain, string selector)
    {
        var path = GetKeyPath(domain, selector);
        if (File.Exists(path))
        {
            throw new InvalidOperationException($"DKIM key already exists: {path}");
        }

        Directory.CreateDirectory(paths.DkimRoot);
        using var rsa = RSA.Create(2048);
        File.WriteAllText(path, rsa.ExportPkcs8PrivateKeyPem());
    }

    /// <summary>The value of the TXT record at {selector}._domainkey.{domain}.</summary>
    public string GetDnsRecord(string domain, string selector)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(GetKeyPath(domain, selector)));
        return $"v=DKIM1; k=rsa; p={Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo())}";
    }
}
