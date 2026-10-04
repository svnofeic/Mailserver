using Mailserver.Core;
using Mailserver.Core.Security.Acme;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mailserver.Tests;

/// <summary>
/// End-to-end issuance against Pebble, the ACME test server of Let's Encrypt. Runs only when PEBBLE_DIRECTORY is set, e.g.
/// https://127.0.0.1:14000/dir with Pebble's httpPort 5002 and the test names resolving to 127.0.0.1 (/etc/hosts).
/// </summary>
public sealed class AcmePebbleTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mailserver-acme-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Issues_and_renews_a_certificate()
    {
        var pebble = Environment.GetEnvironmentVariable("PEBBLE_DIRECTORY");
        if (string.IsNullOrEmpty(pebble))
        {
            return; // Pebble not available in this environment.
        }

        var options = new MailserverOptions { Hostname = "mail.example.test" };
        options.Tls.Acme = new AcmeOptions
        {
            Enabled = true, Email = "admin@example.test", Hostnames = "mail.example.test, webmail.example.test",
            DirectoryUrl = pebble, HttpPort = 5002,
        };
        var manager = new AcmeCertificateManager(Microsoft.Extensions.Options.Options.Create(options), new DataPaths(_directory),
            new AcmeChallengeStore(), TimeProvider.System, NullLogger<AcmeCertificateManager>.Instance)
        {
            Handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true },
        };
        Assert.Equal("noch kein Zertifikat", manager.RenewalReason());

        var steps = new List<string>();
        var status = await manager.IssueAsync(steps.Add);

        Assert.True(status.Success, status.Message);
        using var certificate = manager.LoadCertificate()!;
        Assert.True(certificate.HasPrivateKey);
        Assert.True(certificate.MatchesHostname("mail.example.test"));
        Assert.True(certificate.MatchesHostname("webmail.example.test"));
        Assert.Null(manager.RenewalReason());
        Assert.Contains(steps, s => s.StartsWith("Konto", StringComparison.Ordinal));

        // A second run reuses the account and replaces the certificate.
        options.Tls.Acme.Hostnames = "mail.example.test";
        var second = await manager.IssueAsync(steps.Add);
        Assert.True(second.Success, second.Message);
        Assert.Single(steps, s => s.StartsWith("Konto", StringComparison.Ordinal));
    }
}
