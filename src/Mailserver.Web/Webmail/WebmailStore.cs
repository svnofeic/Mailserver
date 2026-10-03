using System.Collections.Concurrent;
using Mailserver.Core.Storage;
using MimeKit;

namespace Mailserver.Web.Webmail;

/// <summary>What the message list shows; parsed from the headers only and cached (messages never change once stored).</summary>
public sealed record MessageSummary(string From, string FromAddress, string To, string Subject, bool HasAttachments);

public sealed record MessageListItem(StoredMessage Message, MessageSummary Summary)
{
    public bool Seen => Message.HasFlag(MessageFlags.Seen);
    public bool Flagged => Message.HasFlag(MessageFlags.Flagged);
    public bool Answered => Message.HasFlag(MessageFlags.Answered);
}

/// <summary>Read access to a user's mailbox for webmail. Every lookup is scoped to the given account.</summary>
public sealed class WebmailStore(MailboxStore mailboxes)
{
    private const int MaxCachedSummaries = 50_000;
    private readonly ConcurrentDictionary<long, MessageSummary> _summaries = new();

    public static readonly IReadOnlyList<string> FolderOrder = MailboxStore.DefaultFolders;

    public IReadOnlyList<(Folder Folder, FolderStatus Status)> Folders(long accountId)
    {
        var order = FolderOrder.ToList();
        return mailboxes.ListFolders(accountId)
            .OrderBy(f => order.IndexOf(f.Name) is var i and >= 0 ? i : order.Count)
            .ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(f => (f, mailboxes.GetStatus(f.Id)))
            .ToList();
    }

    public Folder? Folder(long accountId, string? name) =>
        mailboxes.GetFolder(accountId, string.IsNullOrEmpty(name) ? MailboxStore.Inbox : name);

    public StoredMessage? Message(Folder folder, long uid) => mailboxes.ListMessages(folder.Id).FirstOrDefault(m => m.Uid == uid);

    /// <summary>Newest first, optionally filtered by sender, recipient or subject.</summary>
    public (IReadOnlyList<MessageListItem> Items, int Total) List(Folder folder, int page, int pageSize, string? search)
    {
        var all = mailboxes.ListMessages(folder.Id).OrderByDescending(m => m.InternalDate).ThenByDescending(m => m.Uid).ToList();
        IEnumerable<MessageListItem> items;
        int total;
        if (string.IsNullOrWhiteSpace(search))
        {
            total = all.Count;
            items = all.Skip(page * pageSize).Take(pageSize).Select(m => new MessageListItem(m, Summary(m)));
        }
        else
        {
            var term = search.Trim();
            var matches = all.Select(m => new MessageListItem(m, Summary(m)))
                .Where(i => i.Summary.Subject.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                            i.Summary.From.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                            i.Summary.FromAddress.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                            i.Summary.To.Contains(term, StringComparison.OrdinalIgnoreCase))
                .ToList();
            total = matches.Count;
            items = matches.Skip(page * pageSize).Take(pageSize);
        }

        return (items.ToList(), total);
    }

    public MimeMessage Load(StoredMessage message)
    {
        using var stream = mailboxes.OpenMessage(message);
        return MimeMessage.Load(stream);
    }

    public string Path(StoredMessage message) => mailboxes.GetMessagePath(message);

    public MessageSummary Summary(StoredMessage message)
    {
        if (_summaries.TryGetValue(message.Id, out var cached))
        {
            return cached;
        }

        MessageSummary summary;
        try
        {
            using var stream = File.OpenRead(mailboxes.GetMessagePath(message));
            var headers = HeaderList.Load(stream);
            var from = InternetAddressList.TryParse(headers[HeaderId.From] ?? "", out var fromList) ? fromList.Mailboxes.FirstOrDefault() : null;
            var to = InternetAddressList.TryParse(headers[HeaderId.To] ?? "", out var toList)
                ? string.Join(", ", toList.Mailboxes.Select(m => string.IsNullOrEmpty(m.Name) ? m.Address : m.Name))
                : "";
            var contentType = headers[HeaderId.ContentType] ?? "";
            summary = new MessageSummary(
                from is null ? "(unbekannt)" : string.IsNullOrEmpty(from.Name) ? from.Address : from.Name,
                from?.Address ?? "",
                to,
                headers[HeaderId.Subject] is { Length: > 0 } subject ? subject : "(kein Betreff)",
                contentType.Contains("multipart/mixed", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or FormatException)
        {
            summary = new MessageSummary("(nicht lesbar)", "", "", "(nicht lesbar)", false);
        }

        if (_summaries.Count > MaxCachedSummaries)
        {
            _summaries.Clear();
        }

        _summaries[message.Id] = summary;
        return summary;
    }
}
