using Mailserver.Core;
using Mailserver.Core.Accounts;
using Mailserver.Core.Dkim;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Mailserver.Web.Pages.Admin.Domains;

public sealed class DetailsModel(AccountStore accounts, DkimKeyStore dkim, DataPaths paths, IOptions<MailserverOptions> options) : MailPageModel
{
    public Domain Domain { get; private set; } = null!;
    public IReadOnlyList<DnsRecord> Records { get; private set; } = [];
    public IReadOnlyList<Mailserver.Core.Accounts.Account> Accounts { get; private set; } = [];
    public List<string> InactiveSelectors { get; } = [];
    public Dictionary<string, string> DkimRecords { get; } = [];

    public IActionResult OnGet(string name)
    {
        if (accounts.GetDomain(name) is not { } domain)
        {
            return NotFound();
        }

        Domain = domain;
        var selector = domain.DkimSelector is { } s && dkim.HasKey(domain.Name, s) ? s : null;
        Records = DnsRecommendations.For(domain.Name, options.Value.Hostname, selector, selector is null ? null : dkim.GetDnsRecord(domain.Name, selector));
        Accounts = accounts.ListAccounts(domain.Name);

        // Keys that exist on disk but are not active yet (after "Rotate").
        foreach (var file in Directory.EnumerateFiles(paths.DkimRoot, $"{domain.Name}.*.pem"))
        {
            var candidate = Path.GetFileNameWithoutExtension(file)[(domain.Name.Length + 1)..];
            if (candidate != domain.DkimSelector)
            {
                InactiveSelectors.Add(candidate);
                DkimRecords[candidate] = dkim.GetDnsRecord(domain.Name, candidate);
            }
        }

        return Page();
    }

    public IActionResult OnPostRotate(string name)
    {
        var selector = $"mail{DateTime.UtcNow:yyyyMMddHHmm}";
        if (accounts.GetDomain(name) is null || dkim.HasKey(name, selector))
        {
            return NotFound();
        }

        dkim.GenerateKey(name, selector);
        Message = $"Neuer Schlüssel „{selector}“ erzeugt. DNS-Eintrag setzen und erst danach aktivieren.";
        return Redirect($"/Admin/Domains/Details?name={name}");
    }

    public IActionResult OnPostActivate(string name, string selector)
    {
        if (accounts.GetDomain(name) is null || !dkim.HasKey(name, selector))
        {
            return NotFound();
        }

        accounts.SetDkimSelector(name, selector);
        Message = $"DKIM-Selector „{selector}“ ist aktiv.";
        return Redirect($"/Admin/Domains/Details?name={name}");
    }

    public IActionResult OnPostDelete(string name)
    {
        if (CurrentAddress.Domain == EmailAddress.NormalizeDomain(name))
        {
            ErrorMessage = "Die Domain des eigenen Admin-Postfachs kann nicht gelöscht werden.";
            return Redirect($"/Admin/Domains/Details?name={name}");
        }

        accounts.RemoveDomain(name);
        Message = $"Domain {name} gelöscht.";
        return RedirectToPage("Index");
    }
}
