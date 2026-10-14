using Mailserver.Core.Accounts;
using Mailserver.Core.Security;
using Mailserver.Core.SpamLogging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mailserver.Web.Pages;

/// <summary>Second step of the sign-in: the code from the authenticator app or a recovery code.</summary>
public sealed class LoginCodeModel(TwoFactor twoFactor, AuthThrottle throttle, SpamLog log, ILogger<LoginCodeModel> logger) : PageModel
{
    public string? Error { get; private set; }
    public string? Address { get; private set; }

    public IActionResult OnGet()
    {
        if (WebSignIn.Pending(HttpContext) is not { } pending)
        {
            return Redirect("/Login");
        }

        Address = pending.Account.Address.ToString();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? code, bool trust = false)
    {
        if (WebSignIn.Pending(HttpContext) is not { } pending)
        {
            return Redirect("/Login");
        }

        Address = pending.Account.Address.ToString();
        var ip = HttpContext.Connection.RemoteIpAddress;
        if (ip is { IsIPv4MappedToIPv6: true })
        {
            ip = ip.MapToIPv4();
        }

        if (throttle.IsLockedOut(ip))
        {
            Error = "Zu viele fehlgeschlagene Anmeldungen. Bitte später erneut versuchen.";
            return Page();
        }

        if (!twoFactor.Verify(pending.Account.Id, code))
        {
            var lockedOut = throttle.RecordFailure(ip);
            log.WriteAuthFailure("Web-2FA", Address, ip, lockedOut);
            logger.LogWarning("Wrong two-factor code for {User} from {Ip}", Address, ip);
            await Task.Delay(TimeSpan.FromSeconds(1));
            Error = "Der Code stimmt nicht (oder wurde schon benutzt).";
            return Page();
        }

        throttle.RecordSuccess(ip);
        WebSignIn.ClearPending(HttpContext);
        if (trust)
        {
            WebSignIn.TrustThisDevice(HttpContext, pending.Account);
        }

        await WebSignIn.CompleteAsync(HttpContext, pending.Account, pending.Remember);
        logger.LogInformation("Web login {User} from {Ip} (two-factor)", Address, ip);
        return LocalRedirect(Url.IsLocalUrl(pending.ReturnUrl) ? pending.ReturnUrl! : "/Mail");
    }
}
