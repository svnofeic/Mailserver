using Mailserver.Core.Storage;
using Mailserver.Web.Webmail;
using Microsoft.AspNetCore.Mvc;

namespace Mailserver.Web.Pages.Mail;

public sealed class IndexModel(WebmailStore store, MailActions actions) : MailPageModel
{
    public const int PageSize = 50;

    public Folder Folder { get; private set; } = null!;
    public IReadOnlyList<(Folder Folder, FolderStatus Status)> Folders { get; private set; } = [];
    public IReadOnlyList<MessageListItem> Items { get; private set; } = [];
    public int Total { get; private set; }
    public int PageNumber { get; private set; }
    public int PageCount => (Total + PageSize - 1) / PageSize;
    public string? Search { get; private set; }
    public bool ShowRecipients => Folder.Name is "Sent" or "Drafts";

    // "page" is also a route value in Razor Pages (the page path), which would win over the query string; hence the explicit sources.
    public IActionResult OnGet(string? folder, [FromQuery(Name = "page")] int page = 0, string? q = null)
    {
        if (store.Folder(CurrentAccount.Id, folder) is not { } selected)
        {
            return NotFound();
        }

        Folder = selected;
        Folders = store.Folders(CurrentAccount.Id);
        Search = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        PageNumber = Math.Max(0, page);
        (Items, Total) = store.List(Folder, PageNumber, PageSize, Search);
        if (Items.Count == 0 && Total > 0)
        {
            // Past the end, e.g. after deleting the last mails of the last page.
            PageNumber = PageCount - 1;
            (Items, Total) = store.List(Folder, PageNumber, PageSize, Search);
        }

        return Page();
    }

    public IActionResult OnPostBulk(string folder, [FromForm(Name = "page")] int page, long[] uids, string op, string? target)
    {
        if (store.Folder(CurrentAccount.Id, folder) is not { } source)
        {
            return NotFound();
        }

        if (uids.Length == 0)
        {
            ErrorMessage = "Bitte zuerst Nachrichten auswählen.";
        }
        else
        {
            var result = actions.Apply(CurrentAccount, source, uids, op, target);
            if (result.Error is not null)
            {
                ErrorMessage = result.Error;
            }
            else
            {
                Message = result.Message;
            }
        }

        return Redirect($"/Mail?folder={Uri.EscapeDataString(source.Name)}&page={page}");
    }

    public IActionResult OnPostEmpty(string folder)
    {
        if (store.Folder(CurrentAccount.Id, folder) is not { } source)
        {
            return NotFound();
        }

        var result = actions.Empty(source);
        ErrorMessage = result.Error;
        Message = result.Message;
        return Redirect($"/Mail?folder={Uri.EscapeDataString(source.Name)}");
    }

    public string PageUrl(int page) =>
        $"/Mail?folder={Uri.EscapeDataString(Folder.Name)}&page={page}{(Search is null ? "" : "&q=" + Uri.EscapeDataString(Search))}";
}
