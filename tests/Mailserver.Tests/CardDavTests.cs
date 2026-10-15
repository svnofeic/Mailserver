using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Mailserver.Core.Accounts;
using Mailserver.Core.Dav;
using Microsoft.Extensions.DependencyInjection;

namespace Mailserver.Tests;

/// <summary>CardDAV: the phone's contacts and the web address book are the same.</summary>
public sealed class CardDavTests : IAsyncLifetime
{
    private TestServer _server = null!;
    private HttpClient _dav = null!;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync();
        _dav = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true })
        {
            BaseAddress = new Uri($"https://127.0.0.1:{_server.WebPort}"),
        };
        _dav.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"alice@example.test:{TestServer.Password}")));
    }

    public async Task DisposeAsync()
    {
        _dav.Dispose();
        await _server.DisposeAsync();
    }

    private const string Book = "/dav/addressbooks/alice@example.test/default/";

    private async Task<HttpResponseMessage> SendAsync(string method, string path, string? body = null, string contentType = "application/xml", string? depth = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, contentType);
        }

        if (depth is not null)
        {
            request.Headers.Add("Depth", depth);
        }

        return await _dav.SendAsync(request);
    }

    [Fact]
    public async Task Phone_contacts_and_web_address_book_stay_the_same()
    {
        var alice = _server.User("alice");
        var contacts = _server.Services.GetRequiredService<Contacts>();
        var store = _server.Services.GetRequiredService<DavStore>();
        // Saved in the webmail before CardDAV existed.
        contacts.Save(alice.Id, "alt@remote.test", "Alter Kontakt");

        // The phone finds the address book and gets the old contact as a vCard.
        var home = await SendAsync("PROPFIND", "/dav/addressbooks/alice@example.test/",
            """<d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/><d:displayname/></d:prop></d:propfind>""", depth: "1");
        var homeText = await home.Content.ReadAsStringAsync();
        Assert.Contains("addressbook", homeText);
        Assert.Contains("Kontakte", homeText);
        var listing = await (await SendAsync("PROPFIND", Book, """<d:propfind xmlns:d="DAV:"><d:prop><d:getetag/></d:prop></d:propfind>""", depth: "1"))
            .Content.ReadAsStringAsync();
        Assert.Contains(".vcf", listing);
        var bookId = store.Collections(alice.Id, DavStore.AddressBook).Single().Id;
        Assert.Contains("alt@remote.test", Assert.Single(store.Objects(bookId)).Data);

        // A contact made on the phone: full vCard kept, its addresses become suggestions.
        var card = """
            BEGIN:VCARD
            VERSION:3.0
            PRODID:-//Apple Inc.//iPhone OS 26.0//EN
            UID:7A1C-PHONE
            N:Becker;Bob;;;
            FN:Bob Becker
            item1.EMAIL;type=INTERNET;type=pref:bob.becker@remote.test
            EMAIL;type=INTERNET;type=WORK:bob@firma.test
            TEL;type=CELL:+49 170 1234567
            END:VCARD
            """.ReplaceLineEndings("\r\n");
        Assert.Equal(HttpStatusCode.Created, (await SendAsync("PUT", Book + "7A1C-PHONE.vcf", card, "text/vcard")).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await SendAsync("PUT", Book + "kaputt.vcf", "BEGIN:VCARD\r\nFN:x\r\nEND:VCARD\r\n", "text/vcard")).StatusCode);

        using var web = new WebClient(_server.WebPort);
        await web.LoginAsync("alice@example.test", TestServer.Password);
        await web.GetAsync("/Contacts?handler=Suggest&q=becker");
        var suggestions = JsonDocument.Parse(web.LastPage).RootElement.EnumerateArray().Select(e => e.GetProperty("address").GetString()).ToList();
        Assert.Contains("bob.becker@remote.test", suggestions);
        await web.GetAsync("/Contacts?handler=Suggest&q=firma");
        Assert.Contains("bob@firma.test", web.LastPage);

        // Renamed and readdressed in the webmail: the phone's vCard changes, the phone number stays.
        var work = contacts.List(alice.Id, "firma").Single();
        await web.PostAsync("/Contacts", "/Contacts?handler=Save", ("id", work.Id.ToString()), ("address", "bob@neue-firma.test"), ("name", "Bob Becker (Büro)"));
        var changed = store.Object(bookId, "7A1C-PHONE.vcf")!.Data;
        Assert.Contains("bob@neue-firma.test", changed);
        Assert.DoesNotContain("bob@firma.test", changed);
        Assert.Contains("TEL;type=CELL:+49 170 1234567", changed);
        Assert.Contains("FN:Bob Becker (Büro)", changed);

        // A new contact in the webmail is a new vCard for the phone (seen through sync).
        await web.PostAsync("/Contacts", "/Contacts?handler=Save", ("address", "carla@remote.test"), ("name", "Carla"));
        var sync = await (await SendAsync("REPORT", Book,
            """<d:sync-collection xmlns:d="DAV:" xmlns:card="urn:ietf:params:xml:ns:carddav"><d:sync-token/><d:prop><d:getetag/><card:address-data/></d:prop></d:sync-collection>"""))
            .Content.ReadAsStringAsync();
        Assert.Contains("carla@remote.test", sync);

        // Deleted on the phone: no longer a saved contact (still a suggestion only if mail was sent to it).
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync("DELETE", Book + "7A1C-PHONE.vcf")).StatusCode);
        Assert.Empty(contacts.List(alice.Id, "becker"));

        // Deleted in the webmail: the vCard goes too.
        var carla = contacts.List(alice.Id, "carla").Single();
        await web.PostAsync("/Contacts", $"/Contacts?handler=Delete&id={carla.Id}");
        Assert.DoesNotContain(store.Objects(bookId), o => o.Data.Contains("carla@remote.test"));
    }

    [Fact]
    public void Cards_are_edited_line_by_line()
    {
        var card = ContactCards.Build("u-1", "Max Mustermann", "max@remote.test");
        Assert.Contains("N:Mustermann;Max;;;", card);
        Assert.Equal("u-1", VCardData.Inspect(card).Uid);
        var two = ContactCards.Update(card, "nicht@da.test", "zweite@remote.test", null);
        Assert.Contains("max@remote.test", two);
        Assert.Contains("zweite@remote.test", two);
        Assert.NotNull(ContactCards.RemoveAddress(two, "MAX@remote.test"));
        Assert.Null(ContactCards.RemoveAddress(card, "max@remote.test"));
        Assert.Equal(2, ContactCards.SplitCards(card + two).Count());
    }
}
