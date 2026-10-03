using Mailserver.Core;
using Mailserver.Core.Queue;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Mailserver.Web.Pages.Admin.Queue;

public sealed class IndexModel(OutboundQueue queue, IOptions<MailserverOptions> options) : MailPageModel
{
    public IReadOnlyList<QueueEntry> Entries { get; private set; } = [];
    public double LifetimeDays => Math.Round(options.Value.Delivery.MaxQueueLifetime.TotalDays, 1);

    public void OnGet() => Entries = queue.List();

    public IActionResult OnPostRetry()
    {
        Message = $"{queue.RetryAll()} Einträge werden sofort erneut zugestellt.";
        return RedirectToPage();
    }

    public IActionResult OnPostDelete(long id)
    {
        Message = queue.Remove(id) ? "Eintrag entfernt." : null;
        return RedirectToPage();
    }
}
