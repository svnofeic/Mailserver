using Mailserver.Core.Storage;
using Mailserver.Web.Webmail;
using Microsoft.AspNetCore.Mvc;

namespace Mailserver.Web.Pages.Mail;

/// <summary>Mail waiting to be sent later and mail waiting to come back ("Wiedervorlage").</summary>
public sealed class LaterModel(FollowUps followUps, WebmailStore store, MailboxStore mailboxes,
    ScheduledMailService service) : MailPageModel
{
    public sealed record Entry(long Id, DateTimeOffset When, Folder Folder, StoredMessage Message, MessageSummary Summary, string? Error, string? ReturnFolder);

    public IReadOnlyList<(Folder Folder, FolderStatus Status)> Folders { get; private set; } = [];
    public IReadOnlyList<Entry> Scheduled { get; private set; } = [];
    public IReadOnlyList<Entry> Reminders { get; private set; } = [];

    public void OnGet()
    {
        Folders = store.Folders(CurrentAccount.Id);
        Scheduled = followUps.Scheduled(CurrentAccount.Id)
            .Select(s => followUps.Locate(CurrentAccount.Id, s.FileName) is { } at && at.Folder.Name == FollowUps.ScheduledFolder
                ? new Entry(s.Id, s.SendAt, at.Folder, at.Message, store.Summary(at.Message), s.LastError, null)
                : null)
            .OfType<Entry>().ToList();
        Reminders = followUps.FollowUpsOf(CurrentAccount.Id)
            .Select(f => followUps.Locate(CurrentAccount.Id, f.FileName) is { } at
                ? new Entry(f.Id, f.Due, at.Folder, at.Message, store.Summary(at.Message), null, f.ReturnFolder)
                : null)
            .OfType<Entry>().ToList();
    }

    public async Task<IActionResult> OnPostSendNowAsync(long id, CancellationToken cancellationToken)
    {
        if (followUps.GetScheduled(CurrentAccount.Id, id) is not { } scheduled)
        {
            return NotFound();
        }

        try
        {
            await service.SendNowAsync(CurrentAccount, scheduled, cancellationToken);
            Message = "Nachricht gesendet.";
        }
        catch (ComposeException ex)
        {
            followUps.SendFailed(scheduled.Id, ex.Message);
            ErrorMessage = ex.Message;
        }

        return RedirectToPage();
    }

    /// <summary>Not sending after all: the message goes to the drafts.</summary>
    public IActionResult OnPostCancel(long id)
    {
        if (followUps.GetScheduled(CurrentAccount.Id, id) is not { } scheduled)
        {
            return NotFound();
        }

        if (followUps.Locate(CurrentAccount.Id, scheduled.FileName) is { } at)
        {
            var drafts = mailboxes.GetOrCreateFolder(CurrentAccount.Id, "Drafts");
            var uid = mailboxes.Move(at.Folder.Id, [at.Message.Uid], drafts.Id).Single().TargetUid;
            mailboxes.UpdateFlags(drafts.Id, [uid], FlagOperation.Add, [MessageFlags.Draft]);
        }

        followUps.RemoveScheduled(scheduled.Id);
        Message = "Nicht gesendet – die Nachricht liegt jetzt in den Entwürfen.";
        return RedirectToPage();
    }

    public IActionResult OnPostRetry(long id)
    {
        if (followUps.GetScheduled(CurrentAccount.Id, id) is not { } scheduled)
        {
            return NotFound();
        }

        // Due right away; the service sends it within half a minute.
        followUps.SetSendTime(scheduled.Id, DateTimeOffset.UtcNow);
        Message = "Wird gleich erneut versucht.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostBringBackAsync(long id, CancellationToken cancellationToken)
    {
        if (followUps.GetFollowUp(CurrentAccount.Id, id) is not { } reminder)
        {
            return NotFound();
        }

        followUps.AddFollowUp(CurrentAccount.Id, reminder.FileName, reminder.ReturnFolder, DateTimeOffset.UtcNow.AddSeconds(-1));
        await service.RunOnceAsync(cancellationToken);
        Message = $"Die Nachricht ist wieder in „{Format.FolderName(reminder.ReturnFolder)}“.";
        return RedirectToPage();
    }
}
