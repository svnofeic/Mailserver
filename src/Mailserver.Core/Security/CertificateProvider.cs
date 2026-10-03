using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailserver.Core.Security;

/// <summary>
/// Supplies the TLS server certificate from a PFX file or the Windows certificate stores (My, Plesk's WebHosting) and reloads it periodically,
/// so renewed Let's Encrypt certificates (win-acme) are used without a restart.
/// </summary>
public sealed class CertificateProvider(IOptions<MailserverOptions> options, TimeProvider timeProvider, ILogger<CertificateProvider> logger)
{
    private readonly Lock _lock = new();
    private X509Certificate2? _current;
    private DateTimeOffset _nextReload = DateTimeOffset.MinValue;

    public X509Certificate2? GetCertificate()
    {
        lock (_lock)
        {
            var now = timeProvider.GetUtcNow();
            if (now < _nextReload)
            {
                return _current;
            }

            _nextReload = now + options.Value.Tls.ReloadInterval;
            try
            {
                var loaded = Load();
                if (loaded is not null && loaded.Thumbprint != _current?.Thumbprint)
                {
                    logger.LogInformation("Using TLS certificate {Subject} (thumbprint {Thumbprint}, valid until {NotAfter:u})",
                        loaded.Subject, loaded.Thumbprint, loaded.NotAfter.ToUniversalTime());
                    _current = loaded;
                }
            }
            catch (Exception ex)
            {
                // Keep serving the previous certificate rather than dropping TLS.
                logger.LogError(ex, "Loading the TLS certificate failed");
            }

            if (_current is null)
            {
                logger.LogWarning("No TLS certificate available for {Hostname}. Run \"mailadmin tls\" to see why.",
                    options.Value.Tls.StoreSubject ?? options.Value.Hostname);
            }
            else if (_current.NotAfter.ToUniversalTime() < now.UtcDateTime.AddDays(14))
            {
                logger.LogWarning("TLS certificate expires on {NotAfter:u}", _current.NotAfter.ToUniversalTime());
            }

            return _current;
        }
    }

    private X509Certificate2? Load()
    {
        var tls = options.Value.Tls;
        if (!string.IsNullOrEmpty(tls.PfxPath))
        {
            var path = Path.IsPathRooted(tls.PfxPath) ? tls.PfxPath : Path.Combine(AppContext.BaseDirectory, tls.PfxPath);
            return X509CertificateLoader.LoadPkcs12FromFile(path, tls.PfxPassword);
        }

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var hostname = tls.StoreSubject ?? options.Value.Hostname;
        return Inspect(hostname)
            .Where(c => c.Problem is null)
            .OrderByDescending(c => c.Certificate.NotAfter)
            .Select(c => c.Certificate)
            .FirstOrDefault();
    }

    /// <summary>
    /// All certificates in LocalMachine\My and LocalMachine\WebHosting (Windows) with the reason why each one can or cannot
    /// be used for <paramref name="hostname"/>. Plesk keeps its Let's Encrypt certificates in "WebHosting"; win-acme and
    /// manual imports use "My". Names are matched against the subject alternative names (incl. wildcards), because
    /// Plesk certificates usually carry the domain as subject and mail.&lt;domain&gt; only as an additional name.
    /// </summary>
    public static IReadOnlyList<CertificateCandidate> Inspect(string hostname)
    {
        var result = new List<CertificateCandidate>();
        if (!OperatingSystem.IsWindows())
        {
            return result;
        }

        var now = DateTime.Now;
        foreach (var storeName in new[] { "My", "WebHosting" })
        {
            var store = new X509Store(storeName, StoreLocation.LocalMachine);
            try
            {
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                store.Dispose();
                continue; // store does not exist on this machine
            }

            using (store)
            {
                foreach (var certificate in store.Certificates)
                {
                    result.Add(new CertificateCandidate(storeName, certificate, Names(certificate), Check(certificate, hostname, now)));
                }
            }
        }

        return result;
    }

    private static string? Check(X509Certificate2 certificate, string hostname, DateTime now)
    {
        if (!certificate.MatchesHostname(hostname))
        {
            return $"gilt nicht für {hostname}";
        }

        if (certificate.NotAfter <= now)
        {
            return $"abgelaufen am {certificate.NotAfter:dd.MM.yyyy}";
        }

        if (certificate.NotBefore > now)
        {
            return $"erst gültig ab {certificate.NotBefore:dd.MM.yyyy}";
        }

        if (!certificate.HasPrivateKey)
        {
            return "kein privater Schlüssel vorhanden";
        }

        try
        {
            using var key = (System.Security.Cryptography.AsymmetricAlgorithm?)certificate.GetRSAPrivateKey() ?? certificate.GetECDsaPrivateKey();
            return key is null ? "Schlüsseltyp wird nicht unterstützt" : null;
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            return $"privater Schlüssel nicht lesbar ({ex.Message.Trim()})";
        }
    }

    private static IReadOnlyList<string> Names(X509Certificate2 certificate)
    {
        var names = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().SelectMany(e => e.EnumerateDnsNames()).ToList();
        var commonName = certificate.GetNameInfo(X509NameType.DnsName, forIssuer: false);
        if (!string.IsNullOrEmpty(commonName) && !names.Contains(commonName, StringComparer.OrdinalIgnoreCase))
        {
            names.Insert(0, commonName);
        }

        return names;
    }
}

/// <param name="Problem">Why the certificate cannot be used, or null if it can.</param>
public sealed record CertificateCandidate(string Store, X509Certificate2 Certificate, IReadOnlyList<string> Names, string? Problem);
