using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Mailserver.Core;

namespace Mailserver.Migration;

public sealed record DavCollectionExport(string Kind, string Name, string File, int Items);

public sealed record DavExportResult(bool ServerFound, IReadOnlyList<DavCollectionExport> Collections, IReadOnlyList<string> Warnings)
{
    public int Contacts => Collections.Where(c => c.Kind == DavExporter.Contacts).Sum(c => c.Items);
    public int CalendarItems => Collections.Where(c => c.Kind == DavExporter.Calendars).Sum(c => c.Items);
}

/// <summary>
/// Saves address books (CardDAV) as .vcf and calendars/task lists (CalDAV) as .ics below
/// &lt;target&gt;/&lt;address&gt;/Kontakte and …/Kalender. The server is found through the usual discovery
/// (/.well-known, current-user-principal, home sets), so only the web address of the old server is needed.
/// </summary>
public sealed class DavExporter
{
    public const string Contacts = "Kontakte";
    public const string Calendars = "Kalender";

    private const int MaxRedirects = 5;
    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace CardDav = "urn:ietf:params:xml:ns:carddav";
    private static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";

    private readonly HttpMessageHandler? _handler;

    public DavExporter(HttpMessageHandler? handler = null) => _handler = handler;

    public async Task<DavExportResult> ExportAsync(Uri server, EmailAddress address, string password, string targetDirectory,
        bool acceptInvalidCertificate = false, Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        using var http = CreateClient(acceptInvalidCertificate);
        var auth = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{address}:{password}")));
        var session = new Session(http, auth, cancellationToken);
        var warnings = new List<string>();

        var principal = await FindPrincipalAsync(session, server);
        if (principal is null)
        {
            return new DavExportResult(false, [], [$"Kein CalDAV/CardDAV-Zugang unter {server} gefunden."]);
        }

        var homes = await session.PropFindAsync(principal, 0,
            new XElement(CardDav + "addressbook-home-set"), new XElement(CalDav + "calendar-home-set"));
        var props = homes?.Responses.FirstOrDefault().Props;
        var accountDirectory = MailboxExporter.AccountDirectory(targetDirectory, address);
        var results = new List<DavCollectionExport>();
        foreach (var (kind, homeProperty, type) in new[]
                 {
                     (Contacts, CardDav + "addressbook-home-set", CardDav + "addressbook"),
                     (Calendars, CalDav + "calendar-home-set", CalDav + "calendar"),
                 })
        {
            var homeHref = props?.Element(homeProperty)?.Element(Dav + "href")?.Value;
            if (homeHref is null)
            {
                warnings.Add($"{kind}: der Server meldet keine Sammlung.");
                continue;
            }

            var home = new Uri(homes!.Uri, homeHref);
            var listing = await session.PropFindAsync(home, 1, new XElement(Dav + "resourcetype"), new XElement(Dav + "displayname"));
            if (listing is null)
            {
                warnings.Add($"{kind}: {home} lässt sich nicht auflisten.");
                continue;
            }

            var directory = Path.Combine(accountDirectory, kind);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (href, collectionProps) in listing.Responses)
            {
                if (collectionProps.Element(Dav + "resourcetype")?.Element(type) is null)
                {
                    continue;
                }

                var uri = new Uri(listing.Uri, href);
                var name = collectionProps.Element(Dav + "displayname")?.Value is { Length: > 0 } displayName
                    ? displayName
                    : Uri.UnescapeDataString(uri.AbsolutePath.TrimEnd('/').Split('/')[^1]);
                var items = kind == Contacts ? await FetchContactsAsync(session, uri) : await FetchCalendarAsync(session, uri);
                if (items is null)
                {
                    warnings.Add($"{kind} \"{name}\" ließ sich nicht abrufen.");
                    continue;
                }

                Directory.CreateDirectory(directory);
                var extension = kind == Contacts ? ".vcf" : ".ics";
                var file = ExportNames.Unique(ExportNames.Sanitize(name, 80), f => !used.Add(f), extension);
                var content = kind == Contacts ? JoinVCards(items) : MergeCalendars(items);
                await File.WriteAllTextAsync(Path.Combine(directory, file), content, new UTF8Encoding(false), cancellationToken);
                results.Add(new DavCollectionExport(kind, name, Path.Combine(kind, file), items.Count));
                progress?.Invoke($"  {kind} \"{name}\" -> {kind}/{file}: {items.Count} Einträge");
            }
        }

        return new DavExportResult(true, results, warnings);
    }

    private HttpClient CreateClient(bool acceptInvalidCertificate)
    {
        if (_handler is not null)
        {
            return new HttpClient(_handler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(5) };
        }

        var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        if (acceptInvalidCertificate)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        return new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
    }

    private static async Task<Uri?> FindPrincipalAsync(Session session, Uri server)
    {
        var root = new Uri(server.GetLeftPart(UriPartial.Authority) + "/");
        var candidates = server.AbsolutePath.Length > 1
            ? new[] { server }
            : new[] { new Uri(root, ".well-known/caldav"), new Uri(root, ".well-known/carddav"), new Uri(root, "webdav/"), root };
        foreach (var candidate in candidates)
        {
            var response = await session.PropFindAsync(candidate, 0, new XElement(Dav + "current-user-principal"));
            if (response?.Responses.Select(r => r.Props.Element(Dav + "current-user-principal")?.Element(Dav + "href")?.Value)
                    .FirstOrDefault(h => h is not null) is { } href)
            {
                return new Uri(response.Uri, href);
            }
        }

        return null;
    }

    private static async Task<List<string>?> FetchContactsAsync(Session session, Uri collection)
    {
        var query = new XElement(CardDav + "addressbook-query",
            new XElement(Dav + "prop", new XElement(Dav + "getetag"), new XElement(CardDav + "address-data")));
        return await QueryAsync(session, collection, query, CardDav + "address-data", "text/vcard");
    }

    private static async Task<List<string>?> FetchCalendarAsync(Session session, Uri collection)
    {
        var query = new XElement(CalDav + "calendar-query",
            new XElement(Dav + "prop", new XElement(Dav + "getetag"), new XElement(CalDav + "calendar-data")),
            new XElement(CalDav + "filter", new XElement(CalDav + "comp-filter", new XAttribute("name", "VCALENDAR"))));
        return await QueryAsync(session, collection, query, CalDav + "calendar-data", "text/calendar");
    }

    /// <summary>REPORT with the data of all entries; servers without REPORT support are read entry by entry.</summary>
    private static async Task<List<string>?> QueryAsync(Session session, Uri collection, XElement query, XName dataElement, string contentType)
    {
        var report = await session.SendAsync("REPORT", collection, 1, query);
        if (report is not null)
        {
            return report.Responses
                .Select(r => r.Props.Element(dataElement)?.Value)
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(d => d!)
                .ToList();
        }

        var listing = await session.PropFindAsync(collection, 1, new XElement(Dav + "resourcetype"), new XElement(Dav + "getcontenttype"));
        if (listing is null)
        {
            return null;
        }

        var items = new List<string>();
        foreach (var (href, props) in listing.Responses)
        {
            var uri = new Uri(listing.Uri, href);
            if (props.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null || uri.AbsolutePath.TrimEnd('/') == listing.Uri.AbsolutePath.TrimEnd('/'))
            {
                continue;
            }

            var type = props.Element(Dav + "getcontenttype")?.Value;
            if (type is not null && !type.StartsWith(contentType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (await session.GetAsync(uri) is { } item)
            {
                items.Add(item);
            }
        }

        return items;
    }

    public static string JoinVCards(IEnumerable<string> cards) =>
        string.Concat(cards.Select(c => NormalizeLineEndings(c).TrimEnd('\r', '\n') + "\r\n"));

    /// <summary>
    /// Combines single iCalendar objects into one VCALENDAR: every event, task and journal entry, and each time zone once.
    /// </summary>
    public static string MergeCalendars(IEnumerable<string> calendars)
    {
        var timeZones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var components = new List<string>();
        foreach (var calendar in calendars)
        {
            var lines = NormalizeLineEndings(calendar).Split("\r\n");
            var depth = 0;
            StringBuilder? current = null;
            string? tzid = null;
            foreach (var line in lines)
            {
                if (line.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase))
                {
                    depth++;
                    if (depth == 2)
                    {
                        current = new StringBuilder();
                        tzid = null;
                    }
                }

                if (depth >= 2 && current is not null)
                {
                    current.Append(line).Append("\r\n");
                    if (depth == 2 && line.StartsWith("TZID:", StringComparison.OrdinalIgnoreCase))
                    {
                        tzid = line[5..].Trim();
                    }
                }

                if (line.StartsWith("END:", StringComparison.OrdinalIgnoreCase))
                {
                    if (depth == 2 && current is not null)
                    {
                        var text = current.ToString();
                        if (line.Equals("END:VTIMEZONE", StringComparison.OrdinalIgnoreCase))
                        {
                            timeZones.TryAdd(tzid ?? text, text);
                        }
                        else
                        {
                            components.Add(text);
                        }

                        current = null;
                    }

                    depth--;
                }
            }
        }

        var result = new StringBuilder("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Mailserver//Export//DE\r\nCALSCALE:GREGORIAN\r\n");
        foreach (var part in timeZones.Values.Concat(components))
        {
            result.Append(part);
        }

        return result.Append("END:VCALENDAR\r\n").ToString();
    }

    private static string NormalizeLineEndings(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");

    private sealed record MultiStatus(Uri Uri, IReadOnlyList<(string Href, XElement Props)> Responses);

    private sealed class Session(HttpClient http, AuthenticationHeaderValue auth, CancellationToken cancellationToken)
    {
        public Task<MultiStatus?> PropFindAsync(Uri uri, int depth, params XElement[] properties) =>
            SendAsync("PROPFIND", uri, depth, new XElement(Dav + "propfind", new XElement(Dav + "prop", properties)));

        /// <summary>Sends a WebDAV request; returns null unless the server answers 207 Multi-Status.</summary>
        public async Task<MultiStatus?> SendAsync(string method, Uri uri, int depth, XElement body)
        {
            var (response, finalUri) = await SendWithRedirectsAsync(uri, () =>
            {
                var content = new StringContent(new XDocument(new XDeclaration("1.0", "utf-8", null), body).ToString(), Encoding.UTF8, "application/xml");
                var request = new HttpRequestMessage(new HttpMethod(method), uri) { Content = content };
                request.Headers.Add("Depth", depth.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return request;
            });
            using var disposable = response;
            if (response is null || response.StatusCode != (HttpStatusCode)207)
            {
                return null;
            }

            XDocument document;
            try
            {
                document = XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            }
            catch (System.Xml.XmlException)
            {
                return null;
            }

            var responses = new List<(string, XElement)>();
            foreach (var item in document.Descendants(Dav + "response"))
            {
                var href = item.Element(Dav + "href")?.Value;
                if (href is null)
                {
                    continue;
                }

                // Only properties the server actually has (status 200); 404 entries list what is missing.
                var props = new XElement(Dav + "prop", item.Elements(Dav + "propstat")
                    .Where(p => p.Element(Dav + "status")?.Value.Contains(" 200 ") ?? true)
                    .SelectMany(p => p.Element(Dav + "prop")?.Elements() ?? []));
                responses.Add((href.Trim(), props));
            }

            return new MultiStatus(finalUri, responses);
        }

        public async Task<string?> GetAsync(Uri uri)
        {
            var (response, _) = await SendWithRedirectsAsync(uri, () => new HttpRequestMessage(HttpMethod.Get, uri));
            using var disposable = response;
            return response is { IsSuccessStatusCode: true } ? await response.Content.ReadAsStringAsync(cancellationToken) : null;
        }

        /// <summary>Follows redirects (e.g. from /.well-known) itself, so the credentials are sent to the new address too.</summary>
        private async Task<(HttpResponseMessage? Response, Uri Uri)> SendWithRedirectsAsync(Uri uri, Func<HttpRequestMessage> create)
        {
            for (var i = 0; i <= MaxRedirects; i++)
            {
                using var request = create();
                request.RequestUri = uri;
                request.Headers.Authorization = auth;
                HttpResponseMessage response;
                try
                {
                    response = await http.SendAsync(request, cancellationToken);
                }
                catch (HttpRequestException)
                {
                    return (null, uri);
                }

                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
                {
                    uri = new Uri(uri, location);
                    response.Dispose();
                    continue;
                }

                return (response, uri);
            }

            return (null, uri);
        }
    }
}
