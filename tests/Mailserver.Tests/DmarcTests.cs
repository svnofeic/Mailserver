using System.IO.Compression;
using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using Mailserver.Core.Dmarc;
using Mailserver.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace Mailserver.Tests;

/// <summary>DMARC aggregate reports: received at postmaster@, stored, filed away and evaluated.</summary>
public sealed class DmarcTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private DmarcReports Reports => _server.Services.GetRequiredService<DmarcReports>();

    private static string ReportXml(string id, string ip2Dkim = "fail") => $"""
        <?xml version="1.0" encoding="UTF-8" ?>
        <feedback>
          <report_metadata><org_name>google.com</org_name><email>noreply-dmarc-support@google.com</email>
            <report_id>{id}</report_id><date_range><begin>{DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds()}</begin><end>{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}</end></date_range></report_metadata>
          <policy_published><domain>example.test</domain><adkim>r</adkim><aspf>r</aspf><p>none</p><sp>none</sp><pct>100</pct></policy_published>
          <record>
            <row><source_ip>89.22.103.114</source_ip><count>12</count><policy_evaluated><disposition>none</disposition><dkim>pass</dkim><spf>pass</spf></policy_evaluated></row>
            <identifiers><header_from>example.test</header_from></identifiers>
            <auth_results><dkim><domain>example.test</domain><result>pass</result><selector>test</selector></dkim><spf><domain>example.test</domain><result>pass</result></spf></auth_results>
          </record>
          <record>
            <row><source_ip>203.0.113.9</source_ip><count>3</count><policy_evaluated><disposition>none</disposition><dkim>{ip2Dkim}</dkim><spf>fail</spf></policy_evaluated></row>
            <identifiers><header_from>example.test</header_from></identifiers>
            <auth_results><spf><domain>spammer.test</domain><result>fail</result></spf></auth_results>
          </record>
        </feedback>
        """;

    private static MimeMessage ReportMail(string id, bool zip = false)
    {
        var xml = Encoding.UTF8.GetBytes(ReportXml(id));
        using var packed = new MemoryStream();
        string name;
        if (zip)
        {
            using (var archive = new ZipArchive(packed, ZipArchiveMode.Create, leaveOpen: true))
            using (var entry = archive.CreateEntry("google.com!example.test!1!2.xml").Open())
            {
                entry.Write(xml);
            }

            name = "google.com!example.test!1!2.zip";
        }
        else
        {
            using (var gzip = new GZipStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            {
                gzip.Write(xml);
            }

            name = "google.com!example.test!1!2.xml.gz";
        }

        var builder = new BodyBuilder { TextBody = "This is an aggregate report from google.com." };
        builder.Attachments.Add(name, packed.ToArray(), zip ? new ContentType("application", "zip") : new ContentType("application", "gzip"));
        var message = new MimeMessage { Subject = $"Report domain: example.test Submitter: google.com Report-ID: {id}", Body = builder.ToMessageBody() };
        message.From.Add(MailboxAddress.Parse("noreply-dmarc-support@remote.test"));
        message.To.Add(MailboxAddress.Parse("postmaster@example.test"));
        return message;
    }

    [Fact]
    public void Parses_the_report_format()
    {
        var report = DmarcReports.Parse(ReportXml("abc"))!;
        Assert.Equal("google.com", report.Organization);
        Assert.Equal("example.test", report.Domain);
        Assert.Equal("none", report.Policy);
        Assert.Equal(2, report.Rows.Count);
        Assert.True(report.Rows[0].Passed);
        Assert.False(report.Rows[1].Passed);
        Assert.Equal("spammer.test", report.Rows[1].SpfDomain);
        Assert.Null(DmarcReports.Parse("<html/>"));
        // No external entities.
        Assert.ThrowsAny<System.Xml.XmlException>(() => DmarcReports.Parse("<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><feedback>&e;</feedback>"));
    }

    [Fact]
    public async Task Reports_to_postmaster_are_stored_filed_and_shown()
    {
        _server.HostAccounts.SetAdmin(Mailserver.Core.EmailAddress.Parse("alice@example.test"), true);
        using (var client = new SmtpClient { ServerCertificateValidationCallback = (_, _, _, _) => true })
        {
            await client.ConnectAsync("127.0.0.1", _server.InboundPort, SecureSocketOptions.StartTls);
            await client.SendAsync(ReportMail("r1"));
            await client.SendAsync(ReportMail("r2", zip: true));
            await client.SendAsync(ReportMail("r1"));
            await client.DisconnectAsync(true);
        }

        // postmaster@ has no mailbox: it reaches the administrator, in the report folder, already read.
        var alice = _server.User("alice");
        var folder = _server.HostMailboxes.GetFolder(alice.Id, DmarcReports.Folder)!;
        var filed = _server.HostMailboxes.ListMessages(folder.Id);
        Assert.Equal(3, filed.Count);
        Assert.All(filed, m => Assert.True(m.HasFlag(MessageFlags.Seen)));
        Assert.Empty(_server.Inbox("alice"));
        Assert.Empty(_server.Inbox("bob"));

        // The same report twice counts once.
        var summary = Assert.Single(Reports.Summary(DateTimeOffset.UtcNow.AddDays(-7)));
        Assert.Equal(2, summary.Reports);
        Assert.Equal(30, summary.Messages);
        Assert.Equal(24, summary.Passed);
        Assert.Equal(["89.22.103.114", "203.0.113.9"], summary.Sources.Select(s => s.Ip));
        Assert.Equal("warn", Mailserver.Web.Pages.Admin.Dmarc.IndexModel.Advice(summary).Tone);

        using var web = new WebClient(_server.WebPort);
        await web.LoginAsync("alice@example.test", TestServer.Password);
        await web.GetAsync("/Admin/Dmarc");
        Assert.Contains("203.0.113.9", web.LastPage);
        Assert.Contains("0 von 6", web.LastPage);

        // Reading existing mails again finds nothing new.
        await web.PostAsync("/Admin/Dmarc", "/Admin/Dmarc?handler=Import&days=30");
        Assert.Contains("3 Bericht-Mail(s) gefunden, 0 neue Berichte", web.LastPage);
    }

    [Fact]
    public void Advice_depends_on_the_pass_rate_and_policy()
    {
        var good = new DmarcDomainSummary("example.test", 3, 100, 100, "none", DateTimeOffset.UtcNow,
            [new DmarcSource("1.2.3.4", 100, 100, "example.test", "example.test", "example.test", ["google.com"])]);
        Assert.Contains("p=quarantine", Mailserver.Web.Pages.Admin.Dmarc.IndexModel.Advice(good).Text);
        Assert.Contains("p=reject", Mailserver.Web.Pages.Admin.Dmarc.IndexModel.Advice(good with { Policy = "quarantine" }).Text);
    }
}
