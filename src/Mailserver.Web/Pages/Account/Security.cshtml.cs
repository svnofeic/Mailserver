using Mailserver.Core.Accounts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using QRCoder;

namespace Mailserver.Web.Pages.Account;

/// <summary>Two-factor sign-in (authenticator app) and the devices that may skip the code.</summary>
public sealed class SecurityModel(AccountStore accounts, TwoFactor twoFactor, IDataProtectionProvider protection) : MailPageModel
{
    public bool Enabled { get; private set; }
    public DateTimeOffset? EnabledSince { get; private set; }
    public int CodesLeft { get; private set; }
    public IReadOnlyList<TrustedDevice> Devices { get; private set; } = [];

    /// <summary>Set while setting up: the new secret (plain for display, protected for the form), link and QR code.</summary>
    public string? Secret { get; private set; }
    public string? ProtectedSecret { get; private set; }
    public string? SetupUri { get; private set; }
    public string? QrSvg { get; private set; }

    /// <summary>Shown once right after they were created.</summary>
    public IReadOnlyList<string>? RecoveryCodes { get; private set; }

    public void OnGet() => Load();

    /// <summary>Shows a new secret; nothing is stored until a code from the app confirms it.</summary>
    public IActionResult OnGetSetup()
    {
        if (Blocked() is { } blocked)
        {
            return blocked;
        }

        Load();
        if (!Enabled)
        {
            ShowSetup(TwoFactor.NewSecret());
        }

        return Page();
    }

    public async Task<IActionResult> OnPostEnableAsync(string? secret, string? code)
    {
        if (Blocked() is { } blocked)
        {
            return blocked;
        }

        string plain;
        try
        {
            plain = Protector().Unprotect(secret ?? "");
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            ErrorMessage = "Die Einrichtung ist abgelaufen – bitte neu beginnen.";
            return RedirectToPage();
        }

        var codes = twoFactor.Enable(CurrentAccount.Id, plain, (code ?? "").Replace(" ", "").Trim());
        if (codes is null)
        {
            Load();
            ShowSetup(plain);
            SetupError = "Der Code passt nicht. Stimmt die Uhrzeit auf dem Handy? Bitte den aktuellen Code aus der App eingeben.";
            return Page();
        }

        // The security stamp changed: other sessions end, this one continues.
        await WebSignIn.RenewAsync(HttpContext, accounts.FindAccount(CurrentAccount.Id)!);
        WebSignIn.TrustThisDevice(HttpContext, CurrentAccount);
        Load();
        RecoveryCodes = codes;
        return Page();
    }

    public string? SetupError { get; private set; }

    public IActionResult OnPostCodes(string? current)
    {
        if ((Blocked() ?? PasswordProblem(current)) is { } blocked)
        {
            return blocked;
        }

        Load();
        if (Enabled)
        {
            RecoveryCodes = twoFactor.NewRecoveryCodes(CurrentAccount.Id);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostDisableAsync(string? current)
    {
        if ((Blocked() ?? PasswordProblem(current)) is { } blocked)
        {
            return blocked;
        }

        twoFactor.Disable(CurrentAccount.Id);
        await WebSignIn.RenewAsync(HttpContext, accounts.FindAccount(CurrentAccount.Id)!);
        Message = "Zwei-Faktor-Anmeldung ausgeschaltet. Die Anmeldung braucht jetzt nur noch das Passwort.";
        return RedirectToPage();
    }

    public IActionResult OnPostForget(long id)
    {
        if (Blocked() is { } blocked)
        {
            return blocked;
        }

        twoFactor.ForgetDevice(CurrentAccount.Id, id);
        Message = "Das Gerät muss bei der nächsten Anmeldung wieder einen Code eingeben.";
        return RedirectToPage();
    }

    public IActionResult OnPostForgetAll()
    {
        if (Blocked() is { } blocked)
        {
            return blocked;
        }

        twoFactor.ForgetDevices(CurrentAccount.Id);
        Message = "Alle Geräte müssen bei der nächsten Anmeldung wieder einen Code eingeben.";
        return RedirectToPage();
    }

    private void Load()
    {
        EnabledSince = twoFactor.EnabledSince(CurrentAccount.Id);
        Enabled = EnabledSince is not null;
        CodesLeft = Enabled ? twoFactor.RecoveryCodesLeft(CurrentAccount.Id) : 0;
        Devices = Enabled ? twoFactor.Devices(CurrentAccount.Id) : [];
    }

    private void ShowSetup(string secret)
    {
        Secret = string.Join(' ', secret.Chunk(4).Select(c => new string(c)));
        ProtectedSecret = Protector().Protect(secret);
        SetupUri = TwoFactor.SetupUri($"Mailserver {HttpContext.Request.Host.Host}", CurrentAddress.ToString(), secret);
        using var data = new QRCodeGenerator().CreateQrCode(SetupUri, QRCodeGenerator.ECCLevel.M);
        QrSvg = new SvgQRCode(data).GetGraphic(new System.Drawing.Size(220, 220), "#000000", "#ffffff", drawQuietZones: true,
            sizingMode: SvgQRCode.SizingMode.ViewBoxAttribute);
    }

    // An administrator working in someone else's mailbox must not change how its owner signs in.
    private IActionResult? Blocked()
    {
        if (ImpersonatedBy is null)
        {
            return null;
        }

        ErrorMessage = "Die Zwei-Faktor-Anmeldung richtet der Benutzer selbst ein; zurücksetzen können Sie sie unter Verwaltung → Postfächer.";
        return RedirectToPage();
    }

    private IActionResult? PasswordProblem(string? current)
    {
        if (accounts.Authenticate(CurrentAddress.ToString(), current ?? "") is not null)
        {
            return null;
        }

        ErrorMessage = "Das aktuelle Passwort ist falsch.";
        return RedirectToPage();
    }

    private ITimeLimitedDataProtector Protector() =>
        protection.CreateProtector("Mailserver.Web.TotpSetup", CurrentAccount.Id.ToString()).ToTimeLimitedDataProtector();
}
