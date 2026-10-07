using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Mailserver.Core;
using Mailserver.Core.Security;
using Mailserver.Core.Security.Acme;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mailserver.Tests;

/// <summary>Parts of the Let's Encrypt support that work without an ACME server (the full flow is in <see cref="AcmePebbleTests"/>).</summary>
public sealed class AcmeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mailserver-acme-unit-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Challenge_server_answers_only_known_tokens()
    {
        var store = new AcmeChallengeStore();
        store.Add("token-1234567890abcdef", "token-1234567890abcdef.thumbprint");
        var port = FreePort();
        await using var server = AcmeHttpChallengeServer.Start(store, port);
        using var http = new HttpClient();

        Assert.Equal("token-1234567890abcdef.thumbprint",
            await http.GetStringAsync($"http://127.0.0.1:{port}/.well-known/acme-challenge/token-1234567890abcdef"));
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"http://127.0.0.1:{port}/.well-known/acme-challenge/unbekannt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"http://127.0.0.1:{port}/")).StatusCode);
    }

    [Fact]
    public async Task Self_check_finds_another_web_server_on_port_80()
    {
        const string token = "token-1234567890abcdef";
        // Stand-in for IIS: answers every request with 404 and its own Server header.
        var iis = new TcpListener(IPAddress.Loopback, 0);
        iis.Start();
        var port = ((IPEndPoint)iis.LocalEndpoint).Port;
        var answering = Task.Run(async () =>
        {
            using var client = await iis.AcceptTcpClientAsync();
            var stream = client.GetStream();
            _ = await stream.ReadAsync(new byte[4096]); // the request line is enough
            await stream.WriteAsync("HTTP/1.1 404 Not Found\r\nServer: Microsoft-IIS/10.0\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
        });

        var ex = await Assert.ThrowsAsync<AcmeException>(() =>
            AcmeCertificateManager.SelfCheckAsync("localhost", port, token, token + ".thumbprint", null, CancellationToken.None));
        Assert.Contains("Microsoft-IIS/10.0 (404)", ex.Message);
        Assert.Contains("Challenge-Ordner", ex.Message);
        await answering;
        iis.Stop();

        // The mail server itself answering: fine. Nobody answering: Let's Encrypt decides.
        var store = new AcmeChallengeStore();
        store.Add(token, token + ".thumbprint");
        await using (AcmeHttpChallengeServer.Start(store, port))
        {
            await AcmeCertificateManager.SelfCheckAsync("localhost", port, token, token + ".thumbprint", null, CancellationToken.None);
        }

        await AcmeCertificateManager.SelfCheckAsync("localhost", port, token, token + ".thumbprint", null, CancellationToken.None);
    }

    [Fact]
    public void Busy_port_gives_a_helpful_message()
    {
        var blocker = new TcpListener(IPAddress.Any, 0);
        blocker.Start();
        try
        {
            var ex = Assert.Throws<AcmeException>(() => AcmeHttpChallengeServer.Start(new AcmeChallengeStore(), ((IPEndPoint)blocker.LocalEndpoint).Port));
            Assert.Contains("Challenge-Ordner", ex.Message);
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public void Challenge_directory_writes_files_for_iis()
    {
        var folder = new AcmeChallengeDirectory(_directory);
        folder.Write("token-abcdefghijklmnop", "antwort");

        var challengeFolder = Path.Combine(_directory, ".well-known", "acme-challenge");
        Assert.Equal("antwort", File.ReadAllText(Path.Combine(challengeFolder, "token-abcdefghijklmnop")));
        Assert.Contains("mimeMap fileExtension=\".\"", File.ReadAllText(Path.Combine(challengeFolder, "web.config")));
        folder.Delete("token-abcdefghijklmnop");
        Assert.False(File.Exists(Path.Combine(challengeFolder, "token-abcdefghijklmnop")));
    }

    [Fact]
    public void Key_authorization_uses_the_rfc_7638_thumbprint()
    {
        // Example key from RFC 7638 is RSA; for EC the canonical form is {"crv","kty","x","y"} — checked against a manual computation.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var client = new AcmeClient(new Uri("https://acme.invalid/dir"), key);
        var p = key.ExportParameters(false);
        var jwk = $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{AcmeClient.Base64Url(p.Q.X!)}\",\"y\":\"{AcmeClient.Base64Url(p.Q.Y!)}\"}}";
        var expected = "tok." + AcmeClient.Base64Url(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(jwk)));

        Assert.Equal(expected, client.KeyAuthorization("tok"));
        Assert.DoesNotContain("=", expected);
    }

    [Fact]
    public void Provider_uses_the_issued_certificate_and_falls_back_for_the_web()
    {
        var options = new MailserverOptions { Hostname = "mail.example.test", DataDirectory = _directory };
        var paths = new DataPaths(_directory);
        var manager = new AcmeCertificateManager(Microsoft.Extensions.Options.Options.Create(options), paths, new AcmeChallengeStore(),
            TimeProvider.System, NullLogger<AcmeCertificateManager>.Instance);
        var provider = new CertificateProvider(Microsoft.Extensions.Options.Options.Create(options), TimeProvider.System,
            NullLogger<CertificateProvider>.Instance, manager);

        // Nothing yet: SMTP/IMAP get none, the web interface a self-signed stand-in.
        Assert.Null(provider.GetCertificate());
        var fallback = provider.GetWebCertificate();
        Assert.True(fallback.HasPrivateKey);
        Assert.True(fallback.MatchesHostname("mail.example.test"));

        Directory.CreateDirectory(paths.AcmeRoot);
        File.WriteAllBytes(manager.CertificateFile, CreatePfx("mail.example.test"));
        provider.Invalidate();
        var issued = provider.GetCertificate();
        Assert.NotNull(issued);
        Assert.True(issued.MatchesHostname("mail.example.test"));
        Assert.Same(issued, provider.GetWebCertificate());

        provider.ReportTlsUnavailable();
        Assert.True(provider.RestartRecommended);
    }

    [Fact]
    public void Renewal_is_due_for_new_names_and_before_expiry()
    {
        var options = new MailserverOptions { Hostname = "mail.example.test" };
        options.Tls.Acme = new AcmeOptions { Enabled = true, Hostnames = "mail.example.test" };
        var paths = new DataPaths(_directory);
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var manager = new AcmeCertificateManager(Microsoft.Extensions.Options.Options.Create(options), paths, new AcmeChallengeStore(), time,
            NullLogger<AcmeCertificateManager>.Instance);
        Assert.Equal("noch kein Zertifikat", manager.RenewalReason());

        Directory.CreateDirectory(paths.AcmeRoot);
        File.WriteAllBytes(manager.CertificateFile, CreatePfx("mail.example.test", days: 90));
        Assert.Null(manager.RenewalReason());

        options.Tls.Acme.Hostnames = "mail.example.test, webmail.example.test";
        Assert.Equal("Hostnamen geändert", manager.RenewalReason());

        options.Tls.Acme.Hostnames = null;
        time.Now = time.Now.AddDays(70);
        Assert.StartsWith("läuft in", manager.RenewalReason());

        options.Tls.Acme.Enabled = false;
        Assert.Null(manager.RenewalReason());
    }

    [Theory]
    [InlineData(null, "mail.example.test")]
    [InlineData("Mail.Example.test, webmail.example.test.", "mail.example.test|webmail.example.test")]
    [InlineData("a.example.test\nb.example.test a.example.test", "a.example.test|b.example.test")]
    public void Parses_hostnames(string? configured, string expected) =>
        Assert.Equal(expected.Split('|'), new AcmeOptions { Hostnames = configured }.EffectiveHostnames("mail.example.test"));

    [Fact]
    public async Task Invalid_hostnames_are_rejected_before_contacting_the_server()
    {
        var options = new MailserverOptions { Hostname = "mail.example.test" };
        options.Tls.Acme = new AcmeOptions { Enabled = true, Hostnames = "*.example.test", DirectoryUrl = "https://acme.invalid/dir" };
        var manager = new AcmeCertificateManager(Microsoft.Extensions.Options.Options.Create(options), new DataPaths(_directory),
            new AcmeChallengeStore(), TimeProvider.System, NullLogger<AcmeCertificateManager>.Instance);

        var status = await manager.IssueAsync();

        Assert.False(status.Success);
        Assert.Contains("kein gültiger Hostname", status.Message);
        Assert.Equal(status.Message, manager.Status.Message);
    }

    private static byte[] CreatePfx(string hostname, int days = 90)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={hostname}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(hostname);
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(days));
        return certificate.Export(X509ContentType.Pkcs12);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

public sealed class AcmeWebTests : IAsyncLifetime
{
    private TestServer _server = null!;
    private WebClient _web = null!;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync();
        var admin = _server.HostAccounts.AddAccount(EmailAddress.Parse("chef@example.test"), TestServer.Password);
        _server.HostAccounts.SetAdmin(admin.Address, true);
        _web = new WebClient(_server.WebPort);
        await _web.LoginAsync("chef@example.test", TestServer.Password);
    }

    public async Task DisposeAsync()
    {
        _web.Dispose();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task Shows_the_certificate_and_saves_lets_encrypt_settings()
    {
        await _web.GetAsync("/Admin/Certificate");
        Assert.Contains("Verwendetes Zertifikat", _web.LastPage);
        Assert.Contains("mail.example.test", _web.LastPage);

        await _web.PostAsync("/Admin/Certificate", "/Admin/Certificate?handler=Save", ("Form.Enabled", "true"),
            ("Form.Email", "admin@example.test"), ("Form.Hostnames", "mail.example.test\nwebmail.example.test"));

        Assert.Contains("Gespeichert.", _web.LastPage);
        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(_server.DataDirectory, "settings.json")))!["Mailserver"]!["Tls"]!["Acme"]!;
        Assert.True(saved["Enabled"]!.GetValue<bool>());
        Assert.Equal("mail.example.test, webmail.example.test", saved["Hostnames"]!.GetValue<string>());
    }

    [Fact]
    public async Task Requires_an_email_address()
    {
        await _web.PostAsync("/Admin/Certificate", "/Admin/Certificate?handler=Save", ("Form.Enabled", "true"), ("Form.Email", ""));

        Assert.Contains("Bitte eine E-Mail-Adresse angeben", _web.LastPage);
        Assert.False(File.Exists(Path.Combine(_server.DataDirectory, "settings.json")) &&
                     File.ReadAllText(Path.Combine(_server.DataDirectory, "settings.json")).Contains("Acme"));
    }

    [Fact]
    public async Task Web_interface_answers_pending_challenges()
    {
        _server.Services.GetRequiredService<AcmeChallengeStore>().Add("token-abcdefghijklmnop", "antwort");

        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
        Assert.Equal("antwort", await http.GetStringAsync($"https://127.0.0.1:{_server.WebPort}/.well-known/acme-challenge/token-abcdefghijklmnop"));
        Assert.Equal(HttpStatusCode.NotFound,
            (await http.GetAsync($"https://127.0.0.1:{_server.WebPort}/.well-known/acme-challenge/anderes-token-123")).StatusCode);
    }
}
