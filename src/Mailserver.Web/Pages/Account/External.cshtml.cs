using Mailserver.Core.External;
using Microsoft.AspNetCore.Mvc;

namespace Mailserver.Web.Pages.Account;

public sealed class ExternalForm
{
    public long? Id { get; set; }
    public string Address { get; set; } = "";
    public string? Folder { get; set; }
    public string? ImapHost { get; set; }
    public int ImapPort { get; set; } = 993;
    public MailSecurity ImapSecurity { get; set; } = MailSecurity.Ssl;
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public bool Send { get; set; } = true;
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 465;
    public MailSecurity SmtpSecurity { get; set; } = MailSecurity.Ssl;
    public bool FetchExisting { get; set; }

    public static ExternalForm From(ExternalAccount account) => new()
    {
        Id = account.Id,
        Address = account.Address,
        Folder = account.Settings.Folder,
        ImapHost = account.Settings.Imap.Host,
        ImapPort = account.Settings.Imap.Port,
        ImapSecurity = account.Settings.Imap.Security,
        UserName = account.Settings.UserName,
        Send = account.CanSend,
        SmtpHost = account.Settings.Smtp?.Host,
        SmtpPort = account.Settings.Smtp?.Port ?? 465,
        SmtpSecurity = account.Settings.Smtp?.Security ?? MailSecurity.Ssl,
    };

    /// <summary>Empty server names are guessed as imap./smtp. plus the domain, as most providers name them.</summary>
    public ExternalAccountSettings ToSettings()
    {
        var domain = Address.Contains('@') ? Address[(Address.LastIndexOf('@') + 1)..].Trim() : "";
        var imapHost = string.IsNullOrWhiteSpace(ImapHost) ? "imap." + domain : ImapHost;
        var smtpHost = string.IsNullOrWhiteSpace(SmtpHost) ? "smtp." + domain : SmtpHost;
        return new ExternalAccountSettings(Address.Trim(), Folder ?? "", new MailServerAddress(imapHost, ImapPort, ImapSecurity),
            Send ? new MailServerAddress(smtpHost, SmtpPort, SmtpSecurity) : null, UserName ?? "");
    }
}

/// <summary>Addresses at other providers: fetched into a folder of this mailbox, usable as sender in webmail.</summary>
public sealed class ExternalModel(ExternalAccountStore store, IExternalMail client) : MailPageModel
{
    [BindProperty]
    public ExternalForm Form { get; set; } = new();

    public IReadOnlyList<ExternalAccount> Accounts { get; private set; } = [];

    /// <summary>True while a new address is entered or an existing one edited.</summary>
    public bool Editing { get; private set; }

    public IActionResult OnGet(long? edit, bool add = false)
    {
        Accounts = store.List(CurrentAccount.Id);
        if (edit is { } id)
        {
            if (store.Get(CurrentAccount.Id, id) is not { } account)
            {
                return NotFound();
            }

            Form = ExternalForm.From(account);
            Editing = true;
        }
        else
        {
            Editing = add || Accounts.Count == 0;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        var existing = Form.Id is { } id ? store.Get(CurrentAccount.Id, id) : null;
        if (Form.Id is not null && existing is null)
        {
            return NotFound();
        }

        try
        {
            var settings = store.Check(CurrentAccount, Form.ToSettings(), existing?.Id);
            // Checked before saving: a typo should show up now, not as a failed fetch an hour later.
            var password = string.IsNullOrEmpty(Form.Password) && existing is not null ? store.Password(existing.Id) : Form.Password;
            if (string.IsNullOrEmpty(password))
            {
                throw new ArgumentException(existing is null
                    ? "Bitte das Passwort für das Konto beim Anbieter angeben."
                    : "Das gespeicherte Passwort ist nicht mehr lesbar – bitte neu eingeben.");
            }

            if (await client.TestAsync(settings, password, HttpContext.RequestAborted) is { } error)
            {
                throw new ArgumentException(error);
            }

            var saved = existing is null
                ? store.Add(CurrentAccount, settings, password, Form.FetchExisting)
                : store.Update(CurrentAccount, existing.Id, settings, Form.Password);
            Message = existing is null
                ? $"{saved.Address} eingerichtet. Neue Mails erscheinen im Ordner „{Format.FolderName(saved.Settings.Folder)}“ – abgerufen wird " +
                  (Form.FetchExisting ? "alle paar Minuten; die vorhandenen Mails kommen in den nächsten Minuten dazu." : "alle paar Minuten.")
                : $"{saved.Address} gespeichert.";
            if (existing is null && !Form.FetchExisting)
            {
                // Right away: from now on counts as "new" (taking over a full inbox is left to the background fetch).
                await client.FetchAsync(saved, HttpContext.RequestAborted);
            }

            return RedirectToPage();
        }
        catch (ArgumentException ex)
        {
            ErrorMessage = ex.Message;
            Form.Password = null;
            Accounts = store.List(CurrentAccount.Id);
            Editing = true;
            return Page();
        }
    }

    public async Task<IActionResult> OnPostFetchAsync(long id)
    {
        if (store.Get(CurrentAccount.Id, id) is not { } account)
        {
            return NotFound();
        }

        var result = await client.FetchAsync(account, HttpContext.RequestAborted);
        if (result.Error is { } error)
        {
            ErrorMessage = $"{account.Address}: {error}";
        }
        else
        {
            Message = result.Fetched switch
            {
                0 => $"{account.Address}: keine neuen Mails.",
                1 => $"{account.Address}: 1 neue Mail abgerufen.",
                var n => $"{account.Address}: {n} neue Mails abgerufen.",
            };
        }

        return RedirectToPage();
    }

    public IActionResult OnPostEnable(long id, bool enabled)
    {
        if (store.SetEnabled(CurrentAccount.Id, id, enabled))
        {
            Message = enabled ? "Abruf eingeschaltet." : "Abruf ausgeschaltet. Senden über diese Adresse geht weiterhin.";
        }

        return RedirectToPage();
    }

    public IActionResult OnPostRemove(long id)
    {
        if (store.Get(CurrentAccount.Id, id) is { } account && store.Remove(CurrentAccount.Id, id))
        {
            Message = $"{account.Address} entfernt. Die bereits abgerufenen Mails bleiben im Ordner „{account.Settings.Folder}“.";
        }

        return RedirectToPage();
    }
}
