using Mailserver.Core;
using Mailserver.Core.Accounts;
using Mailserver.Core.Routing;
using Mailserver.Core.Security;
using Mailserver.Core.SpamLogging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mailserver.Web.Pages.Recovery;

/// <summary>"Passwort vergessen": sends a reset link to the mailbox's confirmed external address.</summary>
public sealed class IndexModel(AccountStore accounts, PasswordRecovery recovery, RecoveryMailer mailer, RecoveryThrottle throttle,
    AuthThrottle authThrottle, SpamLog log, ILogger<IndexModel> logger) : PageModel
{
    public bool Sent { get; private set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnPostAsync(string? email, CancellationToken cancellationToken)
    {
        var ip = HttpContext.Connection.RemoteIpAddress is { IsIPv4MappedToIPv6: true } mapped ? mapped.MapToIPv4() : HttpContext.Connection.RemoteIpAddress;
        if (!EmailAddress.TryParse(email?.Trim() ?? "", out var address))
        {
            Error = "Bitte die E-Mail-Adresse des Postfachs eingeben.";
            return Page();
        }

        if (authThrottle.IsLockedOut(ip) || !throttle.TryRequest(address.ToString(), ip?.ToString()))
        {
            Error = "Zu viele Anfragen. Bitte in einer Stunde erneut versuchen.";
            return Page();
        }

        // Same answer whether or not the mailbox exists or has an address: the form must not reveal either.
        Sent = true;
        var account = accounts.FindAccount(address);
        var reset = account is null ? null : recovery.CreateReset(account);
        if (reset is { } link)
        {
            await mailer.SendResetAsync(account!, link.Address, link.Token, cancellationToken);
        }

        var outcome = account is null ? "kein Postfach"
            : reset is not null ? $"Link an {PasswordRecovery.Mask(reset.Value.Address)}"
            : account.IsAdmin ? "nicht für Administratoren"
            : "keine bestätigte Ersatz-Adresse";
        log.Write(new SpamLogEntry
        {
            Stage = SpamLogStage.Auth, Action = SpamLogAction.ResetRequested, ClientIp = ip?.ToString(), Recipient = address.ToString(),
            Detail = $"Web, {outcome}",
        });
        logger.LogInformation("Password reset requested for {Address} from {Ip}: {Outcome}", address, ip, outcome);
        return Page();
    }
}
