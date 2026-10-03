using System.Security.Claims;
using Mailserver.Core;
using Mailserver.Core.Accounts;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;

namespace Mailserver.Web;

/// <summary>Base for all signed-in pages: current account and status messages across redirects.</summary>
public abstract class MailPageModel : PageModel
{
    private Account? _account;

    public Account CurrentAccount => _account ??= HttpContext.RequestServices.GetRequiredService<AccountStore>()
        .FindAccount(long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!))
        ?? throw new InvalidOperationException("Account not found.");

    public EmailAddress CurrentAddress => CurrentAccount.Address;

    public bool IsAdmin => User.HasClaim(WebHosting.AdminClaim, "true");

    /// <summary>Success notice; shown on this page or, after a redirect, on the next one.</summary>
    public string? Message
    {
        get => TempData.Peek(nameof(Message)) as string;
        set => TempData[nameof(Message)] = value;
    }

    /// <summary>Error notice; shown on this page or, after a redirect, on the next one.</summary>
    public string? ErrorMessage
    {
        get => TempData.Peek(nameof(ErrorMessage)) as string;
        set => TempData[nameof(ErrorMessage)] = value;
    }
}
