using System.Text;
using Mailserver.Core;
using Mailserver.Core.Accounts;
using Mailserver.Core.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Mailserver.Web.Pages.Account;

/// <summary>Setting up mail programs: a profile for iPhone/iPad/Mac and the data for everything else.</summary>
public sealed class SetupModel(IOptions<MailserverOptions> options, AccountStore accounts, CertificateProvider certificates) : MailPageModel
{
    public MailserverOptions Settings => options.Value;
    public IReadOnlyList<string> Domains => accounts.ListDomains().Select(d => d.Name).ToList();

    public void OnGet()
    {
    }

    public IActionResult OnGetProfile()
    {
        var profile = Encoding.UTF8.GetBytes(ClientSetup.AppleProfile(Settings, CurrentAccount));
        return File(ClientSetup.Sign(profile, certificates.GetCertificate()), "application/x-apple-aspen-config", $"{CurrentAddress}.mobileconfig");
    }
}
