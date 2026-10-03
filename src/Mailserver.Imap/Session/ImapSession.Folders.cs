using System.Text;
using System.Text.RegularExpressions;
using Mailserver.Core.Storage;
using Mailserver.Imap.Protocol;

namespace Mailserver.Imap.Session;

public sealed partial class ImapSession
{
    private static readonly Dictionary<string, string> SpecialUse = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sent"] = @"\Sent",
        ["Drafts"] = @"\Drafts",
        ["Trash"] = @"\Trash",
        ["Junk"] = @"\Junk",
        ["Archive"] = @"\Archive",
    };

    private const string SystemFlags = @"\Answered \Flagged \Deleted \Seen \Draft";

    private async Task SelectAsync(ImapCommand command, IReadOnlyList<ImapToken> args, bool readOnly, CancellationToken cancellationToken)
    {
        if (args.Count < 1)
        {
            throw new ImapParseException("Missing folder name");
        }

        // A failed SELECT leaves the session without a selected folder (RFC 3501 6.3.1).
        _selected = null;
        var folder = mailboxes.GetFolder(_account!.Id, DecodeFolderName(args[0]));
        if (folder is null)
        {
            await Respond(command, null, "NO [NONEXISTENT] Folder does not exist", cancellationToken);
            return;
        }

        var selected = new SelectedFolder(mailboxes, folder, readOnly);
        _selected = selected;

        var keywords = selected.Uids
            .Select(uid => selected.GetMessage(uid))
            .SelectMany(m => m?.FlagList ?? [])
            .Where(f => !f.StartsWith('\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var flags = string.Join(' ', new[] { SystemFlags }.Concat(keywords));

        var response = new ImapResponse()
            .Raw($"* FLAGS ({flags})\r\n")
            .Raw(readOnly ? "* OK [PERMANENTFLAGS ()] Read-only\r\n" : $"* OK [PERMANENTFLAGS ({flags} \\*)] Flags permitted\r\n")
            .Raw($"* {selected.Count} EXISTS\r\n")
            .Raw("* 0 RECENT\r\n");

        var firstUnseen = selected.Uids.Select((uid, index) => (uid, index))
            .FirstOrDefault(x => selected.GetMessage(x.uid) is { } m && !m.HasFlag(MessageFlags.Seen));
        if (firstUnseen.uid != 0)
        {
            response.Raw($"* OK [UNSEEN {firstUnseen.index + 1}] First unseen\r\n");
        }

        response
            .Raw($"* OK [UIDVALIDITY {folder.UidValidity}] UIDs valid\r\n")
            .Raw($"* OK [UIDNEXT {folder.UidNext}] Predicted next UID\r\n")
            .Raw($"{command.Tag} OK [{(readOnly ? "READ-ONLY" : "READ-WRITE")}] {command.Name} completed\r\n");
        await connection.WriteAsync(response.ToMemory(), cancellationToken);
    }

    private async Task CloseAsync(ImapCommand command, bool expunge, CancellationToken cancellationToken)
    {
        if (expunge && !_selected!.ReadOnly)
        {
            // CLOSE expunges silently (RFC 3501 6.4.2).
            mailboxes.Expunge(_selected.Folder.Id);
        }

        _selected = null;
        await Respond(command, null, $"OK {command.Name} completed", cancellationToken);
    }

    private async Task CreateAsync(ImapCommand command, IReadOnlyList<ImapToken> args, CancellationToken cancellationToken)
    {
        var name = DecodeFolderName(RequireArgument(args, 0));
        if (!IsValidFolderName(name) || name == MailboxStore.Inbox)
        {
            await Respond(command, null, "NO [CANNOT] Invalid folder name", cancellationToken);
            return;
        }

        if (mailboxes.GetFolder(_account!.Id, name) is not null)
        {
            await Respond(command, null, "NO [ALREADYEXISTS] Folder already exists", cancellationToken);
            return;
        }

        CreateParents(name);
        mailboxes.CreateFolder(_account.Id, name);
        await Respond(command, null, "OK CREATE completed", cancellationToken);
    }

    private async Task DeleteAsync(ImapCommand command, IReadOnlyList<ImapToken> args, CancellationToken cancellationToken)
    {
        var name = DecodeFolderName(RequireArgument(args, 0));
        if (name == MailboxStore.Inbox)
        {
            await Respond(command, null, "NO [CANNOT] INBOX cannot be deleted", cancellationToken);
            return;
        }

        var folders = mailboxes.ListFolders(_account!.Id);
        if (folders.All(f => f.Name != name))
        {
            await Respond(command, null, "NO [NONEXISTENT] Folder does not exist", cancellationToken);
            return;
        }

        if (folders.Any(f => f.Name.StartsWith(name + MailboxStore.HierarchyDelimiter, StringComparison.Ordinal)))
        {
            await Respond(command, null, "NO [INUSE] Delete the subfolders first", cancellationToken);
            return;
        }

        if (_selected?.Folder.Name == name)
        {
            _selected = null;
        }

        mailboxes.DeleteFolder(_account.Id, name);
        await Respond(command, null, "OK DELETE completed", cancellationToken);
    }

    private async Task RenameAsync(ImapCommand command, IReadOnlyList<ImapToken> args, CancellationToken cancellationToken)
    {
        var from = DecodeFolderName(RequireArgument(args, 0));
        var to = DecodeFolderName(RequireArgument(args, 1));
        if (from == MailboxStore.Inbox || to == MailboxStore.Inbox || !IsValidFolderName(to) ||
            to.StartsWith(from + MailboxStore.HierarchyDelimiter, StringComparison.Ordinal))
        {
            await Respond(command, null, "NO [CANNOT] This rename is not possible", cancellationToken);
            return;
        }

        if (mailboxes.GetFolder(_account!.Id, from) is null)
        {
            await Respond(command, null, "NO [NONEXISTENT] Folder does not exist", cancellationToken);
            return;
        }

        CreateParents(to);
        if (!mailboxes.RenameFolder(_account.Id, from, to))
        {
            await Respond(command, null, "NO [ALREADYEXISTS] Target folder already exists", cancellationToken);
            return;
        }

        await Respond(command, null, "OK RENAME completed", cancellationToken);
    }

    private async Task SubscribeAsync(ImapCommand command, IReadOnlyList<ImapToken> args, bool subscribe, CancellationToken cancellationToken)
    {
        var name = DecodeFolderName(RequireArgument(args, 0));
        if (!mailboxes.SetSubscribed(_account!.Id, name, subscribe) && subscribe)
        {
            await Respond(command, null, "NO [NONEXISTENT] Folder does not exist", cancellationToken);
            return;
        }

        await Respond(command, null, $"OK {command.Name} completed", cancellationToken);
    }

    private async Task ListAsync(ImapCommand command, IReadOnlyList<ImapToken> args, bool lsub, CancellationToken cancellationToken)
    {
        // LIST [(selection options)] reference pattern [RETURN (options)] — options are accepted; SPECIAL-USE attributes are always returned.
        var position = 0;
        var selection = new List<string>();
        if (!lsub && args.Count > 0 && args[0] is ListToken selectionList)
        {
            selection.AddRange(selectionList.Items.Select(i => i.AsAtom().ToUpperInvariant()));
            position = 1;
        }

        if (args.Count < position + 2)
        {
            throw new ImapParseException("LIST expects reference and pattern");
        }

        var reference = ModifiedUtf7.Decode(args[position].AsString());
        var patterns = args[position + 1] is ListToken patternList
            ? patternList.Items.Select(p => ModifiedUtf7.Decode(p.AsString())).ToList()
            : [ModifiedUtf7.Decode(args[position + 1].AsString())];

        var response = new ImapResponse();
        if (patterns is [""])
        {
            response.Raw($"* {command.Name} (\\Noselect) \"/\" \"\"\r\n");
        }
        else
        {
            var folders = mailboxes.ListFolders(_account!.Id);
            var existing = folders.ToDictionary(f => f.Name);
            var allNames = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var folder in folders)
            {
                allNames.Add(folder.Name);
                // Missing parents (e.g. only "a/b" exists) are listed as \Noselect placeholders.
                for (var i = folder.Name.IndexOf(MailboxStore.HierarchyDelimiter); i > 0; i = folder.Name.IndexOf(MailboxStore.HierarchyDelimiter, i + 1))
                {
                    allNames.Add(folder.Name[..i]);
                }
            }

            var matchers = patterns.Select(p => BuildMatcher(reference + p)).ToList();
            foreach (var name in allNames)
            {
                if (!matchers.Any(m => m.IsMatch(name)))
                {
                    continue;
                }

                existing.TryGetValue(name, out var folder);
                var subscribed = folder?.Subscribed ?? false;
                var specialUse = SpecialUse.GetValueOrDefault(name);
                if ((lsub || selection.Contains("SUBSCRIBED")) && !subscribed)
                {
                    continue;
                }

                if (selection.Contains("SPECIAL-USE") && specialUse is null)
                {
                    continue;
                }

                var attributes = new List<string>();
                if (folder is null)
                {
                    attributes.Add(@"\Noselect");
                }

                var hasChildren = allNames.Any(n => n.StartsWith(name + MailboxStore.HierarchyDelimiter, StringComparison.Ordinal));
                attributes.Add(hasChildren ? @"\HasChildren" : @"\HasNoChildren");
                if (specialUse is not null)
                {
                    attributes.Add(specialUse);
                }

                if (subscribed && !lsub && selection.Contains("SUBSCRIBED"))
                {
                    attributes.Add(@"\Subscribed");
                }

                response.Raw($"* {command.Name} ({string.Join(' ', attributes)}) \"/\" ").String(ModifiedUtf7.Encode(name)).Line();
            }
        }

        response.Raw($"{command.Tag} OK {command.Name} completed\r\n");
        await connection.WriteAsync(response.ToMemory(), cancellationToken);
    }

    private async Task StatusAsync(ImapCommand command, IReadOnlyList<ImapToken> args, CancellationToken cancellationToken)
    {
        var name = DecodeFolderName(RequireArgument(args, 0));
        var folder = mailboxes.GetFolder(_account!.Id, name);
        if (folder is null)
        {
            await Respond(command, null, "NO [NONEXISTENT] Folder does not exist", cancellationToken);
            return;
        }

        var status = mailboxes.GetStatus(folder.Id);
        var items = RequireArgument(args, 1).AsList().Select(i => i.AsAtom().ToUpperInvariant()).Select(item => item switch
        {
            "MESSAGES" => $"MESSAGES {status.Messages}",
            "RECENT" => "RECENT 0",
            "UIDNEXT" => $"UIDNEXT {status.UidNext}",
            "UIDVALIDITY" => $"UIDVALIDITY {status.UidValidity}",
            "UNSEEN" => $"UNSEEN {status.Unseen}",
            _ => throw new ImapParseException($"Unknown STATUS item {item}"),
        });

        var response = new ImapResponse().Raw("* STATUS ").String(ModifiedUtf7.Encode(name)).Raw($" ({string.Join(' ', items)})\r\n");
        response.Raw($"{command.Tag} OK STATUS completed\r\n");
        await connection.WriteAsync(response.ToMemory(), cancellationToken);
    }

    private void CreateParents(string name)
    {
        for (var i = name.IndexOf(MailboxStore.HierarchyDelimiter); i > 0; i = name.IndexOf(MailboxStore.HierarchyDelimiter, i + 1))
        {
            mailboxes.GetOrCreateFolder(_account!.Id, name[..i]);
        }
    }

    private static bool IsValidFolderName(string name) =>
        name.Length is > 0 and <= 255 &&
        !name.Contains("//", StringComparison.Ordinal) &&
        !name.StartsWith(MailboxStore.HierarchyDelimiter) &&
        !name.Any(c => char.IsControl(c) || c is '*' or '%');

    /// <summary>"*" matches anything, "%" anything but the hierarchy delimiter. INBOX matches case-insensitively.</summary>
    private static Regex BuildMatcher(string pattern)
    {
        var regex = new StringBuilder("^");
        foreach (var c in pattern)
        {
            regex.Append(c switch
            {
                '*' => ".*",
                '%' => "[^/]*",
                _ => Regex.Escape(c.ToString()),
            });
        }

        regex.Append('$');
        var expression = regex.ToString();
        if (pattern.StartsWith(MailboxStore.Inbox, StringComparison.OrdinalIgnoreCase))
        {
            expression = "^(?i:INBOX)" + expression[(1 + Regex.Escape(MailboxStore.Inbox).Length)..];
        }

        return new Regex(expression, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    private static ImapToken RequireArgument(IReadOnlyList<ImapToken> args, int index) =>
        index < args.Count ? args[index] : throw new ImapParseException("Missing argument");
}
