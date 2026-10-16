using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace Mailserver.Tests;

/// <summary>CalDAV as the iPhone, the Mac, Thunderbird and DAVx⁵ use it: discovery, calendars, events, sync.</summary>
public sealed class CalDavTests : IAsyncLifetime
{
    private static readonly XNamespace D = "DAV:";
    private static readonly XNamespace C = "urn:ietf:params:xml:ns:caldav";
    private static readonly XNamespace CS = "http://calendarserver.org/ns/";

    private TestServer _server = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync();
        _http = Client("alice@example.test", TestServer.Password);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
    }

    private HttpClient Client(string user, string password)
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, ServerCertificateCustomValidationCallback = (_, _, _, _) => true })
        {
            BaseAddress = new Uri($"https://127.0.0.1:{_server.WebPort}"),
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
        return http;
    }

    private static string Event(string uid, string start, string summary, string extra = "") => $"""
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Test//DE
        BEGIN:VEVENT
        UID:{uid}
        DTSTAMP:20261001T080000Z
        DTSTART:{start}
        DURATION:PT1H
        SUMMARY:{summary}
        {extra}
        END:VEVENT
        END:VCALENDAR
        """.ReplaceLineEndings("\r\n").Replace("\r\n\r\n", "\r\n");

    private async Task<(HttpStatusCode Status, XDocument? Xml, HttpResponseMessage Response)> SendAsync(string method, string path, string? body = null,
        string? depth = null, HttpClient? http = null, string contentType = "application/xml", params (string Name, string Value)[] headers)
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

        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        var response = await (http ?? _http).SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.StartsWith("<?xml") ? XDocument.Parse(text) : null, response);
    }

    private static string PropFind(params XName[] props) =>
        new XDocument(new XElement(D + "propfind", new XAttribute(XNamespace.Xmlns + "d", D), new XElement(D + "prop", props.Select(p => new XElement(p))))).ToString();

    private static IEnumerable<XElement> Responses(XDocument? xml) => xml!.Root!.Elements(D + "response");

    [Fact]
    public async Task Clients_find_the_calendars_and_keep_events_in_sync()
    {
        // Discovery: well-known address, then the user, then the calendar home.
        var (status, _, response) = await SendAsync("PROPFIND", "/.well-known/caldav");
        Assert.Equal(HttpStatusCode.MovedPermanently, status);
        Assert.Equal("/dav/", response.Headers.Location!.OriginalString);

        using (var anonymous = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true })
               { BaseAddress = _http.BaseAddress })
        {
            var unauthorized = await SendAsync("PROPFIND", "/dav/", PropFind(D + "current-user-principal"), "0", anonymous);
            Assert.True(unauthorized.Status == HttpStatusCode.Unauthorized, await unauthorized.Response.Content.ReadAsStringAsync() + string.Join(";", unauthorized.Response.Headers.Select(h => h.Key + "=" + string.Join(",", h.Value))));
            Assert.Contains("Basic", unauthorized.Response.Headers.WwwAuthenticate.ToString());
        }

        var (_, root, _) = await SendAsync("PROPFIND", "/", PropFind(D + "current-user-principal"), "0");
        var principal = root!.Descendants(D + "current-user-principal").Single().Element(D + "href")!.Value;
        Assert.Equal("/dav/principals/alice@example.test/", principal);

        var (_, me, _) = await SendAsync("PROPFIND", principal, PropFind(C + "calendar-home-set", C + "calendar-user-address-set", D + "displayname"), "0");
        var home = me!.Descendants(C + "calendar-home-set").Single().Element(D + "href")!.Value;
        Assert.Contains("mailto:alice@example.test", me.Descendants(C + "calendar-user-address-set").Single().Value);

        var (listStatus, list, _) = await SendAsync("PROPFIND", home, PropFind(D + "resourcetype", D + "displayname", CS + "getctag", D + "sync-token",
            C + "supported-calendar-component-set"), "1");
        Assert.Equal((HttpStatusCode)207, listStatus);
        var calendar = Responses(list).Single(r => r.Descendants(C + "calendar").Any());
        var calendarHref = calendar.Element(D + "href")!.Value;
        Assert.Equal("/dav/calendars/alice@example.test/default/", calendarHref);
        Assert.Equal("Kalender", calendar.Descendants(D + "displayname").Single().Value);

        // Events: create, refuse a second create of the same name, change with the right ETag only.
        var put = await SendAsync("PUT", calendarHref + "termin1.ics", Event("termin-1", "20261020T080000Z", "Zahnarzt"), contentType: "text/calendar",
            headers: ("If-None-Match", "*"));
        Assert.Equal(HttpStatusCode.Created, put.Status);
        var etag = put.Response.Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.PreconditionFailed,
            (await SendAsync("PUT", calendarHref + "termin1.ics", Event("termin-1", "20261020T080000Z", "x"), contentType: "text/calendar", headers: ("If-None-Match", "*"))).Status);
        Assert.Equal(HttpStatusCode.PreconditionFailed,
            (await SendAsync("PUT", calendarHref + "termin1.ics", Event("termin-1", "20261020T080000Z", "x"), contentType: "text/calendar", headers: ("If-Match", "\"falsch\""))).Status);
        var changed = await SendAsync("PUT", calendarHref + "termin1.ics", Event("termin-1", "20261020T090000Z", "Zahnarzt (verschoben)"),
            contentType: "text/calendar", headers: ("If-Match", etag));
        Assert.Equal(HttpStatusCode.NoContent, changed.Status);
        // The same UID under another name is a conflict; broken data is refused.
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync("PUT", calendarHref + "doppelt.ics", Event("termin-1", "20261020T090000Z", "x"), contentType: "text/calendar")).Status);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await SendAsync("PUT", calendarHref + "kaputt.ics", "kein Kalender", contentType: "text/calendar")).Status);
        await SendAsync("PUT", calendarHref + "serie.ics", Event("serie-1", "20260105T070000Z", "Sport", "RRULE:FREQ=WEEKLY"), contentType: "text/calendar");

        var get = await _http.GetAsync(calendarHref + "termin1.ics");
        Assert.Contains("Zahnarzt (verschoben)", await get.Content.ReadAsStringAsync());
        Assert.Equal(changed.Response.Headers.ETag!.Tag, get.Headers.ETag!.Tag);

        // calendar-query with a time range: what the iPhone asks for the coming weeks.
        string Query(string start, string end) => $"""
            <c:calendar-query xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"><d:prop><d:getetag/></d:prop>
            <c:filter><c:comp-filter name="VCALENDAR"><c:comp-filter name="VEVENT"><c:time-range start="{start}" end="{end}"/></c:comp-filter></c:comp-filter></c:filter>
            </c:calendar-query>
            """;
        var october = await SendAsync("REPORT", calendarHref, Query("20261001T000000Z", "20261101T000000Z"), "1");
        Assert.Equal(["serie.ics", "termin1.ics"], Responses(october.Xml).Select(r => r.Element(D + "href")!.Value.Split('/').Last()).Order());
        var before = await SendAsync("REPORT", calendarHref, Query("20250101T000000Z", "20250201T000000Z"), "1");
        Assert.Empty(Responses(before.Xml));

        var multiget = await SendAsync("REPORT", calendarHref, $"""
            <c:calendar-multiget xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"><d:prop><d:getetag/><c:calendar-data/></d:prop>
            <d:href>{calendarHref}termin1.ics</d:href><d:href>{calendarHref}fehlt.ics</d:href></c:calendar-multiget>
            """, "1");
        var found = Responses(multiget.Xml).ToList();
        Assert.Contains("Zahnarzt (verschoben)", found[0].Descendants(C + "calendar-data").Single().Value);
        Assert.Contains("404", found[1].Element(D + "status")!.Value);

        // Sync: everything first, then only what changed since the token.
        const string syncBody = """<d:sync-collection xmlns:d="DAV:"><d:sync-token>{0}</d:sync-token><d:sync-level>1</d:sync-level><d:prop><d:getetag/></d:prop></d:sync-collection>""";
        var first = await SendAsync("REPORT", calendarHref, string.Format(syncBody, ""), "1");
        Assert.Equal(2, Responses(first.Xml).Count());
        var token = first.Xml!.Root!.Element(D + "sync-token")!.Value;
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync("DELETE", calendarHref + "serie.ics")).Status);
        var second = await SendAsync("REPORT", calendarHref, string.Format(syncBody, token), "1");
        var deleted = Assert.Single(Responses(second.Xml));
        Assert.EndsWith("serie.ics", deleted.Element(D + "href")!.Value);
        Assert.Contains("404", deleted.Element(D + "status")!.Value);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync("REPORT", calendarHref, string.Format(syncBody, "http://mailserver/ns/sync/99999"), "1")).Status);

        // A new calendar from the client, renamed and coloured.
        Assert.Equal(HttpStatusCode.Created, (await SendAsync("MKCALENDAR", home + "arbeit/", """
            <c:mkcalendar xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"><d:set><d:prop><d:displayname>Arbeit</d:displayname></d:prop></d:set></c:mkcalendar>
            """)).Status);
        Assert.Equal((HttpStatusCode)207, (await SendAsync("PROPPATCH", home + "arbeit/", """
            <d:propertyupdate xmlns:d="DAV:" xmlns:a="http://apple.com/ns/ical/"><d:set><d:prop><a:calendar-color>#FF0000FF</a:calendar-color><a:calendar-order>2</a:calendar-order></d:prop></d:set></d:propertyupdate>
            """)).Status);
        var (_, after, _) = await SendAsync("PROPFIND", home, PropFind(D + "displayname", XName.Get("calendar-color", "http://apple.com/ns/ical/")), "1");
        Assert.Contains(Responses(after), r => r.Descendants(D + "displayname").Any(n => n.Value == "Arbeit") && r.ToString().Contains("#FF0000FF"));

        // The whole calendar as one file (subscription / export).
        var all = await (await _http.GetAsync(calendarHref)).Content.ReadAsStringAsync();
        Assert.StartsWith("BEGIN:VCALENDAR", all);
        Assert.Contains("Zahnarzt (verschoben)", all);
    }

    [Fact]
    public async Task Only_the_owner_gets_in()
    {
        using var wrong = Client("alice@example.test", "falsch");
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync("PROPFIND", "/dav/", PropFind(D + "current-user-principal"), "0", wrong)).Status);

        using var bob = Client("bob@example.test", TestServer.Password);
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync("PROPFIND", "/dav/calendars/alice@example.test/", PropFind(D + "displayname"), "1", bob)).Status);
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync("PUT", "/dav/calendars/alice@example.test/default/x.ics", Event("x", "20261020T080000Z", "x"), http: bob, contentType: "text/calendar")).Status);

        // A password change ends access with the old password at once (no waiting for the login cache).
        Assert.Equal((HttpStatusCode)207, (await SendAsync("PROPFIND", "/dav/", PropFind(D + "current-user-principal"), "0")).Status);
        _server.HostAccounts.SetPassword(Mailserver.Core.EmailAddress.Parse("alice@example.test"), "ein ganz neues Passwort");
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync("PROPFIND", "/dav/", PropFind(D + "current-user-principal"), "0")).Status);

        var options = await SendAsync("OPTIONS", "/dav/");
        Assert.Contains("calendar-access", options.Response.Headers.GetValues("DAV").Single());
    }

    [Fact]
    public async Task The_calendar_page_shows_which_devices_synced()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("iOS/26.0 (23A341) dataaccessd/1.0");
        await SendAsync("PROPFIND", "/dav/calendars/alice@example.test/", PropFind(D + "displayname"), "1");
        using (var wrong = Client("alice@example.test", "altes Passwort"))
        {
            wrong.DefaultRequestHeaders.UserAgent.ParseAdd("macOS/15.1 (24B83) CalendarAgent/1000");
            await SendAsync("PROPFIND", "/dav/", PropFind(D + "current-user-principal"), "0", wrong);
        }

        using var web = new WebClient(_server.WebPort);
        await web.LoginAsync("alice@example.test", TestServer.Password);
        await web.GetAsync("/Calendar/Calendars");
        Assert.Contains("iPhone/iPad (iOS 26.0)", web.LastPage);
        Assert.Contains("Liste der Kalender (2 Einträge)", web.LastPage);
        Assert.Contains("Mac (macOS 15.1)", web.LastPage);
        Assert.Contains("falsches Passwort", web.LastPage);

        Assert.Equal("DAVx⁵ (Android)", Mailserver.Web.Dav.DavActivity.ClientName("DAVx5/4.4.2-ose (2024/10/01; dav4jvm; okhttp/4.12.0) Android/14"));
        Assert.Equal("Thunderbird", Mailserver.Web.Dav.DavActivity.ClientName("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:128.0) Gecko/20100101 Thunderbird/128.3.0"));
    }
}
