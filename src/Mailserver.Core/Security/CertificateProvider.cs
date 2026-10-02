using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailserver.Core.Security;

/// <summary>
/// Supplies the TLS server certificate from a PFX file or the Windows certificate store and reloads it periodically,
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

        var subject = tls.StoreSubject ?? options.Value.Hostname;
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var now = DateTime.Now;
        return store.Certificates
            .Find(X509FindType.FindBySubjectName, subject, validOnly: false)
            .Where(c => c.HasPrivateKey && c.NotBefore <= now && c.NotAfter > now)
            .OrderByDescending(c => c.NotAfter)
            .FirstOrDefault();
    }
}
