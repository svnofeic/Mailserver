using Mailserver.Core.Accounts;
using Mailserver.Core.Storage;
using Mailserver.Web.Webmail;
using Microsoft.AspNetCore.Mvc;

namespace Mailserver.Web.Pages.Mail;

public sealed class IndexModel(WebmailStore store, MailActions actions, Conversations conversations, AccountPrefs prefs) : MailPageModel
{
    public const int PageSize = 50;

    /// <summary>A line of the list: one message, or in conversation view the newest message of a conversation.</summary>
    /// <param name="Uids">The messages the checkbox stands for, comma-separated.</param>
    public sealed record Row(MessageListItem Item, string Uids, int Count, bool Unread, bool Flagged);

    /// <summary>Messages of a conversation that are added to the conversations right before the list is shown.</summary>
    private const int IndexBeforeList = 500;

    public Folder Folder { get; private set; } = null!;
    public IReadOnlyList<(Folder Folder, FolderStatus Status)> Folders { get; private set; } = [];
    public IReadOnlyList<Row> Items { get; private set; } = [];

    /// <summary>The user's choice (on unless switched off), whether or not this folder can be grouped.</summary>
    public bool ConversationsOn { get; private set; }

    /// <summary>The list shows conversations (not for drafts, trash, spam and planned mail, and not while filtering).</summary>
    public bool Grouped { get; private set; }
    public int Total { get; private set; }
    public int PageNumber { get; private set; }
    public int PageCount => (Total + PageSize - 1) / PageSize;
    public string? Search { get; private set; }
    public bool ShowRecipients => Folder.Name is "Sent" or "Drafts";

    // "page" is also a route value in Razor Pages (the page path), which would win over the query string; hence the explicit sources.
    /// <param name="seite">Page number as typed into "Gehe zu Seite" (counting from 1); wins over <paramref name="page"/>.</param>
    public async Task<IActionResult> OnGetAsync(string? folder, [FromQuery(Name = "page")] int page = 0, string? q = null, int? seite = null,
        CancellationToken cancellationToken = default)
    {
        if (store.Folder(CurrentAccount.Id, folder) is not { } selected)
        {
            return NotFound();
        }

        Folder = selected;
        Folders = store.Folders(CurrentAccount.Id);
        Search = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        PageNumber = Math.Max(0, seite is { } typed ? typed - 1 : page);
        ConversationsOn = prefs.GetBool(CurrentAccount.Id, AccountPrefs.Conversations, true);
        Grouped = ConversationsOn && Search is null && !Conversations.FlatFolders.Contains(Folder.Name);
        if (Grouped)
        {
            // New mail is grouped right away, not only when the background service gets to it.
            await conversations.IndexPendingAsync(IndexBeforeList, CurrentAccount.Id, cancellationToken);
        }

        Load();
        if (Items.Count == 0 && Total > 0)
        {
            // Past the end, e.g. after deleting the last mails of the last page.
            PageNumber = PageCount - 1;
            Load();
        }

        return Page();
    }

    private void Load()
    {
        if (Grouped)
        {
            var (rows, total) = conversations.List(Folder, PageNumber * PageSize, PageSize);
            Items = rows.Select(r => new Row(new MessageListItem(r.Newest, store.Summary(r.Newest)), string.Join(",", r.Uids), r.Count, r.Unread,
                r.Flagged)).ToList();
            Total = total;
        }
        else
        {
            var (items, total) = store.List(Folder, PageNumber, PageSize, Search);
            Items = items.Select(i => new Row(i, i.Message.Uid.ToString(), 1, !i.Seen, i.Flagged)).ToList();
            Total = total;
        }
    }

    public IActionResult OnPostConversations(string folder, bool on)
    {
        prefs.SetBool(CurrentAccount.Id, AccountPrefs.Conversations, on);
        Message = on ? "Zusammengehörige Mails werden jetzt als Konversation angezeigt." : "Jede Mail wird jetzt einzeln angezeigt.";
        return Redirect($"/Mail?folder={Uri.EscapeDataString(folder)}");
    }

    /// <param name="uids">Uids, or in conversation view comma-separated uids of a conversation.</param>
    public IActionResult OnPostBulk(string folder, [FromForm(Name = "page")] int page, string[] uids, string op, string? target)
    {
        var selected = uids.SelectMany(u => u.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(u => long.TryParse(u, out var uid) ? uid : 0).Where(u => u > 0).Distinct().ToArray();
        if (store.Folder(CurrentAccount.Id, folder) is not { } source)
        {
            return NotFound();
        }

        if (selected.Length == 0)
        {
            ErrorMessage = "Bitte zuerst Nachrichten auswählen.";
        }
        else
        {
            var result = actions.Apply(CurrentAccount, source, selected, op, target);
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

    /// <summary>
    /// Page numbers (from 0) for the page links: the first, the last and two on each side of the current one;
    /// null stands for a gap ("…").
    /// </summary>
    public IReadOnlyList<int?> PageLinks()
    {
        var shown = Enumerable.Range(PageNumber - 2, 5).Append(0).Append(PageCount - 1)
            .Where(p => p >= 0 && p < PageCount).Distinct().Order().ToList();
        var links = new List<int?>();
        foreach (var number in shown)
        {
            if (links.Count > 0 && links[^1] is { } previous && number - previous > 1)
            {
                // A gap of a single page shows that page instead of "…".
                links.Add(number - previous == 2 ? previous + 1 : null);
            }

            links.Add(number);
        }

        return links;
    }

    public string PageUrl(int page) =>
        $"/Mail?folder={Uri.EscapeDataString(Folder.Name)}&page={page}{(Search is null ? "" : "&q=" + Uri.EscapeDataString(Search))}";
}
