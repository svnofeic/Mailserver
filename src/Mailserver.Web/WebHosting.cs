using System.Net;
using System.Security.Claims;
using Mailserver.Core;
using Mailserver.Core.Accounts;
using Mailserver.Core.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Mailserver.Web;

public static class WebHosting
{
    public const string AdminPolicy = "Admin";
    public const string AdminClaim = "mailserver:admin";

    /// <summary>Set while an administrator works in someone else's mailbox: id, address and security stamp of the administrator.</summary>
    public const string ImpersonatorClaim = "mailserver:impersonator";
    public const string ImpersonatorNameClaim = "mailserver:impersonator-name";
    public const string ImpersonatorStampClaim = "mailserver:impersonator-stamp";
    public const string StampClaim = "mailserver:stamp";

    /// <summary>Kestrel endpoints, authentication and Razor Pages for the web interface.</summary>
    public static WebApplicationBuilder AddMailserverWeb(this WebApplicationBuilder builder)
    {
        var web = builder.Configuration.GetSection($"{MailserverOptions.SectionName}:Web").Get<WebOptions>() ?? new WebOptions();
        var dataDirectory = builder.Configuration[$"{MailserverOptions.SectionName}:DataDirectory"] ?? "data";
        var secureCookies = web.InsecureHttpPort == 0;

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = 1024 * 1024;
            if (!web.Enabled)
            {
                // Kestrel needs an endpoint; a random loopback port keeps the interface unreachable.
                kestrel.Listen(IPAddress.Loopback, 0);
                return;
            }

            foreach (var address in web.EffectiveListenAddresses.Select(IPAddress.Parse))
            {
                if (web.HttpsPort > 0)
                {
                    kestrel.Listen(address, web.HttpsPort, listen => listen.UseHttps(https =>
                    {
                        // Same certificate as SMTP/IMAP, reloaded after renewals.
                        var certificates = kestrel.ApplicationServices.GetRequiredService<CertificateProvider>();
                        https.ServerCertificateSelector = (_, _) => certificates.GetWebCertificate();
                    }));
                }

                if (web.InsecureHttpPort > 0)
                {
                    kestrel.Listen(address, web.InsecureHttpPort);
                }
            }
        });

        var services = builder.Services;
        // Umlauts as-is instead of &#xE4; entities.
        services.AddSingleton(System.Text.Encodings.Web.HtmlEncoder.Create(System.Text.Unicode.UnicodeRanges.All));
        var dataProtection = services.AddDataProtection()
            .SetApplicationName("Mailserver")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(new DataPaths(dataDirectory).Root, "keys")));
        if (OperatingSystem.IsWindows())
        {
            dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);
        }

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(cookie =>
        {
            cookie.Cookie.Name = secureCookies ? "__Host-mailserver" : "mailserver";
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SameSite = SameSiteMode.Strict;
            cookie.Cookie.SecurePolicy = secureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            cookie.LoginPath = "/Login";
            cookie.LogoutPath = "/Logout";
            cookie.AccessDeniedPath = "/Verboten";
            cookie.ExpireTimeSpan = web.SessionTimeout;
            cookie.SlidingExpiration = true;
            cookie.Events.OnValidatePrincipal = ValidatePrincipalAsync;
        });
        services.AddSingleton<Webmail.WebmailStore>();
        services.AddSingleton<Webmail.InlineImages>();
        services.AddSingleton<RecoveryThrottle>();
        services.AddSingleton<Webmail.WebmailSender>();
        services.AddSingleton<Webmail.MailActions>();
        services.AddSingleton<Webmail.FolderManager>();
        services.AddAuthorizationBuilder().AddPolicy(AdminPolicy, policy => policy.RequireClaim(AdminClaim, "true"));
        services.AddAntiforgery(antiforgery =>
        {
            antiforgery.Cookie.Name = secureCookies ? "__Host-mailserver-af" : "mailserver-af";
            antiforgery.Cookie.SecurePolicy = secureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        });
        services.AddRazorPages(pages =>
        {
            pages.Conventions.AuthorizeFolder("/");
            pages.Conventions.AllowAnonymousToPage("/Login");
            pages.Conventions.AllowAnonymousToPage("/Fehler");
            pages.Conventions.AllowAnonymousToFolder("/Recovery");
            pages.Conventions.AuthorizeFolder("/Admin", AdminPolicy);
        }).AddApplicationPart(typeof(WebHosting).Assembly);

        return builder;
    }

    public static WebApplication UseMailserverWeb(this WebApplication app)
    {
        app.UseExceptionHandler("/Fehler");
        app.Use(async (context, next) =>
        {
            // Blocked addresses (Admin → IP-Sperren) get nothing; Let's Encrypt validation must still get through.
            if (context.RequestServices.GetRequiredService<Mailserver.Core.Security.AuthThrottle>().IsBlocked(context.Connection.RemoteIpAddress) &&
                !context.Request.Path.StartsWithSegments("/.well-known/acme-challenge"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Zugriff von dieser Adresse gesperrt.");
                return;
            }

            await next();
        });
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Content-Security-Policy"] =
                "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; manifest-src 'self'; worker-src 'self'; connect-src 'self'; frame-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
            // Pages with forms get no-cache headers from the antiforgery system; everything else is marked here.
            context.Response.OnStarting(() =>
            {
                if (!context.Response.Headers.ContainsKey("Cache-Control"))
                {
                    context.Response.Headers.CacheControl = "no-store";
                }

                return Task.CompletedTask;
            });
            if (context.Request.IsHttps)
            {
                headers["Strict-Transport-Security"] = "max-age=31536000";
            }

            await next();
        });
        app.Use(async (context, next) =>
        {
            // Webmail uploads can be as large as a message; everything else keeps the small default limit. This has to happen
            // before anything reads the form (antiforgery validation does so early).
            if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path.StartsWithSegments("/Mail/Compose") &&
                context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            {
                var maxMessageSize = context.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<MailserverOptions>>().Value.MaxMessageSizeBytes;
                limit.MaxRequestBodySize = maxMessageSize + 1024 * 1024;
            }

            await next();
        });
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapRazorPages();
        // Let's Encrypt check, if this web interface itself listens on port 80 (Web:InsecureHttpPort).
        var settings = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<MailserverOptions>>().Value;
        if (settings.Web.Enabled && settings.Web.InsecureHttpPort > 0 && settings.Web.InsecureHttpPort == settings.Tls.Acme.HttpPort)
        {
            app.Services.GetRequiredService<Mailserver.Core.Security.Acme.AcmeCertificateManager>().ChallengesServedByWeb = true;
        }

        app.MapGet(Mailserver.Core.Security.Acme.AcmeChallengeStore.PathPrefix + "{token}", (string token, Mailserver.Core.Security.Acme.AcmeChallengeStore store) =>
            store.Find(token) is { } answer ? Results.Text(answer, "text/plain") : Results.NotFound()).AllowAnonymous();
        app.MapGet("/assets/editor.js", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "public, max-age=3600";
            return Results.Text(EditorScript.Value, "text/javascript; charset=utf-8");
        }).AllowAnonymous();
        app.MapGet("/assets/site.css", (HttpContext context) =>
        {
            // The URL carries the version, so a new release is fetched right away.
            context.Response.Headers.CacheControl = "public, max-age=604800";
            return Results.Text(Stylesheet.Value, "text/css; charset=utf-8");
        }).AllowAnonymous();

        MapProgressiveWebApp(app);

        // Light or dark design, remembered in a cookie (works without JavaScript).
        app.MapGet("/theme", (HttpContext context, string? mode, string? returnUrl) =>
        {
            context.Response.Cookies.Append(ThemeCookie, mode == "light" ? "light" : "dark", new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(1), HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Lax, IsEssential = true,
            });
            var target = returnUrl is { Length: > 0 } && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") && !returnUrl.StartsWith("/\\") ? returnUrl : "/";
            return Results.Redirect(target);
        }).AllowAnonymous();
        return app;
    }

    /// <summary>
    /// Installable web app: manifest, icons, service worker (offline page, cached design files) and the script that registers
    /// it. All without login, as browsers fetch them in the background.
    /// </summary>
    private static void MapProgressiveWebApp(WebApplication app)
    {
        var version = Uri.EscapeDataString(Mailserver.Core.BuildInfo.Version);
        app.MapGet("/manifest.webmanifest", (HttpContext context, Microsoft.Extensions.Options.IOptions<MailserverOptions> options) =>
        {
            var icons = new object[]
            {
                new { src = "/assets/icons/icon-192.png", sizes = "192x192", type = "image/png", purpose = "any" },
                new { src = "/assets/icons/icon-512.png", sizes = "512x512", type = "image/png", purpose = "any" },
                new { src = "/assets/icons/maskable-512.png", sizes = "512x512", type = "image/png", purpose = "maskable" },
            };
            var manifest = new Dictionary<string, object>
            {
                ["id"] = "/",
                ["name"] = $"Mailserver {options.Value.Hostname}",
                ["short_name"] = "Mail",
                ["description"] = "Webmail und Verwaltung des Mailservers",
                ["lang"] = "de",
                ["start_url"] = "/Mail",
                ["scope"] = "/",
                ["display"] = "standalone",
                ["background_color"] = "#070b1d",
                ["theme_color"] = "#0b1030",
                ["icons"] = icons,
                ["shortcuts"] = new object[]
                {
                    new { name = "Neue Mail", url = "/Mail/Compose", icons = new[] { icons[0] } },
                    new { name = "Übersicht", url = "/", icons = new[] { icons[0] } },
                    new { name = "Server-Übersicht", url = "/Admin", icons = new[] { icons[0] } },
                },
                // Lets the installed app open mailto: links (the browser asks the user once).
                ["protocol_handlers"] = new object[] { new { protocol = "mailto", url = "/Mail/Compose?mailto=%s" } },
            };
            context.Response.Headers.CacheControl = "public, max-age=3600";
            return Results.Json(manifest, contentType: "application/manifest+json; charset=utf-8");
        }).AllowAnonymous();

        app.MapGet("/sw.js", (HttpContext context) =>
        {
            // Always checked for a new version; a new release replaces the cached design files.
            context.Response.Headers.CacheControl = "no-cache";
            return Results.Text(Resource("sw.js").Replace("__VERSION__", version), "text/javascript; charset=utf-8");
        }).AllowAnonymous();
        app.MapGet("/assets/app.js", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "public, max-age=604800";
            return Results.Text(Resource("app.js"), "text/javascript; charset=utf-8");
        }).AllowAnonymous();
        app.MapGet("/assets/push.js", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "public, max-age=604800";
            return Results.Text(Resource("push.js"), "text/javascript; charset=utf-8");
        }).AllowAnonymous();
        app.MapGet(Webmail.InlineImages.Path, (HttpContext context, Webmail.InlineImages images, long m, string? c, string? e, string? s) =>
            images.ServeAsync(context, m, c, e, s)).AllowAnonymous();
        app.MapGet("/offline", () => Results.Content(Resource("offline.html").Replace("__VERSION__", version), "text/html; charset=utf-8")).AllowAnonymous();
        app.MapGet("/assets/icons/{name}", (HttpContext context, string name) => Icon(context, name)).AllowAnonymous();
        app.MapGet("/apple-touch-icon.png", (HttpContext context) => Icon(context, "apple-touch-icon.png")).AllowAnonymous();
    }

    private static IResult Icon(HttpContext context, string name)
    {
        using var stream = typeof(WebHosting).Assembly.GetManifestResourceStream("icons/" + name);
        if (stream is null)
        {
            return Results.NotFound();
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        context.Response.Headers.CacheControl = "public, max-age=604800";
        return Results.File(buffer.ToArray(), "image/png");
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Resources = new();

    private static string Resource(string name) => Resources.GetOrAdd(name, key =>
    {
        using var stream = typeof(WebHosting).Assembly.GetManifestResourceStream(key)!;
        return new StreamReader(stream).ReadToEnd();
    });

    public const string ThemeCookie = "theme";

    /// <summary>"light" or "dark" (the default).</summary>
    public static string Theme(HttpContext context) => context.Request.Cookies[ThemeCookie] == "light" ? "light" : "dark";

    private static readonly Lazy<string> Stylesheet = new(() =>
    {
        using var stream = typeof(WebHosting).Assembly.GetManifestResourceStream("site.css")!;
        return new StreamReader(stream).ReadToEnd();
    });

    private static readonly Lazy<string> EditorScript = new(() =>
    {
        using var stream = typeof(WebHosting).Assembly.GetManifestResourceStream("editor.js")!;
        return new StreamReader(stream).ReadToEnd();
    });

    public static ClaimsPrincipal CreatePrincipal(Account account, string stamp) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, account.Id.ToString()),
            new Claim(ClaimTypes.Name, account.Address.ToString()),
            new Claim(AdminClaim, account.IsAdmin ? "true" : "false"),
            new Claim(StampClaim, stamp),
        ], CookieAuthenticationDefaults.AuthenticationScheme));

    /// <summary>
    /// A session in <paramref name="account"/>'s mailbox opened by <paramref name="admin"/>. It never has administrator rights
    /// (also not for another administrator's mailbox) and ends as soon as either account changes.
    /// </summary>
    public static ClaimsPrincipal CreateImpersonation(Account account, string stamp, Account admin, string adminStamp) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, account.Id.ToString()),
            new Claim(ClaimTypes.Name, account.Address.ToString()),
            new Claim(AdminClaim, "false"),
            new Claim(StampClaim, stamp),
            new Claim(ImpersonatorClaim, admin.Id.ToString()),
            new Claim(ImpersonatorNameClaim, admin.Address.ToString()),
            new Claim(ImpersonatorStampClaim, adminStamp),
        ], CookieAuthenticationDefaults.AuthenticationScheme));

    /// <summary>The administrator behind an impersonated session, if still allowed to be one.</summary>
    public static Account? Impersonator(ClaimsPrincipal user, AccountStore accounts) =>
        long.TryParse(user.FindFirstValue(ImpersonatorClaim), out var id) && accounts.FindAccount(id) is { Enabled: true, IsAdmin: true } admin &&
        accounts.GetSecurityStamp(id) == user.FindFirstValue(ImpersonatorStampClaim)
            ? admin
            : null;

    /// <summary>Ends sessions whose account was deleted, disabled, demoted or got a new password.</summary>
    private static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        var accounts = context.HttpContext.RequestServices.GetRequiredService<AccountStore>();
        var id = long.TryParse(context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var value) ? value : 0;
        var account = accounts.FindAccount(id);
        if (account is { Enabled: true } && accounts.GetSecurityStamp(id) == context.Principal?.FindFirstValue(StampClaim) &&
            (context.Principal!.FindFirst(ImpersonatorClaim) is null || Impersonator(context.Principal, accounts) is not null))
        {
            return;
        }

        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Mailserver.Web")
            .LogInformation("Web session of account {Id} ended (account changed)", id);
    }
}
