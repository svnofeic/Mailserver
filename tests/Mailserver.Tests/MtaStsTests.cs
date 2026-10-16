using System.IO.Compression;
using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using Mailserver.AntiSpam.Diagnostics;
using Mailserver.AntiSpam.Dns;
using Mailserver.Core;
using Mailserver.Core.Dmarc;
using Mailserver.Core.Security;
using Mailserver.Smtp.Delivery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace Mailserver.Tests;

/// <summary>MTA-STS (RFC 8461) in both directions and TLS reports (RFC 8460).</summary>
public sealed class MtaStsTests
{
    [Fact]
    public void Parses_policies_and_matches_mx_hosts()
    {
        var policy = MtaStsPolicy.Parse("version: STSv1\r\nmode: enforce\r\nmx: mail.example.com\r\nmx: *.backup.example.com\r\nmax_age: 86400\r\n")!;
        Assert.True(policy.Enforce);
        Assert.Equal(TimeSpan.FromDays(1), policy.MaxAge);
        Assert.True(policy.Allows("MAIL.example.com."));
        Assert.True(policy.Allows("mx1.backup.example.com"));
        Assert.False(policy.Allows("a.b.backup.example.com"));
        Assert.False(policy.Allows("backup.example.com"));
        Assert.False(policy.Allows("evil.example.net"));

        Assert.Null(MtaStsPolicy.Parse("version: STSv2\nmode: enforce\nmx: a\nmax_age: 1"));
        Assert.Null(MtaStsPolicy.Parse("version: STSv1\nmode: enforce\nmax_age: 1"));
        Assert.Null(MtaStsPolicy.Parse("<html>Not found</html>"));
    }

    [Fact]
    public void Published_policy_and_id_follow_the_settings()
    {
        var options = new MailserverOptions { Hostname = "mail.feicht.me" };
        var testing = MtaStsPublisher.PolicyText(options);
        Assert.Equal("version: STSv1\r\nmode: testing\r\nmx: mail.feicht.me\r\nmax_age: 604800\r\n", testing);
        var id = MtaStsPublisher.PolicyId(options);
        Assert.Equal(id, MtaStsPublisher.PolicyId(options));
        options.MtaSts.Mode = "enforce";
        Assert.NotEqual(id, MtaStsPublisher.PolicyId(options));
        Assert.True(MtaStsPolicy.Parse(MtaStsPublisher.PolicyText(options))!.Allows("mail.feicht.me"));
        Assert.Equal("v=TLSRPTv1; rua=mailto:postmaster@feicht.me", MtaStsPublisher.TlsRptRecord("feicht.me"));
    }

    [Fact]
    public async Task Remembers_policies_so_a_suppressed_record_does_not_switch_them_off()
    {
        var txt = new List<string> { "v=STSv1; id=1" };
        var fetches = 0;
        var policies = new MtaStsPolicies(null!, NullLogger<MtaStsPolicies>.Instance)
        {
            LookupTxt = (_, _) => Task.FromResult<IReadOnlyList<string>>(txt),
            Fetch = (_, _) => { fetches++; return Task.FromResult<string?>("version: STSv1\nmode: enforce\nmx: mx.remote.test\nmax_age: 86400\n"); },
        };

        Assert.True((await policies.GetAsync("remote.test", CancellationToken.None))!.Enforce);
        Assert.True((await policies.GetAsync("remote.test", CancellationToken.None))!.Enforce);
        Assert.Equal(1, fetches); // same id: no new download

        txt.Clear(); // an attacker drops the record
        Assert.True((await policies.GetAsync("remote.test", CancellationToken.None))!.Enforce);

        txt.Add("v=STSv1; id=2"); // a new id: fetched again
        await policies.GetAsync("remote.test", CancellationToken.None);
        Assert.Equal(2, fetches);

        var none = new MtaStsPolicies(null!, NullLogger<MtaStsPolicies>.Instance)
        {
            LookupTxt = (_, _) => Task.FromResult<IReadOnlyList<string>>([]),
            Fetch = (_, _) => throw new InvalidOperationException("must not fetch without a record"),
        };
        Assert.Null(await none.GetAsync("plain.test", CancellationToken.None));
    }

    [Fact]
    public async Task Policy_file_is_served_and_checked_by_the_diagnosis()
    {
        var dns = new FakeDns();
        await using var server = await TestServer.StartAsync(new Dictionary<string, string?> { ["Mailserver:MtaSts:Mode"] = "enforce" },
            services => services.AddSingleton<IDnsResolver>(dns));
        using var web = new WebClient(server.WebPort);
        await web.GetAsync("/.well-known/mta-sts.txt");
        var options = server.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<MailserverOptions>>().Value;
        Assert.Equal(MtaStsPublisher.PolicyText(options), web.LastPage);
        Assert.Contains("mode: enforce", web.LastPage);

        var diagnostics = server.Services.GetRequiredService<ServerDiagnostics>();
        diagnostics.Connect = (_, _, _) => Task.FromResult<string?>(null);
        diagnostics.LatestRelease = _ => Task.FromResult<string?>(null);
        DiagnosticCheck Check(IReadOnlyList<DiagnosticCheck> checks) => checks.Single(c => c.Title == "MTA-STS");

        Assert.Equal(CheckStatus.Info, Check(await diagnostics.RunAsync()).Status);

        dns.AddTxt("_mta-sts.example.test", MtaStsPublisher.TxtRecord(options));
        diagnostics.FetchMtaStsPolicy = (_, _) => Task.FromResult<string?>(null);
        Assert.Equal(CheckStatus.Error, Check(await diagnostics.RunAsync()).Status);

        diagnostics.FetchMtaStsPolicy = (_, _) => Task.FromResult<string?>("version: STSv1\nmode: enforce\nmx: mail.other.test\nmax_age: 604800\n");
        Assert.Equal(CheckStatus.Error, Check(await diagnostics.RunAsync()).Status);

        diagnostics.FetchMtaStsPolicy = (_, _) => Task.FromResult<string?>(MtaStsPublisher.PolicyText(options));
        Assert.Equal(CheckStatus.Ok, Check(await diagnostics.RunAsync()).Status);

        // Same content, other form (as written by hand or by the Plesk file manager): fine.
        var maxAge = MtaStsPublisher.PolicyText(options).Split('\n').Single(l => l.StartsWith("max_age")).Trim();
        diagnostics.FetchMtaStsPolicy = (_, _) => Task.FromResult<string?>($"\uFEFFversion: STSv1\nmx: MAIL.example.test\nmode: enforce  \n{maxAge}");
        Assert.Equal(CheckStatus.Ok, Check(await diagnostics.RunAsync()).Status);

        diagnostics.FetchMtaStsPolicy = (_, _) => Task.FromResult<string?>(MtaStsPublisher.PolicyText(options).Replace("enforce", "testing"));
        var different = Check(await diagnostics.RunAsync());
        Assert.Equal(CheckStatus.Warning, different.Status);
        Assert.Contains("mode: testing, eingestellt ist enforce", different.Detail);

        // Right file, other id: only a hint.
        dns.Txt.Remove("_mta-sts.example.test");
        dns.AddTxt("_mta-sts.example.test", "v=STSv1; id=20261001");
        diagnostics.FetchMtaStsPolicy = (_, _) => Task.FromResult<string?>(MtaStsPublisher.PolicyText(options));
        Assert.Equal(CheckStatus.Info, Check(await diagnostics.RunAsync()).Status);
    }

    private static string TlsJson(string id, int ok, int failed) => $$"""
        {
          "organization-name": "Google Inc.",
          "date-range": { "start-datetime": "{{DateTimeOffset.UtcNow.AddDays(-1):yyyy-MM-ddTHH:mm:ssZ}}", "end-datetime": "{{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ}}" },
          "contact-info": "smtp-tls-reporting@google.com",
          "report-id": "{{id}}",
          "policies": [{
            "policy": { "policy-type": "sts", "policy-string": ["version: STSv1", "mode: testing"], "policy-domain": "example.test" },
            "summary": { "total-successful-session-count": {{ok}}, "total-failure-session-count": {{failed}} },
            "failure-details": [{ "result-type": "certificate-expired", "sending-mta-ip": "203.0.113.5", "receiving-mx-hostname": "mail.example.test.", "failed-session-count": {{failed}} }]
          }]
        }
        """;

    [Fact]
    public async Task Tls_reports_are_stored_and_filed_like_dmarc_reports()
    {
        await using var server = await TestServer.StartAsync();
        server.HostAccounts.SetAdmin(EmailAddress.Parse("alice@example.test"), true);
        using var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(TlsJson("2026-10-14T00:00:00Z_example.test", 120, 3)));
        }

        var builder = new BodyBuilder { TextBody = "This is an aggregate TLS report from Google." };
        builder.Attachments.Add("google.com!example.test!1!2!001.json.gz", packed.ToArray(), new ContentType("application", "tlsrpt+gzip"));
        var message = new MimeMessage { Subject = "Report Domain: example.test Submitter: google.com Report-ID: <x@google.com>", Body = builder.ToMessageBody() };
        message.From.Add(MailboxAddress.Parse("noreply-smtp-tls-reporting@remote.test"));
        message.To.Add(MailboxAddress.Parse("postmaster@example.test"));
        message.Headers.Add("TLS-Report-Domain", "example.test");

        using (var client = new SmtpClient { ServerCertificateValidationCallback = (_, _, _, _) => true })
        {
            await client.ConnectAsync("127.0.0.1", server.InboundPort, SecureSocketOptions.StartTls);
            await client.SendAsync(message);
            await client.SendAsync(message);
            await client.DisconnectAsync(true);
        }

        var reports = server.Services.GetRequiredService<DmarcReports>();
        var summary = Assert.Single(reports.TlsSummary(DateTimeOffset.UtcNow.AddDays(-7)));
        Assert.Equal(1, summary.Reports);
        Assert.Equal(120, summary.Successful);
        Assert.Equal(3, summary.Failed);
        Assert.Equal("certificate-expired", summary.Failures.Single().Type);
        var folder = server.HostMailboxes.GetFolder(server.User("alice").Id, DmarcReports.Folder)!;
        Assert.Equal(2, server.HostMailboxes.ListMessages(folder.Id).Count);

        using var web = new WebClient(server.WebPort);
        await web.LoginAsync("alice@example.test", TestServer.Password);
        await web.GetAsync("/Admin/Dmarc");
        Assert.Contains("Zertifikat abgelaufen: 3 (mail.example.test)", web.LastPage);
        await web.GetAsync("/Admin/Domains/Details?name=example.test");
        Assert.Contains("_mta-sts.example.test", web.LastPage);
        Assert.Contains("v=TLSRPTv1; rua=mailto:postmaster@example.test", web.LastPage);
    }
}
