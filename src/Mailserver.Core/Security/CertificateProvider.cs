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
                logger.LogWarning("No TLS certificate available");
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

        // Plesk keeps its Let's Encrypt certificates in "WebHosting"; win-acme and manual imports use "My".
        // Matching uses the subject alternative names (incl. wildcards), because Plesk certificates usually carry
        // the domain as subject and mail.<domain> only as an additional name.
        var hostname = tls.StoreSubject ?? options.Value.Hostname;
        var now = DateTime.Now;
        X509Certificate2? best = null;
        foreach (var storeName in new[] { "My", "WebHosting" })
        {
            using var store = new X509Store(storeName, StoreLocation.LocalMachine);
            try
            {
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                continue; // store does not exist on this machine
            }

            foreach (var candidate in store.Certificates)
            {
                if (candidate.HasPrivateKey && candidate.NotBefore <= now && candidate.NotAfter > now &&
                    candidate.MatchesHostname(hostname) && (best is null || candidate.NotAfter > best.NotAfter))
                {
                    best = candidate;
                }
            }
        }

        return best;
    }
}
