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
                        https.ServerCertificateSelector = (_, _) => certificates.GetCertificate();
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
        services.AddSingleton<Webmail.WebmailSender>();
        services.AddSingleton<Webmail.MailActions>();
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
            pages.Conventions.AuthorizeFolder("/Admin", AdminPolicy);
        }).AddApplicationPart(typeof(WebHosting).Assembly);

        return builder;
    }

    public static WebApplication UseMailserverWeb(this WebApplication app)
    {
        app.UseExceptionHandler("/Fehler");
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Content-Security-Policy"] =
                "default-src 'none'; style-src 'unsafe-inline'; img-src 'self' data:; frame-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
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
        return app;
    }

    public static ClaimsPrincipal CreatePrincipal(Account account, string stamp) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, account.Id.ToString()),
            new Claim(ClaimTypes.Name, account.Address.ToString()),
            new Claim(AdminClaim, account.IsAdmin ? "true" : "false"),
            new Claim(StampClaim, stamp),
        ], CookieAuthenticationDefaults.AuthenticationScheme));

    /// <summary>Ends sessions whose account was deleted, disabled, demoted or got a new password.</summary>
    private static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        var accounts = context.HttpContext.RequestServices.GetRequiredService<AccountStore>();
        var id = long.TryParse(context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var value) ? value : 0;
        var account = accounts.FindAccount(id);
        if (account is { Enabled: true } && accounts.GetSecurityStamp(id) == context.Principal?.FindFirstValue(StampClaim))
        {
            return;
        }

        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Mailserver.Web")
            .LogInformation("Web session of account {Id} ended (account changed)", id);
    }
}
