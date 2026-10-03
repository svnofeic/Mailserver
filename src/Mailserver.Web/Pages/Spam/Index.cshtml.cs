using Mailserver.Core;
using Mailserver.Core.Rules;
using Mailserver.Core.SpamLogging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Mailserver.Web.Pages.Spam;

public sealed class IndexModel(SpamLog log, RuleStore rules, IOptions<MailserverOptions> options) : MailPageModel
{
    public int Days { get; private set; }
    public string? Filter { get; private set; }
    public double Threshold => options.Value.Spam.JunkThreshold;
    public IReadOnlyList<(SpamLogEntry Delivery, SpamLogEntry? Message)> Entries { get; private set; } = [];

    public void OnGet(int days = 7, string? filter = null)
    {
        Days = Math.Clamp(days, 1, 365);
        Filter = filter is "spam" or "inbox" ? filter : null;
        Entries = log.Deliveries(CurrentAddress.ToString(), DateTimeOffset.UtcNow.AddDays(-Days), 500)
            .Where(e => Filter switch
            {
                "spam" => e.Delivery.Folder == "Junk" || e.Delivery.Action == SpamLogAction.Discarded,
                "inbox" => e.Delivery.Folder != "Junk" && e.Delivery.Action != SpamLogAction.Discarded,
                _ => true,
            })
            .ToList();
    }

    public IActionResult OnPostAllow(string sender) => AddSenderRule(sender, RuleAction.Inbox, "erlaubt – landet künftig immer im Posteingang");

    public IActionResult OnPostBlock(string sender) => AddSenderRule(sender, RuleAction.Junk, "wird künftig in den Spam-Ordner verschoben");

    private IActionResult AddSenderRule(string sender, RuleAction action, string result)
    {
        if (!EmailAddress.TryParse(sender, out var address))
        {
            ErrorMessage = "Ungültige Absenderadresse.";
            return RedirectToPage();
        }

        var condition = new RuleCondition(RuleField.From, RuleOperator.Contains, address.ToString());
        rules.Add(CurrentAddress.ToString(), $"{condition} → {RuleActionText.Describe(action, null)}", [condition], action);
        Message = $"{address} {result}. Die Regel kann unter „Regeln“ geändert werden.";
        return RedirectToPage();
    }
}
