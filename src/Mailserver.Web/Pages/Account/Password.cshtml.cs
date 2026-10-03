using Mailserver.Core.Accounts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace Mailserver.Web.Pages.Account;

public sealed class PasswordModel(AccountStore accounts) : MailPageModel
{
    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(string current, string password, string confirm)
    {
        if (accounts.Authenticate(CurrentAddress.ToString(), current ?? "") is null)
        {
            ErrorMessage = "Das aktuelle Passwort ist falsch.";
            return RedirectToPage();
        }

        var problem = PasswordRules.Check(password, confirm);
        if (problem is not null)
        {
            ErrorMessage = problem;
            return RedirectToPage();
        }

        accounts.SetPassword(CurrentAddress, password);
        // The security stamp changed, which ends all other sessions; this one is renewed.
        var account = accounts.FindAccount(CurrentAccount.Id)!;
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            WebHosting.CreatePrincipal(account, accounts.GetSecurityStamp(account.Id)!));
        Message = "Das Passwort wurde geändert.";
        return RedirectToPage();
    }
}
