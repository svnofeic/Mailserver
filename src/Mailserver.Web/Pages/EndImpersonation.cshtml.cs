using Mailserver.Core.Accounts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace Mailserver.Web.Pages;

/// <summary>Leaves a mailbox opened with "Als Benutzer anmelden" and returns to the administrator's own session.</summary>
public sealed class EndImpersonationModel(AccountStore accounts) : MailPageModel
{
    public IActionResult OnGet() => Redirect("/");

    public async Task<IActionResult> OnPostAsync()
    {
        var mailbox = CurrentAddress;
        if (WebHosting.Impersonator(User, accounts) is not { } admin)
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Redirect("/Login");
        }

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            WebHosting.CreatePrincipal(admin, accounts.GetSecurityStamp(admin.Id)!));
        Message = $"Postfach {mailbox} verlassen – Sie sind wieder als {admin.Address} angemeldet.";
        return Redirect($"/Admin/Mailboxes/Edit?address={Uri.EscapeDataString(mailbox.ToString())}");
    }
}
