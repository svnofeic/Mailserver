using Mailserver.Core;
using Mailserver.Core.SpamLogging;
using Mailserver.Core.Storage;
using Microsoft.Extensions.Options;

namespace Mailserver.Web.Pages;

public sealed class IndexModel(MailboxStore mailboxes, SpamLog log, IOptions<MailserverOptions> options) : MailPageModel
{
    public long Usage { get; private set; }
    public List<(Folder Folder, FolderStatus Status)> Folders { get; } = [];
    public IReadOnlyList<(SpamLogEntry Delivery, SpamLogEntry? Message)> Recent { get; private set; } = [];
    public int SpamThisWeek { get; private set; }
    public string Hostname => options.Value.Hostname;

    public void OnGet()
    {
        Usage = mailboxes.GetUsage(CurrentAccount.Id);
        var order = MailboxStore.DefaultFolders.ToList();
        foreach (var folder in mailboxes.ListFolders(CurrentAccount.Id)
                     .OrderBy(f => order.IndexOf(f.Name) is var i and >= 0 ? i : order.Count).ThenBy(f => f.Name))
        {
            Folders.Add((folder, mailboxes.GetStatus(folder.Id)));
        }

        var week = log.Deliveries(CurrentAddress.ToString(), DateTimeOffset.UtcNow.AddDays(-7), 1000);
        SpamThisWeek = week.Count(d => d.Delivery.Folder == "Junk" || d.Delivery.Action == SpamLogAction.Discarded);
        Recent = week.Take(10).ToList();
    }
}
