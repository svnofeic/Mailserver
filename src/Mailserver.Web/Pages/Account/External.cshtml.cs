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

    /// <summary>
    /// Empty server names come from the list of known providers (T-Online, GMX …) or are guessed as imap./smtp. plus the
    /// domain, as most providers name them. Outlook.com addresses always use Microsoft's servers and sign-in.
    /// </summary>
    public ExternalAccountSettings ToSettings()
    {
        var address = Address.Trim();
        var provider = MailProviders.For(address);
        if (provider is { Microsoft: true })
        {
            return new ExternalAccountSettings(address, Folder ?? "", provider.Imap, Send ? provider.Smtp : null, UserName ?? "", ExternalAuth.Microsoft);
        }

        var domain = address.Contains('@') ? address[(address.LastIndexOf('@') + 1)..] : "";
        var imap = !string.IsNullOrWhiteSpace(ImapHost) ? new MailServerAddress(ImapHost, ImapPort, ImapSecurity)
            : provider?.Imap ?? new MailServerAddress("imap." + domain, ImapPort, ImapSecurity);
        var smtp = !string.IsNullOrWhiteSpace(SmtpHost) ? new MailServerAddress(SmtpHost, SmtpPort, SmtpSecurity)
            : provider?.Smtp ?? new MailServerAddress("smtp." + domain, SmtpPort, SmtpSecurity);
        return new ExternalAccountSettings(address, Folder ?? "", imap, Send ? smtp : null, UserName ?? "");
    }
}

/// <summary>Addresses at other providers: fetched into a folder of this mailbox, usable as sender in webmail.</summary>
public sealed class ExternalModel(
    ExternalAccountStore store,
    IExternalMail client,
    MicrosoftMailAuth microsoft,
    MicrosoftSignIns signIns,
    Mailserver.Core.Configuration.SettingsStore settings,
    Microsoft.Extensions.Options.IOptions<Mailserver.Core.MailserverOptions> options) : MailPageModel
{
    /// <summary>A sign-in at Microsoft waiting for the user to confirm the code.</summary>
    public MicrosoftSignIn? PendingSignIn { get; private set; }

    /// <summary>Outlook.com addresses can be added (an app registration at Microsoft is entered).</summary>
    public bool MicrosoftReady => microsoft.ClientId is not null;

    public string? MicrosoftClientId => options.Value.External.MicrosoftClientId;

    [BindProperty]
    public ExternalForm Form { get; set; } = new();

    public IReadOnlyList<ExternalAccount> Accounts { get; private set; } = [];

    /// <summary>True while a new address is entered or an existing one edited.</summary>
    public bool Editing { get; private set; }

    public IActionResult OnGet(long? edit, bool add = false)
    {
        if (signIns.Take(CurrentAccount.Id) is { } signIn)
        {
            if (!signIn.Done)
            {
                PendingSignIn = signIn;
            }
            else if (signIn.Error is { } error)
            {
                ErrorMessage = $"{signIn.Address}: {error}";
            }
            else
            {
                Message = signIn.Result;
            }
        }

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
            if (settings.Auth == ExternalAuth.Microsoft)
            {
                if (existing is { Settings.Auth: ExternalAuth.Microsoft } && existing.Address.Equals(settings.Address, StringComparison.OrdinalIgnoreCase))
                {
                    // Same Microsoft account, e.g. another folder: the sign-in stays valid.
                    store.Update(CurrentAccount, existing.Id, settings, null);
                    Message = $"{settings.Address} gespeichert.";
                    return RedirectToPage();
                }

                await signIns.StartAsync(CurrentAccount, settings, Form.FetchExisting, existing?.Id, HttpContext.RequestAborted);
                return RedirectToPage();
            }

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
                throw new ArgumentException(MailProviders.For(settings.Address)?.Hint is { } hint ? $"{error} Hinweis: {hint}" : error);
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
        catch (Exception ex) when (ex is ArgumentException or ExternalMailException)
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

    /// <summary>Signs an Outlook.com address in again (Microsoft revoked the sign-in, e.g. after a password change).</summary>
    public async Task<IActionResult> OnPostReauthAsync(long id)
    {
        if (store.Get(CurrentAccount.Id, id) is not { Settings.Auth: ExternalAuth.Microsoft } account)
        {
            return NotFound();
        }

        try
        {
            await signIns.StartAsync(CurrentAccount, account.Settings, fetchExisting: false, account.Id, HttpContext.RequestAborted);
        }
        catch (ExternalMailException ex)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage();
    }

    /// <summary>The app registration at Microsoft for Outlook.com addresses (administrators only).</summary>
    public IActionResult OnPostMicrosoftApp(string? clientId)
    {
        if (!IsAdmin)
        {
            return Forbid();
        }

        clientId = clientId?.Trim();
        if (!string.IsNullOrEmpty(clientId) && !Guid.TryParse(clientId, out _))
        {
            ErrorMessage = "Die Anwendungs-ID sieht aus wie 00000000-0000-0000-0000-000000000000.";
            return RedirectToPage();
        }

        settings.SaveExternal(options.Value.External with { MicrosoftClientId = string.IsNullOrEmpty(clientId) ? null : clientId });
        Message = string.IsNullOrEmpty(clientId) ? "App-Registrierung entfernt." : "App-Registrierung gespeichert – Outlook.com-Adressen lassen sich jetzt hinzufügen.";
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
