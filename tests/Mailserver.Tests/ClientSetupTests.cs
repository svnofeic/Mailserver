using System.Net;
using System.Xml.Linq;

namespace Mailserver.Tests;

/// <summary>Automatic setup of mail programs: Apple profile, Thunderbird autoconfig, Outlook autodiscover.</summary>
public sealed class ClientSetupTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task Thunderbird_and_Outlook_find_the_settings()
    {
        using var web = new WebClient(_server.WebPort);
        foreach (var path in new[] { "/mail/config-v1.1.xml?emailaddress=alice%40example.test", "/.well-known/autoconfig/mail/config-v1.1.xml" })
        {
            var response = await web.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var config = XDocument.Parse(web.LastPage);
            var incoming = config.Descendants("incomingServer").Single();
            Assert.Equal("imap", incoming.Attribute("type")!.Value);
            Assert.Equal("SSL", incoming.Element("socketType")!.Value);
            Assert.Equal("%EMAILADDRESS%", incoming.Element("username")!.Value);
            Assert.Contains(config.Descendants("outgoingServer"), s => s.Element("socketType")!.Value == "STARTTLS");
        }

        await web.GetAsync("/mail/config-v1.1.xml?emailaddress=alice%40example.test");
        Assert.Equal("example.test", XDocument.Parse(web.LastPage).Descendants("domain").Single().Value);
        Assert.Equal(_server.ImapsPort.ToString(), XDocument.Parse(web.LastPage).Descendants("incomingServer").Single().Element("port")!.Value);

        var request = """
            <?xml version="1.0" encoding="utf-8"?>
            <Autodiscover xmlns="http://schemas.microsoft.com/exchange/autodiscover/outlook/requestschema/2006">
              <Request><EMailAddress>alice@example.test</EMailAddress>
                <AcceptableResponseSchema>http://schemas.microsoft.com/exchange/autodiscover/outlook/responseschema/2006a</AcceptableResponseSchema></Request>
            </Autodiscover>
            """;
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
        var answer = await http.PostAsync($"https://127.0.0.1:{_server.WebPort}/autodiscover/autodiscover.xml",
            new StringContent(request, System.Text.Encoding.UTF8, "text/xml"));
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        var discovered = XDocument.Parse(await answer.Content.ReadAsStringAsync());
        var protocols = discovered.Descendants().Where(e => e.Name.LocalName == "Protocol").ToList();
        Assert.Equal(["IMAP", "SMTP"], protocols.Select(p => p.Elements().Single(e => e.Name.LocalName == "Type").Value));
        Assert.All(protocols, p => Assert.Equal("alice@example.test", p.Elements().Single(e => e.Name.LocalName == "LoginName").Value));

        // Foreign domains get nothing.
        var foreign = await http.PostAsync($"https://127.0.0.1:{_server.WebPort}/autodiscover/autodiscover.xml",
            new StringContent(request.Replace("example.test", "remote.test"), System.Text.Encoding.UTF8, "text/xml"));
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [Fact]
    public async Task The_Apple_profile_sets_up_the_mailbox_without_a_password()
    {
        using var web = new WebClient(_server.WebPort);
        await web.LoginAsync("alice@example.test", TestServer.Password);
        await web.GetAsync("/Account/Setup");
        Assert.Contains("Profil für alice@example.test laden", web.LastPage);

        var response = await web.GetAsync("/Account/Setup?handler=Profile");
        Assert.Equal("application/x-apple-aspen-config", response.Content.Headers.ContentType!.MediaType);
        // Signed with the server certificate when there is one (as here); the iPhone then shows "verified".
        var signed = new System.Security.Cryptography.Pkcs.SignedCms();
        signed.Decode(await web.GetBytesAsync("/Account/Setup?handler=Profile"));
        signed.CheckSignature(verifySignatureOnly: true);
        var text = System.Text.Encoding.UTF8.GetString(signed.ContentInfo.Content);
        var plist = XDocument.Parse(text);
        var account = plist.Descendants("dict").First(d => d.Elements("string").Any(s => s.Value == "com.apple.mail.managed"));
        string Value(string key) => ((XElement)account.Elements("key").Single(k => k.Value == key).NextNode!).Value;
        Assert.Equal("EmailTypeIMAP", Value("EmailAccountType"));
        Assert.Equal("alice@example.test", Value("IncomingMailServerUsername"));
        Assert.Equal(_server.ImapsPort.ToString(), Value("IncomingMailServerPortNumber"));
        Assert.DoesNotContain("<key>IncomingPassword</key>", text);
        // Calendar and contacts come with it.
        var caldav = plist.Descendants("dict").First(d => d.Elements("string").Any(s => s.Value == "com.apple.caldav.account"));
        Assert.Contains("/dav/principals/alice@example.test/", caldav.ToString());
        Assert.Contains(_server.WebPort.ToString(), caldav.ToString());
        Assert.Contains(plist.Descendants("string"), s => s.Value == "com.apple.carddav.account");
        Assert.DoesNotContain(TestServer.Password, text);

        // Not signed in: no profile.
        using var anonymous = new WebClient(_server.WebPort);
        Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync("/Account/Setup?handler=Profile")).StatusCode);
    }
}
