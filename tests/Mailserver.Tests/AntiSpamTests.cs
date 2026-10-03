using System.Net;
using System.Text;
using Mailserver.AntiSpam;
using Mailserver.AntiSpam.Checks;
using Mailserver.AntiSpam.Dns;
using Mailserver.Core;
using Mailserver.Core.Dkim;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Cryptography;

namespace Mailserver.Tests;

public class SpfCheckerTests
{
    private static readonly IPAddress Ip = IPAddress.Parse("192.0.2.10");
    private readonly FakeDns _dns = new();

    private Task<SpfOutcome> Check(string sender = "bob@example.org", IPAddress? ip = null) =>
        new SpfChecker(_dns).CheckAsync(ip ?? Ip, sender, "mx.example.org", CancellationToken.None);

    [Theory]
    [InlineData("v=spf1 ip4:192.0.2.0/24 -all", SpfResult.Pass)]
    [InlineData("v=spf1 ip4:198.51.100.1 -all", SpfResult.Fail)]
    [InlineData("v=spf1 ip4:198.51.100.1 ~all", SpfResult.SoftFail)]
    [InlineData("v=spf1 ?all", SpfResult.Neutral)]
    [InlineData("v=spf1 ip4:198.51.100.1", SpfResult.Neutral)]
    [InlineData("v=spf1 -ip4:192.0.2.10 +all", SpfResult.Fail)]
    [InlineData("v=spf1 ip6:2001:db8::/32 -all", SpfResult.Fail)]
    [InlineData("v=spf1 foo:bar -all", SpfResult.PermError)]
    [InlineData("v=spf1 ip4:300.1.1.1 -all", SpfResult.PermError)]
    public async Task Evaluates_basic_mechanisms(string record, SpfResult expected)
    {
        _dns.AddTxt("example.org", record);
        Assert.Equal(expected, (await Check()).Result);
    }

    [Fact]
    public async Task No_record_is_none_and_two_records_are_permerror()
    {
        Assert.Equal(SpfResult.None, (await Check()).Result);
        _dns.AddTxt("example.org", "v=spf1 -all").AddTxt("example.org", "v=spf1 +all");
        Assert.Equal(SpfResult.PermError, (await Check()).Result);
    }

    [Fact]
    public async Task Ignores_unrelated_txt_records()
    {
        _dns.AddTxt("example.org", "google-site-verification=abc").AddTxt("example.org", "v=spf1 ip4:192.0.2.10 -all");
        Assert.Equal(SpfResult.Pass, (await Check()).Result);
    }

    [Fact]
    public async Task Follows_include_and_redirect()
    {
        _dns.AddTxt("example.org", "v=spf1 include:_spf.provider.test -all")
            .AddTxt("_spf.provider.test", "v=spf1 redirect=_netblocks.provider.test")
            .AddTxt("_netblocks.provider.test", "v=spf1 ip4:192.0.2.0/28 ~all");
        Assert.Equal(SpfResult.Pass, (await Check()).Result);
        Assert.Equal(SpfResult.Fail, (await Check(ip: IPAddress.Parse("192.0.2.200"))).Result);
    }

    [Fact]
    public async Task Include_without_record_is_permerror()
    {
        _dns.AddTxt("example.org", "v=spf1 include:missing.test -all");
        Assert.Equal(SpfResult.PermError, (await Check()).Result);
    }

    [Fact]
    public async Task Matches_a_and_mx_with_cidr()
    {
        _dns.AddTxt("example.org", "v=spf1 a:web.example.org/24 mx -all").AddA("web.example.org", "192.0.2.99");
        Assert.Equal(SpfResult.Pass, (await Check()).Result);

        var mxOnly = new FakeDns();
        mxOnly.AddTxt("example.org", "v=spf1 mx -all").AddA("mail.example.org", "192.0.2.10");
        mxOnly.Mx["example.org"] = [new MxRecord(10, "mail.example.org")];
        Assert.Equal(SpfResult.Pass, (await new SpfChecker(mxOnly).CheckAsync(Ip, "bob@example.org", "h", CancellationToken.None)).Result);
    }

    [Fact]
    public async Task Expands_macros_in_exists()
    {
        _dns.AddTxt("example.org", "v=spf1 exists:%{ir}.%{l1r+-}._spf.%{d} -all").AddA("10.2.0.192.bob._spf.example.org", "127.0.0.2");
        Assert.Equal(SpfResult.Pass, (await Check()).Result);
    }

    [Fact]
    public async Task Too_many_lookups_is_permerror()
    {
        _dns.AddTxt("example.org", "v=spf1 include:l1.test -all");
        for (var i = 1; i <= 11; i++)
        {
            _dns.AddTxt($"l{i}.test", $"v=spf1 include:l{i + 1}.test -all");
        }

        Assert.Equal(SpfResult.PermError, (await Check()).Result);
    }

    [Fact]
    public async Task Dns_failure_is_temperror()
    {
        _dns.Failing.Add("example.org");
        Assert.Equal(SpfResult.TempError, (await Check()).Result);
    }

    [Fact]
    public async Task Null_sender_uses_helo_domain()
    {
        _dns.AddTxt("mx.example.org", "v=spf1 ip4:192.0.2.10 -all");
        var result = await new SpfChecker(_dns).CheckAsync(Ip, "mx.example.org", "mx.example.org", CancellationToken.None);
        Assert.Equal(SpfResult.Pass, result.Result);
    }
}

public class DkimAndDmarcTests : TestData
{
    private readonly FakeDns _dns = new();

    private (MimeMessage Message, string Record) SignedMessage(string domain, string selector = "s1")
    {
        Accounts.AddDomain(domain, selector);
        var keys = new DkimKeyStore(Paths);
        keys.GenerateKey(domain, selector);
        var message = new MimeMessage { Subject = "Hallo", Body = new TextPart("plain") { Text = "Test" } };
        message.From.Add(MailboxAddress.Parse($"news@{domain}"));
        message.To.Add(MailboxAddress.Parse("x@example.test"));
        message.Prepare(EncodingConstraint.SevenBit);
        new DkimSigner(keys.GetKeyPath(domain, selector), domain, selector)
        {
            HeaderCanonicalizationAlgorithm = DkimCanonicalizationAlgorithm.Relaxed,
            BodyCanonicalizationAlgorithm = DkimCanonicalizationAlgorithm.Relaxed,
        }.Sign(message, [HeaderId.From, HeaderId.Subject, HeaderId.To]);
        return (message, keys.GetDnsRecord(domain, selector));
    }

    [Fact]
    public async Task Valid_signature_passes_and_tampering_fails()
    {
        var (message, record) = SignedMessage("sender.test");
        _dns.AddTxt("s1._domainkey.sender.test", record);
        var checker = new DkimChecker(_dns);

        var result = Assert.Single(await checker.VerifyAsync(message, CancellationToken.None));
        Assert.Equal(DkimResult.Pass, result.Result);
        Assert.Equal("sender.test", result.Domain);

        message.Subject = "Geändert";
        Assert.Equal(DkimResult.Fail, (await checker.VerifyAsync(message, CancellationToken.None)).Single().Result);
    }

    [Fact]
    public async Task Missing_key_is_permerror()
    {
        var (message, _) = SignedMessage("sender.test");
        Assert.Equal(DkimResult.PermError, (await new DkimChecker(_dns).VerifyAsync(message, CancellationToken.None)).Single().Result);
    }

    [Fact]
    public async Task Dmarc_passes_with_relaxed_alignment_and_fails_strict()
    {
        var dkim = new[] { new DkimSignatureResult(DkimResult.Pass, "mail.sender.test", "s1") };
        var noSpf = new SpfOutcome(SpfResult.None, "");
        _dns.AddTxt("_dmarc.sender.test", "v=DMARC1; p=reject");
        var checker = new DmarcChecker(_dns);

        Assert.Equal(DmarcResult.Pass, (await checker.CheckAsync("sender.test", noSpf, dkim, CancellationToken.None)).Result);

        var strict = new FakeDns().AddTxt("_dmarc.sender.test", "v=DMARC1; p=quarantine; adkim=s");
        var outcome = await new DmarcChecker(strict).CheckAsync("sender.test", noSpf, dkim, CancellationToken.None);
        Assert.Equal(DmarcResult.Fail, outcome.Result);
        Assert.Equal(DmarcPolicy.Quarantine, outcome.Policy);
    }

    [Fact]
    public async Task Dmarc_uses_organizational_domain_and_subdomain_policy()
    {
        _dns.AddTxt("_dmarc.example.co.uk", "v=DMARC1; p=none; sp=reject");
        var outcome = await new DmarcChecker(_dns).CheckAsync("news.example.co.uk", new SpfOutcome(SpfResult.Pass, "other.test"), [],
            CancellationToken.None);

        Assert.Equal(DmarcResult.Fail, outcome.Result);
        Assert.Equal(DmarcPolicy.Reject, outcome.Policy);
    }

    [Fact]
    public async Task Dmarc_spf_alignment()
    {
        _dns.AddTxt("_dmarc.sender.test", "v=DMARC1; p=reject");
        var outcome = await new DmarcChecker(_dns).CheckAsync("sender.test", new SpfOutcome(SpfResult.Pass, "bounces.sender.test"), [],
            CancellationToken.None);
        Assert.Equal(DmarcResult.Pass, outcome.Result);
    }

    [Theory]
    [InlineData("mail.example.de", "example.de")]
    [InlineData("a.b.example.co.uk", "example.co.uk")]
    [InlineData("example.de", "example.de")]
    public void Organizational_domain(string domain, string expected) => Assert.Equal(expected, DomainHelper.OrganizationalDomain(domain));
}

public class BlocklistAndGreylistTests : TestData
{
    [Fact]
    public async Task Detects_listing_and_ignores_error_codes()
    {
        var dns = new FakeDns()
            .AddA("2.0.0.127.zen.example", "127.0.0.2")
            .AddA("3.0.0.127.zen.example", "127.255.255.254");
        var checker = new DnsBlocklistChecker(dns);

        Assert.True(await checker.IsListedAsync(IPAddress.Parse("127.0.0.2"), "zen.example", CancellationToken.None));
        Assert.False(await checker.IsListedAsync(IPAddress.Parse("127.0.0.3"), "zen.example", CancellationToken.None));
        Assert.False(await checker.IsListedAsync(IPAddress.Parse("127.0.0.4"), "zen.example", CancellationToken.None));
        Assert.Equal("1.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.8.b.d.0.1.0.0.2",
            DnsBlocklistChecker.ReverseName(IPAddress.Parse("2001:db8::1")));
    }

    [Fact]
    public void Greylisting_defers_first_attempt_and_accepts_after_delay()
    {
        var time = new ManualTime(DateTimeOffset.Parse("2026-01-01T10:00:00Z"));
        var greylist = new Greylist(Database, Options.Create(new MailserverOptions()), time);
        var ip = IPAddress.Parse("198.51.100.20");

        Assert.False(greylist.Check(ip, "a@sender.test", "max@example.test"));
        time.Now = time.Now.AddMinutes(2);
        Assert.False(greylist.Check(ip, "a@sender.test", "max@example.test"));
        time.Now = time.Now.AddMinutes(4);
        Assert.True(greylist.Check(ip, "a@sender.test", "max@example.test"));

        // Same /24, later message: known, accepted immediately.
        time.Now = time.Now.AddDays(3);
        Assert.True(greylist.Check(IPAddress.Parse("198.51.100.77"), "a@sender.test", "max@example.test"));
        Assert.False(greylist.Check(ip, "other@sender.test", "max@example.test"));
    }

    [Fact]
    public void Header_editor_removes_only_matching_fields()
    {
        var raw = Encoding.ASCII.GetBytes("X-Spam-Flag: YES\r\nSubject: Hi\r\nX-Spam-Status: Yes,\r\n\tscore=99\r\n\r\nX-Spam-Flag: body stays\r\n");
        var cleaned = HeaderEditor.RemoveFields(raw, (name, _) => name.StartsWith("X-Spam-", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Subject: Hi\r\n\r\nX-Spam-Flag: body stays\r\n", Encoding.ASCII.GetString(cleaned));
    }

    [Fact]
    public void Shipped_settings_do_not_duplicate_list_defaults()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Mailserver.Service", "appsettings.json");
        var options = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(path)).Build()
            .GetSection(MailserverOptions.SectionName).Get<MailserverOptions>()!;

        Assert.Equal(["0.0.0.0"], options.Smtp.EffectiveListenAddresses);
        Assert.Equal(["0.0.0.0"], options.Imap.EffectiveListenAddresses);
        Assert.Equal(2, options.Spam.EffectiveDnsBlocklists.Count);
    }
}
