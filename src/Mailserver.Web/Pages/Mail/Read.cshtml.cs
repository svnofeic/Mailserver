using Mailserver.Core.Accounts;
using Mailserver.Core.Storage;
using Mailserver.Web.Webmail;
using Microsoft.AspNetCore.Mvc;
using MimeKit;

namespace Mailserver.Web.Pages.Mail;

public sealed class ReadModel(WebmailStore store, MailboxStore mailboxes, MailActions actions, FollowUps followUps,
    Conversations conversations, AccountPrefs prefs) : MailPageModel
{
    /// <summary>Another message of the conversation; <paramref name="Open"/> if it was unread (shown expanded, like in Gmail).</summary>
    public sealed record ThreadEntry(Folder Folder, StoredMessage Message, MessageSummary Summary, bool Open);

    /// <summary>The older messages of the conversation (above the opened one) and the newer ones (below).</summary>
    public IReadOnlyList<ThreadEntry> Before { get; private set; } = [];
    public IReadOnlyList<ThreadEntry> After { get; private set; } = [];
    public int ThreadCount => Before.Count + After.Count + 1;

    public IReadOnlyList<(string Key, string Label)> FollowUpChoices => LaterChoices.FollowUp;

    /// <summary>"Wiedervorlage": the message leaves the folder and comes back unread at the chosen time.</summary>
    public IActionResult OnPostFollowUp(string folder, long uid, string? choice, string? at)
    {
        if (store.Folder(CurrentAccount.Id, folder) is not { } selected || store.Message(selected, uid) is not { } stored)
        {
            return NotFound();
        }

        if (LaterChoices.Resolve(choice, at, DateTimeOffset.Now) is not { } due || due <= DateTimeOffset.Now)
        {
            ErrorMessage = "Bitte einen Zeitpunkt in der Zukunft wählen.";
            return Redirect($"/Mail/Read?folder={Uri.EscapeDataString(folder)}&uid={uid}");
        }

        // Coming from the follow-up folder itself (a new time): back into the folder it originally came from.
        var returnFolder = selected.Name == FollowUps.FollowUpFolder
            ? followUps.FollowUpsOf(CurrentAccount.Id).FirstOrDefault(f => f.FileName == stored.FileName)?.ReturnFolder ?? MailboxStore.Inbox
            : selected.Name;
        followUps.AddFollowUp(CurrentAccount.Id, stored.FileName, returnFolder, due);
        if (selected.Name != FollowUps.FollowUpFolder)
        {
            var target = mailboxes.GetOrCreateFolder(CurrentAccount.Id, FollowUps.FollowUpFolder);
            mailboxes.Move(selected.Id, [uid], target.Id);
        }

        Message = $"Wiedervorlage am {Format.Time(due)}: Die Nachricht kommt dann ungelesen in „{Format.FolderName(returnFolder)}“ zurück.";
        return Redirect($"/Mail?folder={Uri.EscapeDataString(folder)}");
    }

    public Folder Folder { get; private set; } = null!;
    public IReadOnlyList<(Folder Folder, FolderStatus Status)> Folders { get; private set; } = [];
    public StoredMessage Stored { get; private set; } = null!;
    public MimeMessage Mail { get; private set; } = null!;
    public string Subject => string.IsNullOrWhiteSpace(Mail.Subject) ? "(kein Betreff)" : Mail.Subject;
    public bool ShowImages { get; private set; }
    public bool HasRemoteContent { get; private set; }
    public string? SpamScore { get; private set; }
    public List<(string Name, string Type, long Size, bool Viewable)> Attachments { get; } = [];

    public async Task<IActionResult> OnGetAsync(string folder, long uid, int images = 0, CancellationToken cancellationToken = default)
    {
        if (store.Folder(CurrentAccount.Id, folder) is not { } selected || store.Message(selected, uid) is not { } stored)
        {
            return NotFound();
        }

        Folder = selected;
        Stored = stored;
        Mail = store.Load(stored);
        ShowImages = images == 1;
        HasRemoteContent = MailRenderer.HasRemoteContent(Mail);
        SpamScore = Mail.Headers["X-Spam-Score"];
        foreach (var attachment in MailAttachments.List(Mail))
        {
            Attachments.Add((attachment.Name, attachment.ContentType, attachment.Size, MailAttachments.CanView(attachment)));
        }

        if (!stored.HasFlag(MessageFlags.Seen))
        {
            mailboxes.UpdateFlags(selected.Id, [uid], FlagOperation.Add, [MessageFlags.Seen]);
        }

        if (prefs.GetBool(CurrentAccount.Id, AccountPrefs.Conversations, true) && !Conversations.FlatFolders.Contains(selected.Name))
        {
            await conversations.IndexPendingAsync(200, CurrentAccount.Id, cancellationToken);
            LoadThread(selected, stored);
        }

        Folders = store.Folders(CurrentAccount.Id);
        return Page();
    }

    private void LoadThread(Folder selected, StoredMessage stored)
    {
        var thread = conversations.Thread(CurrentAccount.Id, selected, stored);
        var position = thread.ToList().FindIndex(t => t.Message.Id == stored.Id);
        if (thread.Count < 2 || position < 0)
        {
            return;
        }

        var entries = new List<ThreadEntry>();
        foreach (var (folder, message) in thread)
        {
            var unread = !message.HasFlag(MessageFlags.Seen) && message.Id != stored.Id;
            if (unread && folder.Name is not ("Drafts" or "Sent"))
            {
                // Opening the conversation reads it as a whole.
                mailboxes.UpdateFlags(folder.Id, [message.Uid], FlagOperation.Add, [MessageFlags.Seen]);
            }

            entries.Add(new ThreadEntry(folder, message, store.Summary(message), unread));
        }

        Before = entries.Take(position).ToList();
        After = entries.Skip(position + 1).ToList();
    }

    public IActionResult OnPostAction(string folder, long uid, string op, string? target)
    {
        if (store.Folder(CurrentAccount.Id, folder) is not { } selected || store.Message(selected, uid) is null)
        {
            return NotFound();
        }

        var result = actions.Apply(CurrentAccount, selected, [uid], op, target);
        if (result.Error is not null)
        {
            ErrorMessage = result.Error;
            return Redirect($"/Mail/Read?folder={Uri.EscapeDataString(folder)}&uid={uid}");
        }

        Message = result.Message;
        // After moving or deleting, the message is gone from this folder; flag changes stay on the message.
        return op is "flag" or "unflag"
            ? Redirect($"/Mail/Read?folder={Uri.EscapeDataString(folder)}&uid={uid}")
            : Redirect($"/Mail?folder={Uri.EscapeDataString(folder)}");
    }
}
