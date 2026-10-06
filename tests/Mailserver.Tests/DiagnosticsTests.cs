using Mailserver.AntiSpam.Diagnostics;
using Mailserver.AntiSpam.Dns;
using Mailserver.Core.Dkim;
using Microsoft.Extensions.DependencyInjection;

namespace Mailserver.Tests;

public sealed class DiagnosticsTests : IAsyncLifetime
{
    private const string Ip = "203.0.113.10";
    private readonly FakeDns _dns = new();
    private TestServer _server = null!;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync(new Dictionary<string, string?> { ["Mailserver:Delivery:SmartHost:Host"] = "" },
            services => services.AddSingleton<IDnsResolver>(_dns));
        _dns.AddA("mail.example.test", Ip);
        _dns.Ptr[Ip] = "mail.example.test";
        _dns.Mx["example.test"] = [new MxRecord(10, "mail.example.test.")];
        _dns.AddTxt("example.test", $"v=spf1 ip4:{Ip} -all");
        _dns.AddTxt("test._domainkey.example.test", _server.Services.GetRequiredService<DkimKeyStore>().GetDnsRecord("example.test", "test"));
        _dns.AddTxt("_dmarc.example.test", "v=DMARC1; p=reject");

        // No real network: port 25 to Gmail is "blocked", everything else answers.
        Diagnostics.Connect = (host, port, _) => Task.FromResult(port == 25 && host.Contains("google") ? "Zeitüberschreitung" : null);
        Diagnostics.LatestRelease = _ => Task.FromResult<string?>(null);
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private ServerDiagnostics Diagnostics => _server.Services.GetRequiredService<ServerDiagnostics>();

    private static DiagnosticCheck Find(IReadOnlyList<DiagnosticCheck> checks, string title) =>
        checks.Single(c => c.Title.StartsWith(title, StringComparison.Ordinal));

    [Fact]
    public async Task Correct_dns_passes()
    {
        var checks = await Diagnostics.RunAsync();

        Assert.Equal(CheckStatus.Ok, Find(checks, "MX").Status);
        Assert.Equal(CheckStatus.Ok, Find(checks, "SPF").Status);
        Assert.Equal(CheckStatus.Ok, Find(checks, "DKIM").Status);
        Assert.Equal(CheckStatus.Ok, Find(checks, "DMARC").Status);
        Assert.Equal(CheckStatus.Ok, Find(checks, "Reverse DNS").Status);
        Assert.Equal(CheckStatus.Ok, Find(checks, "Blacklists").Status);
        Assert.NotEqual(CheckStatus.Error, Find(checks, "TLS-Zertifikat").Status); // the test certificate is valid for 30 days
        Assert.All(checks.Where(c => c.Title.StartsWith("Port ") && c.Title != "Port 25 nach außen"), c => Assert.Equal(CheckStatus.Ok, c.Status));
        Assert.Equal(CheckStatus.Error, Find(checks, "Port 25 nach außen").Status);
    }

    [Theory]
    [InlineData("\"v=spf1 mx a -all\"", "ungültig", "ohne Anführungszeichen")]
    [InlineData(" v=spf1 mx -all", "ungültig", "genau mit v=spf1 beginnen")]
    public async Task Spf_record_with_quotes_or_spaces_is_explained(string record, string detail, string hint)
    {
        _dns.Txt["example.test"] = ["google-site-verification=abc", record];

        var spf = Find(await Diagnostics.RunAsync(), "SPF");

        Assert.Equal(CheckStatus.Error, spf.Status);
        Assert.Contains(detail, spf.Detail);
        Assert.Contains(hint, spf.Hint);
    }

    [Fact]
    public async Task Two_spf_records_are_an_error()
    {
        _dns.Txt["example.test"] = [$"v=spf1 ip4:{Ip} -all", "v=spf1 mx -all"];

        var spf = Find(await Diagnostics.RunAsync(), "SPF");

        Assert.Equal(CheckStatus.Error, spf.Status);
        Assert.Contains("Mehrere SPF-Einträge", spf.Hint);
    }

    [Fact]
    public async Task Finds_typical_mistakes()
    {
        _dns.Ptr[Ip] = "static-10.provider.example";
        _dns.Txt["example.test"] = ["v=spf1 mx -all"];
        _dns.Mx["example.test"] = [new MxRecord(10, "mx.alter-anbieter.example.")];
        _dns.Txt["test._domainkey.example.test"] = ["v=DKIM1; k=rsa; p=MIIBalterSchluessel"];
        _dns.Txt.Remove("_dmarc.example.test");
        _dns.AddA("10.113.0.203.zen.spamhaus.org", "127.0.0.2");

        var checks = await Diagnostics.RunAsync();

        Assert.Contains("statt mail.example.test", Find(checks, "Reverse DNS").Detail);
        Assert.Equal(CheckStatus.Error, Find(checks, "SPF").Status);
        Assert.Contains("Der MX zeigt nicht auf diesen Server", Find(checks, "MX").Hint);
        Assert.Contains("passt nicht", Find(checks, "DKIM").Detail);
        Assert.Equal(CheckStatus.Warning, Find(checks, "DMARC").Status);
        var blacklists = Find(checks, "Blacklists");
        Assert.Equal("gelistet bei Spamhaus", blacklists.Detail);
        Assert.Contains("check.spamhaus.org", blacklists.Hint);
    }

    [Fact]
    public async Task Admin_page_shows_the_results()
    {
        var admin = _server.HostAccounts.AddAccount(Mailserver.Core.EmailAddress.Parse("chef@example.test"), TestServer.Password);
        _server.HostAccounts.SetAdmin(admin.Address, true);
        using var web = new WebClient(_server.WebPort);
        await web.LoginAsync("chef@example.test", TestServer.Password);

        await web.GetAsync("/Admin/Diagnose");

        Assert.Contains("Domain example.test", web.LastPage);
        Assert.Contains("Port 25 nach außen", web.LastPage);
        Assert.Contains("Fehler", web.LastPage);
    }
}
