using Mailserver.Core.Accounts;
using Microsoft.AspNetCore.Mvc;

namespace Mailserver.Web.Pages.Account;

public sealed class AwayModel(MailboxSettingsStore settings) : MailPageModel
{
    [BindProperty]
    public MailboxAutomationForm Form { get; set; } = new();

    public void OnGet() => Form = MailboxAutomationForm.Create(settings.Get(CurrentAccount.Id));

    public IActionResult OnPost()
    {
        try
        {
            Form.Save(settings, CurrentAccount);
        }
        catch (ArgumentException ex)
        {
            ErrorMessage = ex.Message;
            return Page();
        }

        Message = "Gespeichert.";
        return RedirectToPage();
    }
}
