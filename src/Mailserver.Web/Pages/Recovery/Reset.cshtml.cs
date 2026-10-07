using Mailserver.Core.Accounts;
using Mailserver.Core.Routing;
using Mailserver.Core.Security;
using Mailserver.Core.SpamLogging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mailserver.Web.Pages.Recovery;

/// <summary>Sets a new password with a link from "Passwort vergessen". Opening the link changes nothing (mail scanners open links too).</summary>
public sealed class ResetModel(PasswordRecovery recovery, RecoveryMailer mailer, AuthThrottle throttle, SpamLog log,
    ILogger<ResetModel> logger) : PageModel
{
    public string? Token { get; private set; }
    public Mailserver.Core.Accounts.Account? Account { get; private set; }
    public string? Error { get; private set; }
    public bool Done { get; private set; }

    public void OnGet(string? token)
    {
        Token = token;
        Account = recovery.FindReset(token);
    }

    public async Task<IActionResult> OnPostAsync(string? token, string password, string confirm, CancellationToken cancellationToken)
    {
        var ip = HttpContext.Connection.RemoteIpAddress is { IsIPv4MappedToIPv6: true } mapped ? mapped.MapToIPv4() : HttpContext.Connection.RemoteIpAddress;
        Token = token;
        Account = throttle.IsLockedOut(ip) ? null : recovery.FindReset(token);
        if (Account is null)
        {
            throttle.RecordFailure(ip);
            return Page();
        }

        if (PasswordRules.Check(password, confirm) is { } problem)
        {
            Error = problem;
            return Page();
        }

        recovery.Reset(token, password);
        Done = true;
        log.Write(new SpamLogEntry
        {
            Stage = SpamLogStage.Auth, Action = SpamLogAction.PasswordReset, ClientIp = ip?.ToString(), Recipient = Account.Address.ToString(),
            Detail = "Web, über „Passwort vergessen“",
        });
        logger.LogWarning("Password of {Account} reset via recovery link from {Ip}", Account.Address, ip);
        await mailer.NotifyResetAsync(Account, ip?.ToString(), cancellationToken);
        return Page();
    }
}
