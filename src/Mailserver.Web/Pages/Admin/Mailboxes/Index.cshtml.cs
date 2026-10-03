using Mailserver.Core;
using Mailserver.Core.Accounts;
using Mailserver.Core.Storage;
using Microsoft.AspNetCore.Mvc;

namespace Mailserver.Web.Pages.Admin.Mailboxes;

public sealed class IndexModel(AccountStore accounts, MailboxStore mailboxes) : MailPageModel
{
    public string? Domain { get; private set; }
    public IReadOnlyList<string> Domains { get; private set; } = [];
    public List<(Mailserver.Core.Accounts.Account Account, long Usage)> Accounts { get; } = [];

    public void OnGet(string? domain)
    {
        Domain = string.IsNullOrEmpty(domain) ? null : domain;
        Domains = accounts.ListDomains().Select(d => d.Name).ToList();
        foreach (var account in accounts.ListAccounts(Domain))
        {
            Accounts.Add((account, mailboxes.GetUsage(account.Id)));
        }
    }

    public IActionResult OnPost(string localPart, string domain, string password, long quotaMb, bool isAdmin)
    {
        if (!EmailAddress.TryParse($"{localPart?.Trim()}@{domain}", out var address) || !accounts.IsLocalDomain(address.Domain))
        {
            ErrorMessage = "Ungültige Adresse.";
            return RedirectToPage(new { domain });
        }

        if (PasswordRules.Check(password, password) is { } problem)
        {
            ErrorMessage = problem;
            return RedirectToPage(new { domain });
        }

        try
        {
            var account = accounts.AddAccount(address, password, Math.Max(0, quotaMb) * 1024 * 1024);
            mailboxes.EnsureDefaultFolders(account.Id);
            if (isAdmin)
            {
                accounts.SetAdmin(address, true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            ErrorMessage = ex is Microsoft.Data.Sqlite.SqliteException ? $"{address} existiert bereits." : ex.Message;
            return RedirectToPage(new { domain });
        }

        Message = $"Postfach {address} angelegt.";
        return RedirectToPage(new { domain });
    }
}
