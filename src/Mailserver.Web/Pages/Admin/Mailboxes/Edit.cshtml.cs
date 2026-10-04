using Mailserver.Core;
using Mailserver.Core.Accounts;
using Mailserver.Core.Rules;
using Mailserver.Core.Storage;
using Microsoft.AspNetCore.Mvc;

namespace Mailserver.Web.Pages.Admin.Mailboxes;

public sealed class EditModel(AccountStore accounts, MailboxStore mailboxes, RuleStore rules, MailboxSettingsStore settings) : MailPageModel
{
    [BindProperty]
    public MailboxAutomationForm Form { get; set; } = new();

    public Mailserver.Core.Accounts.Account Account { get; private set; } = null!;
    public long Usage { get; private set; }
    public int RuleCount { get; private set; }
    public List<(Folder Folder, FolderStatus Status)> Folders { get; } = [];
    public bool IsSelf => Account.Id == CurrentAccount.Id;

    public IActionResult OnGet(string address)
    {
        if (!Load(address))
        {
            return NotFound();
        }

        Form = MailboxAutomationForm.Create(settings.Get(Account.Id));
        Describe();
        return Page();
    }

    private void Describe()
    {
        Usage = mailboxes.GetUsage(Account.Id);
        RuleCount = rules.List(Account.Address.ToString()).Count;
        foreach (var folder in mailboxes.ListFolders(Account.Id))
        {
            Folders.Add((folder, mailboxes.GetStatus(folder.Id)));
        }
    }

    public IActionResult OnPostSave(string address, long quotaMb, bool enabled, bool isAdmin)
    {
        if (!Load(address))
        {
            return NotFound();
        }

        accounts.SetQuota(Account.Address, Math.Max(0, quotaMb) * 1024 * 1024);
        if (!IsSelf)
        {
            accounts.SetEnabled(Account.Address, enabled);
            accounts.SetAdmin(Account.Address, isAdmin);
        }

        Message = "Gespeichert.";
        return Redirect($"/Admin/Mailboxes/Edit?address={Account.Address}");
    }

    public IActionResult OnPostAutomation(string address)
    {
        if (!Load(address))
        {
            return NotFound();
        }

        try
        {
            Form.Save(settings, Account);
        }
        catch (ArgumentException ex)
        {
            // Show the form again with what was entered.
            ErrorMessage = ex.Message;
            Describe();
            return Page();
        }

        Message = "Weiterleitung und Abwesenheitsnotiz gespeichert.";
        return Redirect($"/Admin/Mailboxes/Edit?address={Account.Address}");
    }

    public IActionResult OnPostPassword(string address, string password)
    {
        if (!Load(address))
        {
            return NotFound();
        }

        if (PasswordRules.Check(password, password) is { } problem)
        {
            ErrorMessage = problem;
        }
        else if (IsSelf)
        {
            ErrorMessage = "Das eigene Passwort bitte unter „Passwort“ ändern.";
        }
        else
        {
            accounts.SetPassword(Account.Address, password);
            Message = "Passwort gesetzt.";
        }

        return Redirect($"/Admin/Mailboxes/Edit?address={Account.Address}");
    }

    public IActionResult OnPostDelete(string address)
    {
        if (!Load(address))
        {
            return NotFound();
        }

        if (IsSelf)
        {
            ErrorMessage = "Das eigene Postfach kann nicht gelöscht werden.";
            return Redirect($"/Admin/Mailboxes/Edit?address={Account.Address}");
        }

        accounts.RemoveAccount(Account.Address);
        foreach (var rule in rules.List(Account.Address.ToString()))
        {
            rules.Remove(rule.Id);
        }

        Message = $"Postfach {Account.Address} gelöscht.";
        return RedirectToPage("Index");
    }

    private bool Load(string address)
    {
        if (!EmailAddress.TryParse(address, out var parsed) || accounts.FindAccount(parsed) is not { } account)
        {
            return false;
        }

        Account = account;
        return true;
    }
}
