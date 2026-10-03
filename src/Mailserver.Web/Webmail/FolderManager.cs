using Mailserver.Core.Accounts;
using Mailserver.Core.Rules;
using Mailserver.Core.Storage;

namespace Mailserver.Web.Webmail;

/// <summary>Creating, renaming and deleting a user's own folders from webmail.</summary>
public sealed class FolderManager(MailboxStore mailboxes, RuleStore rules)
{
    public MailActionResult Create(Account account, string? parent, string? name)
    {
        var fullName = string.IsNullOrEmpty(parent) ? name?.Trim() : $"{parent}{MailboxStore.HierarchyDelimiter}{name?.Trim()}";
        if (name?.Contains(MailboxStore.HierarchyDelimiter) == true)
        {
            return new(null, "Der Name darf keinen Schrägstrich enthalten – für Unterordner bitte den übergeordneten Ordner auswählen.");
        }

        if (fullName is not null && mailboxes.AliasTarget(account.Id, fullName) is { } special)
        {
            return new(null, $"„{fullName}“ gibt es schon: das ist der Ordner „{Format.FolderName(special)}“.");
        }

        if (MailboxStore.ValidateFolderName(fullName) is { } error)
        {
            return new(null, error);
        }

        if (!string.IsNullOrEmpty(parent) && mailboxes.GetFolder(account.Id, parent) is null)
        {
            return new(null, "Der übergeordnete Ordner existiert nicht.");
        }

        return mailboxes.CreateFolder(account.Id, fullName!) is null
            ? new(null, $"Der Ordner „{fullName}“ existiert bereits.")
            : new($"Ordner „{fullName}“ angelegt.");
    }

    /// <summary>Renames a folder (keeping its place in the hierarchy) and updates rules that move mail into it.</summary>
    public MailActionResult Rename(Account account, string? name, string? newLeafName)
    {
        if (string.IsNullOrEmpty(name) || mailboxes.GetFolder(account.Id, name) is null)
        {
            return new(null, "Ordner nicht gefunden.");
        }

        if (MailboxStore.IsSystemFolder(name))
        {
            return new(null, "Systemordner können nicht umbenannt werden.");
        }

        if (newLeafName?.Contains(MailboxStore.HierarchyDelimiter) == true)
        {
            return new(null, "Der Name darf keinen Schrägstrich enthalten.");
        }

        var parentEnd = name.LastIndexOf(MailboxStore.HierarchyDelimiter);
        var newName = (parentEnd < 0 ? "" : name[..(parentEnd + 1)]) + newLeafName?.Trim();
        if (MailboxStore.ValidateFolderName(newName) is { } error)
        {
            return new(null, error);
        }

        if (MailboxStore.IsSystemFolder(newName) || !mailboxes.RenameFolder(account.Id, name, newName))
        {
            return new(null, $"Der Ordner „{newName}“ existiert bereits.");
        }

        var prefix = name + MailboxStore.HierarchyDelimiter;
        foreach (var rule in rules.List(account.Address.ToString()).Where(r => r.Action == RuleAction.Move && r.Argument is not null))
        {
            var target = rule.Argument!;
            if (target == name || target.StartsWith(prefix, StringComparison.Ordinal))
            {
                rules.Update(rule.Id, rule.Name.Replace(name, newName), rule.Conditions, rule.Action, newName + target[name.Length..],
                    rule.MatchAll, rule.Stop, rule.Priority, rule.Enabled);
            }
        }

        return new($"Ordner in „{newName}“ umbenannt.");
    }

    /// <summary>Deletes a folder without subfolders; its messages go to the Trash instead of being lost.</summary>
    public MailActionResult Delete(Account account, string? name)
    {
        if (string.IsNullOrEmpty(name) || mailboxes.GetFolder(account.Id, name) is not { } folder)
        {
            return new(null, "Ordner nicht gefunden.");
        }

        if (MailboxStore.IsSystemFolder(name))
        {
            return new(null, "Systemordner können nicht gelöscht werden.");
        }

        if (mailboxes.ListFolders(account.Id).Any(f => f.Name.StartsWith(name + MailboxStore.HierarchyDelimiter, StringComparison.Ordinal)))
        {
            return new(null, "Bitte zuerst die Unterordner löschen.");
        }

        var uids = mailboxes.ListMessages(folder.Id).Select(m => m.Uid).ToList();
        if (uids.Count > 0)
        {
            mailboxes.Move(folder.Id, uids, mailboxes.GetOrCreateFolder(account.Id, "Trash").Id);
        }

        mailboxes.DeleteFolder(account.Id, name);
        return new(uids.Count > 0
            ? $"Ordner „{name}“ gelöscht, {uids.Count} Nachricht(en) in den Papierkorb verschoben."
            : $"Ordner „{name}“ gelöscht.");
    }
}
