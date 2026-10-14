using Mailserver.Core.Accounts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Mailserver.Web;

/// <summary>
/// The steps of a web sign-in: password, then – with two-factor sign-in – the code from the app, unless this device is trusted.
/// "Angemeldet bleiben" makes the session cookie persistent for <see cref="RememberFor"/> (renewed with every use).
/// </summary>
public static class WebSignIn
{
    public static readonly TimeSpan RememberFor = TimeSpan.FromDays(30);
    private static readonly TimeSpan CodeTimeout = TimeSpan.FromMinutes(5);
    private const string Purpose = "Mailserver.Web.SecondFactor";

    public static string DeviceCookie(HttpContext context) => context.Request.IsHttps ? "__Host-mailserver-device" : "mailserver-device";

    private static string PendingCookie(HttpContext context) => context.Request.IsHttps ? "__Host-mailserver-2fa" : "mailserver-2fa";

    /// <summary>Signs in right away, or – if a code is needed – remembers the checked password and returns false.</summary>
    public static async Task<bool> PasswordAcceptedAsync(HttpContext context, Account account, bool remember, string? returnUrl)
    {
        var services = context.RequestServices;
        var accounts = services.GetRequiredService<AccountStore>();
        var twoFactor = services.GetRequiredService<TwoFactor>();
        if (twoFactor.IsEnabled(account.Id) && !twoFactor.IsTrusted(account.Id, context.Request.Cookies[DeviceCookie(context)]))
        {
            var ticket = string.Join('\n', account.Id, accounts.GetSecurityStamp(account.Id), remember, returnUrl ?? "");
            context.Response.Cookies.Append(PendingCookie(context), Protector(context).Protect(ticket, CodeTimeout), new CookieOptions
            {
                HttpOnly = true,
                Secure = context.Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                Path = "/",
            });
            return false;
        }

        await CompleteAsync(context, account, remember);
        return true;
    }

    /// <summary>The account waiting for its code (password checked less than five minutes ago and unchanged since).</summary>
    public static (Account Account, bool Remember, string? ReturnUrl)? Pending(HttpContext context)
    {
        try
        {
            var parts = Protector(context).Unprotect(context.Request.Cookies[PendingCookie(context)] ?? "").Split('\n');
            var accounts = context.RequestServices.GetRequiredService<AccountStore>();
            return parts.Length == 4 && long.TryParse(parts[0], out var id) && accounts.FindAccount(id) is { Enabled: true } account &&
                   accounts.GetSecurityStamp(id) == parts[1]
                ? (account, parts[2] == bool.TrueString, parts[3].Length > 0 ? parts[3] : null)
                : null;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    public static void ClearPending(HttpContext context) => context.Response.Cookies.Delete(PendingCookie(context), new CookieOptions
    {
        Secure = context.Request.IsHttps,
        Path = "/",
    });

    /// <summary>Lets this browser skip the code for <see cref="TwoFactor.TrustDuration"/>.</summary>
    public static void TrustThisDevice(HttpContext context, Account account)
    {
        var token = context.RequestServices.GetRequiredService<TwoFactor>().TrustDevice(account.Id, DeviceName(context.Request.Headers.UserAgent));
        context.Response.Cookies.Append(DeviceCookie(context), token, new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = DateTimeOffset.UtcNow.Add(TwoFactor.TrustDuration),
        });
    }

    public static async Task CompleteAsync(HttpContext context, Account account, bool remember)
    {
        var accounts = context.RequestServices.GetRequiredService<AccountStore>();
        var principal = WebHosting.CreatePrincipal(account, accounts.GetSecurityStamp(account.Id)!);
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, Properties(remember));
    }

    /// <summary>Persistent sessions last <see cref="RememberFor"/>; the sliding renewal keeps that span.</summary>
    public static AuthenticationProperties Properties(bool remember) => remember
        ? new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.Add(RememberFor) }
        : new AuthenticationProperties();

    /// <summary>Signs in again with a new principal (e.g. after a password change), keeping "Angemeldet bleiben".</summary>
    public static async Task RenewAsync(HttpContext context, Account account)
    {
        var current = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        await CompleteAsync(context, account, current.Properties?.IsPersistent == true);
    }

    /// <summary>A rough name for the device list ("iPhone · Safari").</summary>
    public static string DeviceName(string? userAgent)
    {
        userAgent ??= "";
        var device = userAgent switch
        {
            _ when userAgent.Contains("iPhone") => "iPhone",
            _ when userAgent.Contains("iPad") => "iPad",
            _ when userAgent.Contains("Android") => "Android",
            _ when userAgent.Contains("Windows") => "Windows",
            _ when userAgent.Contains("Macintosh") => "Mac",
            _ when userAgent.Contains("Linux") => "Linux",
            _ => "Unbekanntes Gerät",
        };
        var browser = userAgent switch
        {
            _ when userAgent.Contains("Edg/") => "Edge",
            _ when userAgent.Contains("Firefox/") || userAgent.Contains("FxiOS/") => "Firefox",
            _ when userAgent.Contains("Chrome/") || userAgent.Contains("CriOS/") => "Chrome",
            _ when userAgent.Contains("Safari/") => "Safari",
            _ => null,
        };
        return browser is null ? device : $"{device} · {browser}";
    }

    private static ITimeLimitedDataProtector Protector(HttpContext context) =>
        context.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).ToTimeLimitedDataProtector();
}
