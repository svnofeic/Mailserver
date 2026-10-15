using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Mailserver.Core.Accounts;
using Mailserver.Core.Dav;
using Mailserver.Core.Security;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace Mailserver.Web.Dav;

/// <summary>
/// CalDAV (RFC 4791) and CardDAV (RFC 6352) on top of WebDAV (RFC 4918), with sync-collection (RFC 6578): calendars and
/// address books for the iPhone, Mac, Thunderbird, DAVx⁵ (Android) and Outlook (with a CalDAV add-in). Login with the
/// address and password of the mailbox, as for IMAP.
/// <code>
/// /dav/                                  root
/// /dav/principals/{address}/             the user
/// /dav/calendars/{address}/              calendar home: one collection per calendar
/// /dav/calendars/{address}/{slug}/{name} an event or task (.ics)
/// /dav/addressbooks/{address}/{slug}/…   the same for address books (.vcf)
/// </code>
/// </summary>
public sealed class DavHandler(DavStore store, AccountStore accounts, AuthThrottle throttle, Mailserver.Core.SpamLogging.SpamLog spamLog,
    ILogger<DavHandler> logger)
{
    public const string Root = "/dav/";
    public static readonly string[] Methods = ["OPTIONS", "PROPFIND", "PROPPATCH", "REPORT", "GET", "HEAD", "PUT", "DELETE", "MKCALENDAR", "MKCOL"];

    private const long MaxBody = 4 * 1024 * 1024;
    private static readonly TimeSpan AuthCacheTime = TimeSpan.FromMinutes(10);

    private static readonly XNamespace D = "DAV:";
    private static readonly XNamespace C = "urn:ietf:params:xml:ns:caldav";
    private static readonly XNamespace CR = "urn:ietf:params:xml:ns:carddav";
    private static readonly XNamespace CS = "http://calendarserver.org/ns/";
    private static readonly XNamespace A = "http://apple.com/ns/ical/";

    // Clients send the password with every request; checking the hash each time would be slow (it is slow on purpose).
    // The security stamp changes with the password, so an old password stops working at once.
    private readonly ConcurrentDictionary<string, (long AccountId, string? Stamp, DateTimeOffset Until)> _authCache = new();

    private enum Kind { Root, Principal, Home, Collection, Object }

    private sealed record Target(Kind Kind, string? Type = null, DavCollection? Collection = null, string? Name = null, DavObject? Object = null);

    public async Task HandleAsync(HttpContext context)
    {
        // DAV answers are XML or empty, never the HTML error page.
        if (context.Features.Get<IStatusCodePagesFeature>() is { } statusPages)
        {
            statusPages.Enabled = false;
        }

        var request = context.Request;
        var response = context.Response;
        response.Headers["DAV"] = "1, 3, access-control, calendar-access, addressbook, extended-mkcol";
        if (request.Method == "OPTIONS")
        {
            response.Headers.Allow = string.Join(", ", Methods);
            response.StatusCode = StatusCodes.Status200OK;
            return;
        }

        if (Authenticate(context) is not { } account)
        {
            return;
        }

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxBody;
        }

        var path = request.Path.Value ?? "/";
        if (path == "/" || path == "/dav")
        {
            path = Root;
        }

        if (Resolve(account, path) is not { } target)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        try
        {
            switch (request.Method)
            {
                case "PROPFIND":
                    await PropFindAsync(context, account, target);
                    break;
                case "PROPPATCH":
                    await PropPatchAsync(context, account, target);
                    break;
                case "REPORT":
                    await ReportAsync(context, account, target);
                    break;
                case "GET" or "HEAD":
                    await GetAsync(context, target);
                    break;
                case "PUT":
                    await PutAsync(context, account, target);
                    break;
                case "DELETE":
                    Delete(context, target);
                    break;
                case "MKCALENDAR" or "MKCOL":
                    await MakeCollectionAsync(context, account, target);
                    break;
                default:
                    response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                    break;
            }
        }
        catch (XmlException)
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        }
    }

    // ---- Login ----

    private Account? Authenticate(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        if (throttle.IsLockedOut(ip))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return null;
        }

        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            string decoded;
            try
            {
                decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
            }
            catch (FormatException)
            {
                decoded = "";
            }

            var colon = decoded.IndexOf(':');
            if (colon > 0)
            {
                var user = decoded[..colon];
                var password = decoded[(colon + 1)..];
                var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user.ToLowerInvariant() + "\n" + password)));
                if (_authCache.TryGetValue(key, out var cached) && cached.Until > DateTimeOffset.UtcNow &&
                    accounts.FindAccount(cached.AccountId) is { Enabled: true } known && accounts.GetSecurityStamp(known.Id) == cached.Stamp)
                {
                    return known;
                }

                if (accounts.Authenticate(user, password) is { } account)
                {
                    throttle.RecordSuccess(ip);
                    if (_authCache.Count > 1000)
                    {
                        _authCache.Clear();
                    }

                    _authCache[key] = (account.Id, accounts.GetSecurityStamp(account.Id), DateTimeOffset.UtcNow + AuthCacheTime);
                    return account;
                }

                var lockedOut = throttle.RecordFailure(ip);
                logger.LogWarning("Failed calendar/contacts login for {User} from {Ip}", user, ip);
                spamLog.WriteAuthFailure("CalDAV", user, ip, lockedOut);
            }
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Basic realm=\"Mailserver\", charset=\"UTF-8\"";
        return null;
    }

    // ---- Addresses ----

    /// <summary>The address to enter in calendar and contacts programs, e.g. "https://mail.example.com:9443/dav/".</summary>
    public static string ServerUrl(Mailserver.Core.MailserverOptions options)
    {
        var port = options.Web.HttpsPort;
        return port > 0
            ? $"https://{options.Hostname}{(port == 443 ? "" : ":" + port.ToString(CultureInfo.InvariantCulture))}{Root}"
            : $"http://{options.Hostname}:{options.Web.InsecureHttpPort.ToString(CultureInfo.InvariantCulture)}{Root}";
    }

    private static string UserSegment(Account account) => Uri.EscapeDataString(account.Address.ToString()).Replace("%40", "@");

    private static string PrincipalHref(Account account) => $"{Root}principals/{UserSegment(account)}/";

    private static string HomeHref(Account account, string type) =>
        $"{Root}{(type == DavStore.Calendar ? "calendars" : "addressbooks")}/{UserSegment(account)}/";

    private static string CollectionHref(Account account, DavCollection collection) =>
        HomeHref(account, collection.Kind) + Uri.EscapeDataString(collection.Slug) + "/";

    private static string ObjectHref(Account account, DavCollection collection, string name) =>
        CollectionHref(account, collection) + Uri.EscapeDataString(name);

    private Target? Resolve(Account account, string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();
        if (segments.Count == 0 || segments[0] != "dav")
        {
            return null;
        }

        segments.RemoveAt(0);
        if (segments.Count == 0)
        {
            return new Target(Kind.Root);
        }

        var type = segments[0] switch
        {
            "principals" => "principal",
            "calendars" => DavStore.Calendar,
            "addressbooks" => DavStore.AddressBook,
            _ => null,
        };
        if (type is null)
        {
            return null;
        }

        if (segments.Count == 1)
        {
            // "/dav/principals/": where clients look for the user.
            return type == "principal" ? new Target(Kind.Root) : null;
        }

        // Only one's own data.
        if (!string.Equals(segments[1], account.Address.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (type == "principal")
        {
            return segments.Count == 2 ? new Target(Kind.Principal) : null;
        }

        if (segments.Count == 2)
        {
            return new Target(Kind.Home, type);
        }

        var collection = store.Collection(account.Id, type, segments[2]);
        if (segments.Count == 3)
        {
            return new Target(Kind.Collection, type, collection, segments[2]);
        }

        if (segments.Count == 4 && collection is not null)
        {
            return new Target(Kind.Object, type, collection, segments[3], store.Object(collection.Id, segments[3]));
        }

        return null;
    }

    // ---- PROPFIND / PROPPATCH ----

    private async Task PropFindAsync(HttpContext context, Account account, Target target)
    {
        if (target is { Kind: Kind.Collection, Collection: null } or { Kind: Kind.Object, Object: null })
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var body = await ReadXmlAsync(context);
        var (names, allProps) = RequestedProperties(body?.Root);
        var depth = context.Request.Headers["Depth"].ToString();
        var responses = new List<XElement> { Response(account, target, names, allProps) };
        if (depth is "1" or "infinity")
        {
            foreach (var child in Children(account, target))
            {
                responses.Add(Response(account, child, names, allProps));
            }
        }

        await WriteMultiStatusAsync(context, responses);
    }

    private IEnumerable<Target> Children(Account account, Target target)
    {
        switch (target.Kind)
        {
            case Kind.Home:
                foreach (var collection in store.Collections(account.Id, target.Type!))
                {
                    yield return new Target(Kind.Collection, target.Type, collection, collection.Slug);
                }

                break;
            case Kind.Collection:
                foreach (var item in store.Objects(target.Collection!.Id))
                {
                    yield return new Target(Kind.Object, target.Type, target.Collection, item.Href, item);
                }

                break;
        }
    }

    private async Task PropPatchAsync(HttpContext context, Account account, Target target)
    {
        var body = await ReadXmlAsync(context);
        if (body?.Root is null || target.Kind != Kind.Collection || target.Collection is null)
        {
            context.Response.StatusCode = target.Kind == Kind.Collection ? StatusCodes.Status400BadRequest : StatusCodes.Status403Forbidden;
            return;
        }

        var props = body.Root.Elements(D + "set").Elements(D + "prop").Elements().ToList();
        var removed = body.Root.Elements(D + "remove").Elements(D + "prop").Elements().ToList();
        ApplyCollectionProperties(target.Collection, props);
        // Properties we do not keep (such as the order of calendars) are acknowledged, so clients do not show an error.
        var answered = props.Concat(removed).Select(p => new XElement(p.Name)).ToList();
        await WriteMultiStatusAsync(context,
            [new XElement(D + "response", new XElement(D + "href", CollectionHref(account, target.Collection)), PropStat(answered, 200))]);
    }

    private void ApplyCollectionProperties(DavCollection collection, IEnumerable<XElement> props)
    {
        string? name = null, color = null, description = null;
        foreach (var prop in props)
        {
            if (prop.Name == D + "displayname")
            {
                name = prop.Value;
            }
            else if (prop.Name == A + "calendar-color")
            {
                color = prop.Value.Trim();
            }
            else if (prop.Name == C + "calendar-description" || prop.Name == CR + "addressbook-description")
            {
                description = prop.Value;
            }
        }

        if (name is not null || color is not null || description is not null)
        {
            store.UpdateCollection(collection.Id, name, color is { Length: > 0 and <= 20 } ? color : null, description);
        }
    }

    private static (IReadOnlyList<XName> Names, bool All) RequestedProperties(XElement? root)
    {
        if (root is null || root.Element(D + "allprop") is not null)
        {
            return ([], true);
        }

        if (root.Element(D + "propname") is not null)
        {
            return ([], true);
        }

        return (root.Element(D + "prop")?.Elements().Select(e => e.Name).ToList() ?? [], false);
    }

    private XElement Response(Account account, Target target, IReadOnlyList<XName> names, bool allProps, XElement? propRequest = null)
    {
        var href = Href(account, target);
        var requested = allProps ? AllProperties(target) : names;
        var found = new List<XElement>();
        var missing = new List<XElement>();
        foreach (var name in requested)
        {
            if (Property(account, target, name, propRequest) is { } value)
            {
                found.Add(value);
            }
            else
            {
                missing.Add(new XElement(name));
            }
        }

        var response = new XElement(D + "response", new XElement(D + "href", href));
        if (found.Count > 0 || missing.Count == 0)
        {
            response.Add(PropStat(found, 200));
        }

        if (missing.Count > 0)
        {
            response.Add(PropStat(missing, 404));
        }

        return response;
    }

    private static string Href(Account account, Target target) => target.Kind switch
    {
        Kind.Root => Root,
        Kind.Principal => PrincipalHref(account),
        Kind.Home => HomeHref(account, target.Type!),
        Kind.Collection => CollectionHref(account, target.Collection!),
        _ => ObjectHref(account, target.Collection!, target.Name!),
    };

    private static IReadOnlyList<XName> AllProperties(Target target) => target.Kind switch
    {
        Kind.Object => [D + "resourcetype", D + "getetag", D + "getcontenttype", D + "getcontentlength", D + "getlastmodified"],
        Kind.Collection => [D + "resourcetype", D + "displayname", D + "owner", CS + "getctag", D + "sync-token"],
        _ => [D + "resourcetype", D + "displayname", D + "current-user-principal"],
    };

    private XElement? Property(Account account, Target target, XName name, XElement? propRequest)
    {
        var principal = new XElement(D + "href", PrincipalHref(account));
        var collection = target.Collection;
        var isCalendar = target.Type == DavStore.Calendar;

        if (name == D + "current-user-principal" || name == D + "principal-URL" && target.Kind == Kind.Principal)
        {
            return new XElement(name, principal);
        }

        if (name == D + "owner" && target.Kind is not Kind.Root)
        {
            return new XElement(name, principal);
        }

        if (name == D + "principal-collection-set")
        {
            return new XElement(name, new XElement(D + "href", Root + "principals/"));
        }

        if (name == C + "calendar-home-set" && target.Kind is Kind.Principal or Kind.Root)
        {
            return new XElement(name, new XElement(D + "href", HomeHref(account, DavStore.Calendar)));
        }

        if (name == CR + "addressbook-home-set" && target.Kind is Kind.Principal or Kind.Root)
        {
            return new XElement(name, new XElement(D + "href", HomeHref(account, DavStore.AddressBook)));
        }

        if (name == C + "calendar-user-address-set" && target.Kind == Kind.Principal)
        {
            return new XElement(name, new XElement(D + "href", "mailto:" + account.Address), principal);
        }

        if (name == D + "current-user-privilege-set")
        {
            return new XElement(name, new[] { "read", "write", "write-properties", "write-content", "bind", "unbind", "read-current-user-privilege-set", "all" }
                .Select(p => new XElement(D + "privilege", new XElement(D + p)))
                .Append(new XElement(D + "privilege", new XElement(C + "read-free-busy"))));
        }

        if (name == D + "resourcetype")
        {
            return target.Kind switch
            {
                Kind.Root or Kind.Home => new XElement(name, new XElement(D + "collection")),
                Kind.Principal => new XElement(name, new XElement(D + "collection"), new XElement(D + "principal")),
                Kind.Collection => new XElement(name, new XElement(D + "collection"), new XElement(isCalendar ? C + "calendar" : CR + "addressbook")),
                _ => new XElement(name),
            };
        }

        if (name == D + "displayname")
        {
            return target.Kind switch
            {
                Kind.Principal => new XElement(name, account.Address.ToString()),
                Kind.Home => new XElement(name, isCalendar ? "Kalender" : "Adressbücher"),
                Kind.Collection => new XElement(name, collection!.Name),
                Kind.Root => new XElement(name, "Mailserver"),
                _ => null,
            };
        }

        if (name == D + "supported-report-set" && target.Kind is Kind.Collection or Kind.Home or Kind.Principal)
        {
            var reports = target.Kind == Kind.Collection
                ? isCalendar ? new[] { C + "calendar-multiget", C + "calendar-query", D + "sync-collection" } : [CR + "addressbook-multiget", CR + "addressbook-query", D + "sync-collection"]
                : [D + "expand-property"];
            return new XElement(name, reports.Select(r => new XElement(D + "supported-report", new XElement(D + "report", new XElement(r)))));
        }

        if (target.Kind == Kind.Collection && collection is not null)
        {
            if (name == CS + "getctag")
            {
                return new XElement(name, collection.SyncRev.ToString(CultureInfo.InvariantCulture));
            }

            if (name == D + "sync-token")
            {
                return new XElement(name, SyncToken(collection.SyncRev));
            }

            if (isCalendar && name == C + "supported-calendar-component-set")
            {
                return new XElement(name, new XElement(C + "comp", new XAttribute("name", "VEVENT")), new XElement(C + "comp", new XAttribute("name", "VTODO")));
            }

            if (isCalendar && name == C + "supported-calendar-data")
            {
                return new XElement(name, new XElement(C + "calendar-data", new XAttribute("content-type", "text/calendar"), new XAttribute("version", "2.0")));
            }

            if (!isCalendar && name == CR + "supported-address-data")
            {
                return new XElement(name,
                    new XElement(CR + "address-data-type", new XAttribute("content-type", "text/vcard"), new XAttribute("version", "3.0")),
                    new XElement(CR + "address-data-type", new XAttribute("content-type", "text/vcard"), new XAttribute("version", "4.0")));
            }

            if (isCalendar && name == C + "calendar-description" || !isCalendar && name == CR + "addressbook-description")
            {
                return new XElement(name, collection.Description ?? "");
            }

            if (isCalendar && name == A + "calendar-color" && collection.Color is not null)
            {
                return new XElement(name, collection.Color);
            }

            if (name == C + "max-resource-size" && isCalendar || name == CR + "max-resource-size" && !isCalendar)
            {
                return new XElement(name, (isCalendar ? CalendarData.MaxBytes : VCardData.MaxBytes).ToString(CultureInfo.InvariantCulture));
            }
        }

        if (target.Kind == Kind.Object && target.Object is { } item)
        {
            if (name == D + "getetag")
            {
                return new XElement(name, item.ETag);
            }

            if (name == D + "getcontenttype")
            {
                return new XElement(name, ContentType(target.Type!, item));
            }

            if (name == D + "getcontentlength")
            {
                return new XElement(name, Encoding.UTF8.GetByteCount(item.Data).ToString(CultureInfo.InvariantCulture));
            }

            if (name == D + "getlastmodified")
            {
                return new XElement(name, item.Modified.ToString("R", CultureInfo.InvariantCulture));
            }

            if (isCalendar && name == C + "calendar-data" || !isCalendar && name == CR + "address-data")
            {
                return new XElement(name, item.Data);
            }
        }

        return null;
    }

    private static string ContentType(string type, DavObject item) => type == DavStore.Calendar
        ? $"text/calendar; charset=utf-8; component={item.Component?.ToLowerInvariant() ?? "vevent"}"
        : "text/vcard; charset=utf-8";

    // ---- REPORT ----

    private async Task ReportAsync(HttpContext context, Account account, Target target)
    {
        var body = await ReadXmlAsync(context);
        var report = body?.Root;
        if (report is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var (names, allProps) = RequestedProperties(report);
        var propRequest = report.Element(D + "prop");
        if (report.Name == D + "expand-property")
        {
            // Delegation (proxy calendars) is not offered: an empty answer.
            await WriteMultiStatusAsync(context, []);
            return;
        }

        if (target.Kind != Kind.Collection || target.Collection is null)
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, new XElement(D + "supported-report"));
            return;
        }

        var collection = target.Collection;
        IEnumerable<Target> Items(IEnumerable<DavObject> objects) =>
            objects.Select(o => new Target(Kind.Object, target.Type, collection, o.Href, o));

        if (report.Name == C + "calendar-multiget" || report.Name == CR + "addressbook-multiget")
        {
            var responses = new List<XElement>();
            foreach (var href in report.Elements(D + "href").Select(h => h.Value.Trim()))
            {
                var name = ObjectName(account, collection, href);
                if (name is not null && store.Object(collection.Id, name) is { } item)
                {
                    responses.Add(Response(account, new Target(Kind.Object, target.Type, collection, name, item), names, allProps, propRequest));
                }
                else
                {
                    responses.Add(new XElement(D + "response", new XElement(D + "href", href), new XElement(D + "status", "HTTP/1.1 404 Not Found")));
                }
            }

            await WriteMultiStatusAsync(context, responses);
            return;
        }

        if (report.Name == C + "calendar-query")
        {
            var (component, from, to, uid) = CalendarFilter(report.Element(C + "filter"));
            var objects = store.ObjectsInRange(collection.Id, from, to, component).Where(o => uid is null || o.Uid == uid);
            await WriteMultiStatusAsync(context, Items(objects).Select(t => Response(account, t, names, allProps, propRequest)).ToList());
            return;
        }

        if (report.Name == CR + "addressbook-query")
        {
            // Filters (by name or address) are applied by the clients themselves; they get all contacts.
            var limit = int.TryParse(report.Element(CR + "limit")?.Element(CR + "nresults")?.Value, out var n) && n > 0 ? n : int.MaxValue;
            await WriteMultiStatusAsync(context, Items(store.Objects(collection.Id).Take(limit)).Select(t => Response(account, t, names, allProps, propRequest)).ToList());
            return;
        }

        if (report.Name == D + "sync-collection")
        {
            var token = report.Element(D + "sync-token")?.Value.Trim() ?? "";
            long since = 0;
            if (token.Length > 0 && (ParseSyncToken(token) is not { } parsed || parsed > collection.SyncRev))
            {
                await WriteErrorAsync(context, StatusCodes.Status403Forbidden, new XElement(D + "valid-sync-token"));
                return;
            }
            else if (token.Length > 0)
            {
                since = ParseSyncToken(token)!.Value;
            }

            var (changed, deleted, rev) = store.Changes(collection.Id, since);
            var responses = Items(changed).Select(t => Response(account, t, names, allProps, propRequest)).ToList();
            responses.AddRange(deleted.Select(name => new XElement(D + "response", new XElement(D + "href", ObjectHref(account, collection, name)),
                new XElement(D + "status", "HTTP/1.1 404 Not Found"))));
            await WriteMultiStatusAsync(context, responses, new XElement(D + "sync-token", SyncToken(rev)));
            return;
        }

        await WriteErrorAsync(context, StatusCodes.Status403Forbidden, new XElement(D + "supported-report"));
    }

    /// <summary>
    /// The parts of a calendar-query filter we use: the component (VEVENT/VTODO), a time range and a UID. Anything else is
    /// left to the client (answering with more than asked is harmless for it).
    /// </summary>
    private static (string? Component, DateTimeOffset? From, DateTimeOffset? To, string? Uid) CalendarFilter(XElement? filter)
    {
        var calendar = filter?.Element(C + "comp-filter");
        var inner = calendar?.Element(C + "comp-filter");
        var component = inner?.Attribute("name")?.Value.ToUpperInvariant();
        var range = inner?.Element(C + "time-range");
        DateTimeOffset? Parse(string? value) =>
            value is not null && DateTime.TryParseExact(value, ["yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd'T'HHmmss"], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? new DateTimeOffset(parsed, TimeSpan.Zero)
                : null;
        var uidFilter = inner?.Elements(C + "prop-filter").FirstOrDefault(p => string.Equals(p.Attribute("name")?.Value, "UID", StringComparison.OrdinalIgnoreCase));
        var uid = uidFilter?.Element(C + "text-match")?.Value.Trim();
        return (component is "VEVENT" or "VTODO" or "VJOURNAL" ? component : null, Parse(range?.Attribute("start")?.Value),
            Parse(range?.Attribute("end")?.Value), string.IsNullOrEmpty(uid) ? null : uid);
    }

    private string? ObjectName(Account account, DavCollection collection, string href)
    {
        var path = Uri.TryCreate(href, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https" ? absolute.AbsolutePath : href;
        var prefix = CollectionHref(account, collection);
        var decoded = Uri.UnescapeDataString(path);
        var decodedPrefix = Uri.UnescapeDataString(prefix);
        if (!decoded.StartsWith(decodedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = decoded[decodedPrefix.Length..];
        return name.Length > 0 && !name.Contains('/') ? name : null;
    }

    private static string SyncToken(long rev) => $"http://mailserver/ns/sync/{rev}";

    private static long? ParseSyncToken(string token)
    {
        var last = token.TrimEnd('/').Split('/').Last();
        return long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var rev) ? rev : null;
    }

    // ---- GET / PUT / DELETE ----

    private async Task GetAsync(HttpContext context, Target target)
    {
        var response = context.Response;
        if (target.Kind == Kind.Collection && target.Collection is { } collection)
        {
            // The whole calendar as one file (subscriptions, export) or all contacts as one .vcf.
            var objects = store.Objects(collection.Id);
            var text = target.Type == DavStore.Calendar ? MergeCalendars(objects.Select(o => o.Data), collection.Name) : string.Concat(objects.Select(o => o.Data.TrimEnd() + "\r\n"));
            response.ContentType = target.Type == DavStore.Calendar ? "text/calendar; charset=utf-8" : "text/vcard; charset=utf-8";
            if (context.Request.Method == "GET")
            {
                await response.WriteAsync(text);
            }

            return;
        }

        if (target is not { Kind: Kind.Object, Object: { } item })
        {
            response.StatusCode = target.Kind == Kind.Object ? StatusCodes.Status404NotFound : StatusCodes.Status405MethodNotAllowed;
            return;
        }

        response.Headers.ETag = item.ETag;
        response.Headers.LastModified = item.Modified.ToString("R", CultureInfo.InvariantCulture);
        response.ContentType = ContentType(target.Type!, item);
        if (context.Request.Headers.IfNoneMatch.ToString() == item.ETag)
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        if (context.Request.Method == "GET")
        {
            await response.WriteAsync(item.Data);
        }
    }

    /// <summary>Several calendar objects as one calendar: the components of all, each time zone once.</summary>
    public static string MergeCalendars(IEnumerable<string> calendars, string name)
    {
        var builder = new StringBuilder("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Mailserver//Kalender//DE\r\nX-WR-CALNAME:")
            .Append(name.Replace("\r", "").Replace("\n", " ")).Append("\r\n");
        var zones = new HashSet<string>();
        foreach (var text in calendars)
        {
            var lines = VCardData.Unfold(text).Replace("\r\n", "\n").Split('\n');
            var depth = 0;
            StringBuilder? part = null;
            string? zone = null;
            foreach (var line in lines)
            {
                if (line.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase))
                {
                    depth++;
                    if (depth == 2)
                    {
                        part = new StringBuilder();
                        zone = null;
                    }
                }

                if (depth >= 2 && part is not null)
                {
                    part.Append(line).Append("\r\n");
                    if (line.StartsWith("TZID:", StringComparison.OrdinalIgnoreCase) && depth == 2)
                    {
                        zone = line[5..].Trim();
                    }
                }

                if (line.StartsWith("END:", StringComparison.OrdinalIgnoreCase))
                {
                    if (depth == 2 && part is not null)
                    {
                        var isZone = part.ToString().StartsWith("BEGIN:VTIMEZONE", StringComparison.OrdinalIgnoreCase);
                        if (!isZone || zone is null || zones.Add(zone))
                        {
                            builder.Append(part);
                        }

                        part = null;
                    }

                    depth--;
                }
            }
        }

        return builder.Append("END:VCALENDAR\r\n").ToString();
    }

    private async Task PutAsync(HttpContext context, Account account, Target target)
    {
        var response = context.Response;
        if (target.Kind != Kind.Object || target.Collection is null || target.Name is null)
        {
            response.StatusCode = target.Kind == Kind.Object ? StatusCodes.Status409Conflict : StatusCodes.Status405MethodNotAllowed;
            return;
        }

        var existing = target.Object;
        var ifMatch = context.Request.Headers.IfMatch.ToString();
        var ifNoneMatch = context.Request.Headers.IfNoneMatch.ToString();
        if (ifNoneMatch == "*" && existing is not null || ifMatch.Length > 0 && (existing is null || ifMatch != "*" && !ifMatch.Split(',').Select(e => e.Trim()).Contains(existing.ETag)))
        {
            response.StatusCode = StatusCodes.Status412PreconditionFailed;
            return;
        }

        string text;
        using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8))
        {
            text = await reader.ReadToEndAsync(context.RequestAborted);
        }

        DavObjectInfo info;
        try
        {
            info = target.Type == DavStore.Calendar ? CalendarData.Inspect(text) : VCardData.Inspect(text);
        }
        catch (FormatException ex)
        {
            logger.LogInformation("Refused {Name} of {Account}: {Error}", target.Name, account.Address, ex.Message);
            await WriteErrorAsync(context, StatusCodes.Status415UnsupportedMediaType,
                new XElement(target.Type == DavStore.Calendar ? C + "valid-calendar-data" : CR + "valid-address-data"), ex.Message);
            return;
        }

        DavObject stored;
        try
        {
            stored = store.Put(target.Collection.Id, target.Name, text, info);
        }
        catch (DavConflictException)
        {
            await WriteErrorAsync(context, StatusCodes.Status409Conflict,
                new XElement(target.Type == DavStore.Calendar ? C + "no-uid-conflict" : CR + "no-uid-conflict"));
            return;
        }
        catch (ArgumentException)
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        response.Headers.ETag = stored.ETag;
        response.StatusCode = existing is null ? StatusCodes.Status201Created : StatusCodes.Status204NoContent;
    }

    private void Delete(HttpContext context, Target target)
    {
        var response = context.Response;
        switch (target)
        {
            case { Kind: Kind.Object, Object: { } item }:
                var ifMatch = context.Request.Headers.IfMatch.ToString();
                if (ifMatch.Length > 0 && ifMatch != "*" && ifMatch != item.ETag)
                {
                    response.StatusCode = StatusCodes.Status412PreconditionFailed;
                    return;
                }

                store.Delete(target.Collection!.Id, item.Href);
                response.StatusCode = StatusCodes.Status204NoContent;
                return;
            case { Kind: Kind.Collection, Collection: { } collection }:
                store.DeleteCollection(collection.Id);
                response.StatusCode = StatusCodes.Status204NoContent;
                return;
            case { Kind: Kind.Object or Kind.Collection }:
                response.StatusCode = StatusCodes.Status404NotFound;
                return;
            default:
                response.StatusCode = StatusCodes.Status403Forbidden;
                return;
        }
    }

    private async Task MakeCollectionAsync(HttpContext context, Account account, Target target)
    {
        if (target.Kind != Kind.Collection || target.Name is null)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (target.Collection is not null)
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        // MKCALENDAR only makes calendars; MKCOL makes what its URL says (calendars or address books).
        if (context.Request.Method == "MKCALENDAR" && target.Type != DavStore.Calendar)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var body = await ReadXmlAsync(context);
        var props = body?.Root?.Elements(D + "set").Elements(D + "prop").Elements().ToList() ?? [];
        DavCollection created;
        try
        {
            created = store.CreateCollection(account.Id, target.Type!, target.Name, props.FirstOrDefault(p => p.Name == D + "displayname")?.Value ?? target.Name);
        }
        catch (Exception ex) when (ex is DavConflictException or ArgumentException)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        ApplyCollectionProperties(created, props.Where(p => p.Name != D + "displayname"));
        logger.LogInformation("{Account} created {Type} {Name}", account.Address, target.Type, target.Name);
        context.Response.StatusCode = StatusCodes.Status201Created;
    }

    // ---- XML ----

    private static async Task<XDocument?> ReadXmlAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(context.RequestAborted);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // No DTDs: no entity expansion tricks.
        using var xml = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(xml);
    }

    private static XElement PropStat(IEnumerable<XElement> props, int status) =>
        new(D + "propstat", new XElement(D + "prop", props),
            new XElement(D + "status", $"HTTP/1.1 {status} {(status == 200 ? "OK" : ((HttpStatusCode)status).ToString())}".Replace("NotFound", "Not Found")));

    private static async Task WriteMultiStatusAsync(HttpContext context, IEnumerable<XElement> responses, XElement? extra = null)
    {
        var root = new XElement(D + "multistatus",
            new XAttribute(XNamespace.Xmlns + "d", D), new XAttribute(XNamespace.Xmlns + "c", C), new XAttribute(XNamespace.Xmlns + "card", CR),
            new XAttribute(XNamespace.Xmlns + "cs", CS), new XAttribute(XNamespace.Xmlns + "a", A), responses, extra);
        await WriteXmlAsync(context, 207, root);
    }

    private static async Task WriteErrorAsync(HttpContext context, int status, XElement condition, string? message = null)
    {
        var root = new XElement(D + "error", new XAttribute(XNamespace.Xmlns + "d", D), condition);
        if (message is not null)
        {
            root.Add(new XElement(D + "responsedescription", message));
        }

        await WriteXmlAsync(context, status, root);
    }

    private static async Task WriteXmlAsync(HttpContext context, int status, XElement root)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/xml; charset=utf-8";
        var text = new XDocument(new XDeclaration("1.0", "utf-8", null), root).Declaration + "\n" + root.ToString(SaveOptions.DisableFormatting);
        await context.Response.WriteAsync(text);
    }
}
