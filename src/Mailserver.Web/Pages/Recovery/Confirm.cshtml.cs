using Mailserver.Core.Accounts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mailserver.Web.Pages.Recovery;

/// <summary>Confirms an external address with the link sent to it – with a button, because mail scanners open links on their own.</summary>
public sealed class ConfirmModel(PasswordRecovery recovery) : PageModel
{
    public string? Token { get; private set; }
    public Mailserver.Core.Accounts.Account? Confirmed { get; private set; }
    public bool Failed { get; private set; }

    public void OnGet(string? token) => Token = token;

    public IActionResult OnPost(string? token)
    {
        Token = token;
        Confirmed = string.IsNullOrEmpty(token) ? null : recovery.Confirm(token);
        Failed = Confirmed is null;
        return Page();
    }
}
