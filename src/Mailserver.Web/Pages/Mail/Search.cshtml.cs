using System.Globalization;
using Mailserver.Core.Storage;
using Mailserver.Web.Webmail;
using Microsoft.AspNetCore.Mvc;

namespace Mailserver.Web.Pages.Mail;

/// <summary>Search over one or all folders, in the text and attachment names, with filters.</summary>
public sealed class SearchModel(WebmailStore store, SearchIndex index) : MailPageModel
{
    public const int PageSize = 50;
    private const int IndexBeforeSearch = 500;

    public IReadOnlyList<(Folder Folder, FolderStatus Status)> Folders { get; private set; } = [];
    public IReadOnlyList<(SearchHit Hit, MessageSummary Summary)> Items { get; private set; } = [];
    public int Total { get; private set; }
    public int PageNumber { get; private set; }
    public int PageCount => (Total + PageSize - 1) / PageSize;
    public bool Searched { get; private set; }
    public int NotYetIndexed { get; private set; }
    public IReadOnlyList<string> IgnoredWords { get; private set; } = [];

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    /// <summary>Folder name; empty means all folders.</summary>
    [BindProperty(SupportsGet = true, Name = "folder")] public string? FolderName { get; set; }
    [BindProperty(SupportsGet = true)] public bool HeadersOnly { get; set; }
    [BindProperty(SupportsGet = true)] public bool Unread { get; set; }
    [BindProperty(SupportsGet = true)] public bool Flagged { get; set; }
    [BindProperty(SupportsGet = true)] public bool Attachment { get; set; }
    [BindProperty(SupportsGet = true)] public string? Since { get; set; }
    [BindProperty(SupportsGet = true)] public string? Until { get; set; }

    public async Task<IActionResult> OnGetAsync([FromQuery(Name = "page")] int page, CancellationToken cancellationToken)
    {
        Folders = store.Folders(CurrentAccount.Id);
        Folder? folder = null;
        if (!string.IsNullOrEmpty(FolderName) && (folder = store.Folder(CurrentAccount.Id, FolderName)) is null)
        {
            return NotFound();
        }

        var search = new MailSearch(Q, folder?.Id, !HeadersOnly, Unread, Flagged, Attachment, Date(Since), Date(Until)?.AddDays(1));
        IgnoredWords = (Q ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Trim('"').Length < MailSearch.MinWordLength).ToList();
        if (search.IsEmpty)
        {
            return Page();
        }

        // Recent mail first goes into the index; the rest follows in the background.
        await index.IndexPendingAsync(IndexBeforeSearch, CurrentAccount.Id, cancellationToken);
        NotYetIndexed = index.Pending(CurrentAccount.Id);
        Searched = true;
        PageNumber = Math.Max(0, page);
        var (hits, total) = index.Search(CurrentAccount.Id, search, PageNumber * PageSize, PageSize);
        Total = total;
        Items = hits.Select(h => (h, store.Summary(h.Message))).ToList();
        return Page();
    }

    private static DateTimeOffset? Date(string? value) =>
        DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date))
            : null;

    public string PageUrl(int page)
    {
        var query = new Dictionary<string, string?>
        {
            ["q"] = Q, ["folder"] = FolderName, ["headersOnly"] = HeadersOnly ? "true" : null, ["unread"] = Unread ? "true" : null,
            ["flagged"] = Flagged ? "true" : null, ["attachment"] = Attachment ? "true" : null, ["since"] = Since, ["until"] = Until,
            ["page"] = page.ToString(CultureInfo.InvariantCulture),
        };
        return "/Mail/Search?" + string.Join('&', query.Where(p => !string.IsNullOrEmpty(p.Value))
            .Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}"));
    }
}
