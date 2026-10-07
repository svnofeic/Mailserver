using Mailserver.Core.Accounts;
using Mailserver.Core.SpamLogging;
using Mailserver.Core.Storage;

namespace Mailserver.Web.Webmail;

public sealed record MailActionResult(string? Message, string? Error = null);

/// <summary>Flag, move and delete operations from the message list and the reading view.</summary>
public sealed class MailActions(MailboxStore mailboxes, SpamFeedback feedback)
{
    public MailActionResult Apply(Account account, Folder source, IReadOnlyCollection<long> uids, string op, string? target)
    {
        var count = uids.Count;
        switch (op)
        {
            case "read":
                mailboxes.UpdateFlags(source.Id, uids, FlagOperation.Add, [MessageFlags.Seen]);
                return new($"{count} als gelesen markiert.");
            case "unread":
                mailboxes.UpdateFlags(source.Id, uids, FlagOperation.Remove, [MessageFlags.Seen]);
                return new($"{count} als ungelesen markiert.");
            case "flag":
                mailboxes.UpdateFlags(source.Id, uids, FlagOperation.Add, [MessageFlags.Flagged]);
                return new($"{count} markiert.");
            case "unflag":
                mailboxes.UpdateFlags(source.Id, uids, FlagOperation.Remove, [MessageFlags.Flagged]);
                return new("Markierung entfernt.");
            case "delete" when source.Name == "Trash":
                mailboxes.UpdateFlags(source.Id, uids, FlagOperation.Add, [MessageFlags.Deleted]);
                mailboxes.Expunge(source.Id, uids);
                return new($"{count} endgültig gelöscht.");
            case "delete":
                return Move(account, source, uids, "Trash", $"{count} in den Papierkorb verschoben.");
            case "spam":
                return Move(account, source, uids, SpamFeedback.JunkFolder, $"{count} als Spam verschoben.");
            case "notspam":
                return Move(account, source, uids, MailboxStore.Inbox, $"{count} in den Posteingang verschoben.");
            case "move" when !string.IsNullOrEmpty(target):
                return Move(account, source, uids, target, $"{count} nach „{Format.FolderName(target)}“ verschoben.");
            default:
                return new(null, "Unbekannte Aktion.");
        }
    }

    /// <summary>Folders whose whole content can be deleted at once.</summary>
    public static bool CanEmpty(Folder folder) => folder.Name is "Trash" or SpamFeedback.JunkFolder;

    /// <summary>Deletes every message in the trash or the spam folder for good.</summary>
    public MailActionResult Empty(Folder folder)
    {
        if (!CanEmpty(folder))
        {
            return new(null, "Nur Papierkorb und Spam lassen sich auf einmal leeren.");
        }

        var uids = mailboxes.ListMessages(folder.Id).Select(m => m.Uid).ToList();
        if (uids.Count == 0)
        {
            return new($"„{Format.FolderName(folder.Name)}“ ist bereits leer.");
        }

        // Only these: a message arriving in the meantime is not flagged and stays.
        mailboxes.UpdateFlags(folder.Id, uids, FlagOperation.Add, [MessageFlags.Deleted]);
        var removed = mailboxes.Expunge(folder.Id, uids).Count;
        return new($"„{Format.FolderName(folder.Name)}“ geleert: {removed} Nachricht(en) endgültig gelöscht.");
    }

    private MailActionResult Move(Account account, Folder source, IReadOnlyCollection<long> uids, string targetName, string message)
    {
        var target = targetName is "Trash" or "Junk" or MailboxStore.Inbox
            ? mailboxes.GetOrCreateFolder(account.Id, targetName)
            : mailboxes.GetFolder(account.Id, targetName);
        if (target is null || target.Id == source.Id)
        {
            return new(null, "Ungültiger Zielordner.");
        }

        var messages = uids.Distinct().Select(uid => mailboxes.GetMessage(source.Id, uid)).OfType<StoredMessage>().ToList();
        feedback.Record(account, source, messages, target);
        mailboxes.Move(source.Id, uids, target.Id);
        return new(message);
    }
}
