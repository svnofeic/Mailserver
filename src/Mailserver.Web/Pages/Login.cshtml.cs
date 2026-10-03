using Mailserver.Core.Accounts;
using Mailserver.Core.Security;
using Mailserver.Core.SpamLogging;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mailserver.Web.Pages;

public sealed class LoginModel(AccountStore accounts, Mailserver.Core.Storage.MailboxStore mailboxes, AuthThrottle throttle, SpamLog log,
    ILogger<LoginModel> logger) : PageModel
{
    public string? Error { get; private set; }
    public string? Email { get; private set; }
    public string? ReturnUrl { get; private set; }

    public IActionResult OnGet(string? returnUrl)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return Redirect("/Mail");
        }

        ReturnUrl = returnUrl;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string email, string password, string? returnUrl)
    {
        Email = email;
        ReturnUrl = returnUrl;
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

        var account = accounts.Authenticate(email ?? "", password ?? "");
        if (account is null)
        {
            var lockedOut = throttle.RecordFailure(ip);
            log.WriteAuthFailure("Web", email ?? "", ip, lockedOut);
            logger.LogWarning("Failed web login for {User} from {Ip}", email, ip);
            await Task.Delay(TimeSpan.FromSeconds(1));
            Error = "E-Mail-Adresse oder Passwort ist falsch.";
            return Page();
        }

        throttle.RecordSuccess(ip);
        foreach (var (alias, target, moved) in mailboxes.MergeAliasFolders(account.Id))
        {
            logger.LogInformation("Merged folder {Alias} of {Account} into {Target} ({Moved} messages)", alias, account.Address, target, moved);
        }

        var principal = WebHosting.CreatePrincipal(account, accounts.GetSecurityStamp(account.Id)!);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        logger.LogInformation("Web login {User} from {Ip}", account.Address, ip);
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/Mail");
    }
}
