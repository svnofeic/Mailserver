using Mailserver.Core.Accounts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace Mailserver.Web.Pages.Account;

public sealed class PasswordModel(AccountStore accounts, PasswordRecovery recovery, Mailserver.Core.Routing.RecoveryMailer mailer) : MailPageModel
{
    public RecoveryAddress? Recovery { get; private set; }

    public void OnGet() => Recovery = recovery.Get(CurrentAccount.Id);

    /// <summary>Stores the external address for "Passwort vergessen" and sends the confirmation link to it.</summary>
    public async Task<IActionResult> OnPostRecoveryAsync(string current, string address, CancellationToken cancellationToken)
    {
        if (RecoveryProblem(current) is { } problem)
        {
            ErrorMessage = problem;
            return RedirectToPage();
        }

        try
        {
            var token = recovery.SetAddress(CurrentAccount, address ?? "");
            await mailer.SendConfirmationAsync(CurrentAccount, recovery.Get(CurrentAccount.Id)!.Address, token, cancellationToken);
            Message = $"Bestätigungslink an {address!.Trim()} geschickt – bitte dort anklicken. Erst dann gilt die Adresse.";
        }
        catch (ArgumentException ex)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage();
    }

    public IActionResult OnPostRemoveRecovery(string current)
    {
        if (RecoveryProblem(current) is { } problem)
        {
            ErrorMessage = problem;
            return RedirectToPage();
        }

        recovery.Remove(CurrentAccount.Id);
        Message = "Ersatz-Adresse entfernt. „Passwort vergessen“ ist für dieses Postfach jetzt aus.";
        return RedirectToPage();
    }

    // Whoever controls the recovery address controls the mailbox, so changing it needs the password – a forgotten open
    // session alone must not be enough.
    private string? RecoveryProblem(string? current) =>
        ImpersonatedBy is not null ? "Die Ersatz-Adresse trägt der Benutzer selbst ein – oder Sie unter Verwaltung → Postfächer."
        : accounts.Authenticate(CurrentAddress.ToString(), current ?? "") is null ? "Das aktuelle Passwort ist falsch."
        : null;

    public async Task<IActionResult> OnPostAsync(string current, string password, string confirm)
    {
        if (ImpersonatedBy is not null)
        {
            ErrorMessage = "Als Administrator setzen Sie das Passwort unter Verwaltung → Postfächer.";
            return RedirectToPage();
        }

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
