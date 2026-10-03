using Mailserver.Core;
using Mailserver.Core.Accounts;
using Mailserver.Core.Dkim;
using Microsoft.AspNetCore.Mvc;

namespace Mailserver.Web.Pages.Admin.Domains;

public sealed class IndexModel(AccountStore accounts, DkimKeyStore dkim) : MailPageModel
{
    public List<(Domain Domain, int Mailboxes, int Aliases)> Domains { get; } = [];

    public void OnGet()
    {
        var mailboxes = accounts.ListAccounts();
        var aliases = accounts.ListAliases();
        foreach (var domain in accounts.ListDomains())
        {
            Domains.Add((domain, mailboxes.Count(a => a.Address.Domain == domain.Name), aliases.Count(a => a.Address.Domain == domain.Name)));
        }
    }

    public IActionResult OnPost(string name)
    {
        if (!EmailAddress.TryNormalizeDomain(name, out var normalized))
        {
            ErrorMessage = "Ungültiger Domainname.";
            return RedirectToPage();
        }

        if (accounts.IsLocalDomain(normalized))
        {
            ErrorMessage = $"{normalized} ist bereits angelegt.";
            return RedirectToPage();
        }

        var selector = $"mail{DateTime.UtcNow:yyyyMM}";
        accounts.AddDomain(normalized, selector);
        if (!dkim.HasKey(normalized, selector))
        {
            dkim.GenerateKey(normalized, selector);
        }

        Message = $"Domain {normalized} angelegt. Jetzt die DNS-Einträge setzen.";
        return Redirect($"/Admin/Domains/Details?name={normalized}");
    }
}
