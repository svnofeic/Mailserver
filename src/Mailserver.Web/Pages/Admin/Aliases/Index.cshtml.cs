using Mailserver.Core;
using Mailserver.Core.Accounts;
using Microsoft.AspNetCore.Mvc;

namespace Mailserver.Web.Pages.Admin.Aliases;

public sealed class IndexModel(AccountStore accounts) : MailPageModel
{
    public IReadOnlyList<Alias> Aliases { get; private set; } = [];

    public void OnGet() => Aliases = accounts.ListAliases();

    public IActionResult OnPost(string address, string targets)
    {
        try
        {
            var targetList = (targets ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(EmailAddress.Parse).ToList();
            var alias = accounts.AddAlias(EmailAddress.Parse(address), targetList);
            Message = $"Alias {alias.Address} → {string.Join(", ", alias.Targets)} gespeichert.";
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage();
    }

    public IActionResult OnPostDelete(string address)
    {
        if (EmailAddress.TryParse(address, out var parsed) && accounts.RemoveAlias(parsed))
        {
            Message = $"Alias {parsed} gelöscht.";
        }

        return RedirectToPage();
    }
}
