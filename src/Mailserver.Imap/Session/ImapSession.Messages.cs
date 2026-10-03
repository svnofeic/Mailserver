using System.Globalization;
using Mailserver.Core.SpamLogging;
using Mailserver.Core.Storage;
using Mailserver.Imap.Mime;
using Mailserver.Imap.Protocol;

namespace Mailserver.Imap.Session;

public sealed partial class ImapSession
{
    private MessageCache? _cache;

    private MessageCache Cache => _cache ??= new MessageCache(mailboxes);

    // ---- FETCH ----

    private async Task FetchAsync(ImapCommand command, IReadOnlyList<ImapToken> args, bool byUid, CancellationToken cancellationToken)
    {
        if (args.Count != 2)
        {
            throw new ImapParseException("FETCH expects a sequence set and items");
        }

        var selected = _selected!;
        var set = SequenceSet.Parse(args[0].AsAtom());
        var items = ExpandFetchItems(args[1]);
        if (byUid && !items.Contains("UID"))
        {
            items.Insert(0, "UID");
        }

        var sections = items.Where(i => i.StartsWith("BODY[", StringComparison.Ordinal) || i.StartsWith("BODY.PEEK[", StringComparison.Ordinal))
            .ToDictionary(i => i, i => BodySection.TryParse(i) ?? throw new ImapParseException($"Invalid section {i}"));
        var needsContent = sections.Count > 0 || items.Any(i => i is "ENVELOPE" or "BODY" or "BODYSTRUCTURE" or "RFC822" or "RFC822.HEADER" or "RFC822.TEXT");
        var marksSeen = !selected.ReadOnly && (sections.Values.Any(s => !s.Peek) || items.Any(i => i is "RFC822" or "RFC822.TEXT"));

        var prelude = new ImapResponse();
        selected.Sync(prelude, allowExpunge: byUid);
        if (!prelude.IsEmpty)
        {
            await connection.WriteAsync(prelude.ToMemory(), cancellationToken);
        }

        foreach (var (sequence, uid) in selected.Resolve(set, byUid))
        {
            var message = selected.GetMessage(uid);
            if (message is null)
            {
                continue; // expunged by another session; reported at the next opportunity
            }

            var content = needsContent ? await Cache.GetAsync(message, cancellationToken) : null;
            if (needsContent && content is null)
            {
                continue;
            }

            var includeFlags = items.Contains("FLAGS");
            if (marksSeen && !message.HasFlag(MessageFlags.Seen))
            {
                var updated = mailboxes.UpdateFlags(message.FolderId, [uid], FlagOperation.Add, [MessageFlags.Seen]);
                if (updated.Count > 0)
                {
                    message = updated[0];
                    selected.RememberFlags(message);
                }

                includeFlags = true;
            }

            var response = new ImapResponse().Raw($"* {sequence} FETCH (");
            var first = true;
            void Separator()
            {
                if (!first)
                {
                    response.Raw(" ");
                }

                first = false;
            }

            if (items.Contains("UID"))
            {
                Separator();
                response.Raw($"UID {uid}");
            }

            if (includeFlags)
            {
                Separator();
                response.Raw($"FLAGS ({message.Flags})");
            }

            foreach (var item in items)
            {
                switch (item)
                {
                    case "UID":
                    case "FLAGS":
                        break;
                    case "INTERNALDATE":
                        Separator();
                        response.Raw($"INTERNALDATE \"{FormatInternalDate(message.InternalDate)}\"");
                        break;
                    case "RFC822.SIZE":
                        Separator();
                        response.Raw($"RFC822.SIZE {message.Size}");
                        break;
                    case "ENVELOPE":
                        Separator();
                        response.Raw("ENVELOPE ");
                        StructureWriter.WriteEnvelope(response, content!.Structure);
                        break;
                    case "BODY":
                    case "BODYSTRUCTURE":
                        Separator();
                        response.Raw(item).Raw(" ");
                        StructureWriter.WriteBodyStructure(response, content!.Structure, extensible: item == "BODYSTRUCTURE");
                        break;
                    case "RFC822":
                        Separator();
                        response.Raw("RFC822 ").Literal(content!.Data);
                        break;
                    case "RFC822.HEADER":
                        Separator();
                        response.Raw("RFC822.HEADER ").Literal(content!.Structure.HeaderBytes);
                        break;
                    case "RFC822.TEXT":
                        Separator();
                        response.Raw("RFC822.TEXT ").Literal(content!.Structure.BodyBytes);
                        break;
                    default:
                        var section = sections[item];
                        Separator();
                        response.Raw(section.ResponseName).Raw(" ").Literal(section.Extract(content!.Structure));
                        break;
                }
            }

            response.Raw(")\r\n");
            await connection.WriteAsync(response.ToMemory(), cancellationToken);
        }

        await Respond(command, null, $"OK {(byUid ? "UID " : "")}FETCH completed", cancellationToken);
    }

    private static List<string> ExpandFetchItems(ImapToken token)
    {
        var items = token.AsList().Select(i => i.AsAtom()).ToList();
        if (items.Count == 1)
        {
            switch (items[0].ToUpperInvariant())
            {
                case "ALL":
                    return ["FLAGS", "INTERNALDATE", "RFC822.SIZE", "ENVELOPE"];
                case "FAST":
                    return ["FLAGS", "INTERNALDATE", "RFC822.SIZE"];
                case "FULL":
                    return ["FLAGS", "INTERNALDATE", "RFC822.SIZE", "ENVELOPE", "BODY"];
            }
        }

        var result = new List<string>();
        foreach (var item in items)
        {
            // Section specifiers keep their case for header names; the keyword part is upper-cased.
            var bracket = item.IndexOf('[');
            var normalized = bracket < 0 ? item.ToUpperInvariant() : item[..bracket].ToUpperInvariant() + item[bracket..];
            if (bracket < 0 && normalized is not ("UID" or "FLAGS" or "INTERNALDATE" or "RFC822.SIZE" or "ENVELOPE" or "BODY" or "BODYSTRUCTURE"
                    or "RFC822" or "RFC822.HEADER" or "RFC822.TEXT"))
            {
                throw new ImapParseException($"Unknown FETCH item {item}");
            }

            if (!result.Contains(normalized))
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    private static string FormatInternalDate(DateTimeOffset date) =>
        date.ToUniversalTime().ToString("dd-MMM-yyyy HH:mm:ss", CultureInfo.InvariantCulture) + " +0000";

    // ---- STORE ----

    private async Task StoreAsync(ImapCommand command, IReadOnlyList<ImapToken> args, bool byUid, CancellationToken cancellationToken)
    {
        if (args.Count < 3)
        {
            throw new ImapParseException("STORE expects a sequence set, an item and flags");
        }

        var selected = _selected!;
        if (selected.ReadOnly)
        {
            await Respond(command, null, "NO [READ-ONLY] Folder is read-only", cancellationToken);
            return;
        }

        var item = args[1].AsAtom().ToUpperInvariant();
        var silent = item.EndsWith(".SILENT", StringComparison.Ordinal);
        var operation = item.Replace(".SILENT", "") switch
        {
            "FLAGS" => FlagOperation.Replace,
            "+FLAGS" => FlagOperation.Add,
            "-FLAGS" => FlagOperation.Remove,
            _ => throw new ImapParseException($"Unknown STORE item {item}"),
        };
        var flags = args.Skip(2).SelectMany(a => a.AsList()).Select(f => f.AsAtom()).ToList();
        if (flags.Any(f => f.StartsWith('\\') && !MessageFlags.System.Contains(f, StringComparer.OrdinalIgnoreCase)))
        {
            throw new ImapParseException("Unknown system flag");
        }

        var targets = selected.Resolve(SequenceSet.Parse(args[0].AsAtom()), byUid)
            .Where(t => selected.GetMessage(t.Uid) is not null)
            .ToList();
        var changed = mailboxes.UpdateFlags(selected.Folder.Id, targets.Select(t => t.Uid).ToList(), operation, flags)
            .ToDictionary(m => m.Uid);

        var response = new ImapResponse();
        foreach (var (sequence, uid) in targets)
        {
            var message = changed.GetValueOrDefault(uid) ?? selected.GetMessage(uid)!;
            selected.RememberFlags(message);
            if (!silent)
            {
                response.Raw($"* {sequence} FETCH (FLAGS ({message.Flags}){(byUid ? $" UID {uid}" : "")})\r\n");
            }
        }

        selected.Sync(response, allowExpunge: byUid);
        response.Raw($"{command.Tag} OK {(byUid ? "UID " : "")}STORE completed\r\n");
        await connection.WriteAsync(response.ToMemory(), cancellationToken);
    }

    // ---- SEARCH ----

    private async Task SearchAsync(ImapCommand command, IReadOnlyList<ImapToken> args, bool byUid, CancellationToken cancellationToken)
    {
        var selected = _selected!;
        SearchQuery query;
        try
        {
            query = SearchQuery.Parse(args);
        }
        catch (UnsupportedCharsetException)
        {
            await Respond(command, null, "NO [BADCHARSET (US-ASCII UTF-8)] Unsupported charset", cancellationToken);
            return;
        }

        var prelude = new ImapResponse();
        selected.Sync(prelude, allowExpunge: byUid);

        var matches = new List<long>();
        for (var i = 0; i < selected.Count; i++)
        {
            var uid = selected.Uids[i];
            if (selected.GetMessage(uid) is not { } message)
            {
                continue;
            }

            var context = new SearchContext(i + 1, message, selected.Count, selected.MaxUid, Cache, cancellationToken);
            if (await query.MatchesAsync(context))
            {
                matches.Add(byUid ? uid : i + 1);
            }
        }

        prelude.Raw("* SEARCH").Raw(matches.Count > 0 ? " " + string.Join(' ', matches) : "").Line();
        prelude.Raw($"{command.Tag} OK {(byUid ? "UID " : "")}SEARCH completed\r\n");
        await connection.WriteAsync(prelude.ToMemory(), cancellationToken);
    }

    // ---- COPY / MOVE ----

    private async Task CopyAsync(ImapCommand command, IReadOnlyList<ImapToken> args, bool byUid, bool move, CancellationToken cancellationToken)
    {
        if (args.Count != 2)
        {
            throw new ImapParseException($"{command.Name} expects a sequence set and a folder");
        }

        var selected = _selected!;
        if (move && selected.ReadOnly)
        {
            await Respond(command, null, "NO [READ-ONLY] Folder is read-only", cancellationToken);
            return;
        }

        var target = mailboxes.GetFolder(_account!.Id, DecodeFolderName(args[1]));
        if (target is null)
        {
            await Respond(command, null, "NO [TRYCREATE] Target folder does not exist", cancellationToken);
            return;
        }

        var uids = selected.Resolve(SequenceSet.Parse(args[0].AsAtom()), byUid)
            .Where(t => selected.GetMessage(t.Uid) is not null)
            .Select(t => t.Uid)
            .ToList();
        if (!move && _account.QuotaBytes > 0 &&
            mailboxes.IsOverQuota(_account, uids.Sum(uid => selected.GetMessage(uid)!.Size)))
        {
            await Respond(command, null, "NO [OVERQUOTA] Mailbox quota exceeded", cancellationToken);
            return;
        }

        spamFeedback.Record(_account, selected.Folder, uids.Select(uid => selected.GetMessage(uid)).OfType<StoredMessage>(), target);
        var pairs = move ? mailboxes.Move(selected.Folder.Id, uids, target.Id) : mailboxes.Copy(selected.Folder.Id, uids, target.Id);
        var copyUid = pairs.Count == 0
            ? ""
            : $"[COPYUID {target.UidValidity} {SequenceSet.Format(pairs.Select(p => p.SourceUid))} {SequenceSet.Format(pairs.Select(p => p.TargetUid))}] ";
        var verb = (byUid ? "UID " : "") + (move ? "MOVE" : "COPY");

        var response = new ImapResponse();
        if (move)
        {
            // RFC 6851: COPYUID goes into an untagged OK, followed by the EXPUNGE responses for the moved messages.
            if (copyUid.Length > 0)
            {
                response.Raw($"* OK {copyUid}Moved\r\n");
            }

            selected.Sync(response, allowExpunge: true);
            response.Raw($"{command.Tag} OK {verb} completed\r\n");
        }
        else
        {
            selected.Sync(response, allowExpunge: byUid);
            response.Raw($"{command.Tag} OK {copyUid}{verb} completed\r\n");
        }

        await connection.WriteAsync(response.ToMemory(), cancellationToken);
    }

    // ---- EXPUNGE ----

    private async Task ExpungeAsync(ImapCommand command, IReadOnlyList<ImapToken> args, bool byUid, CancellationToken cancellationToken)
    {
        var selected = _selected!;
        if (selected.ReadOnly)
        {
            await Respond(command, null, "NO [READ-ONLY] Folder is read-only", cancellationToken);
            return;
        }

        IReadOnlyCollection<long>? uids = null;
        if (byUid)
        {
            uids = selected.Resolve(SequenceSet.Parse(RequireArgument(args, 0).AsAtom()), byUid: true).Select(t => t.Uid).ToList();
        }

        mailboxes.Expunge(selected.Folder.Id, uids);
        var response = new ImapResponse();
        selected.Sync(response, allowExpunge: true);
        response.Raw($"{command.Tag} OK {(byUid ? "UID " : "")}EXPUNGE completed\r\n");
        await connection.WriteAsync(response.ToMemory(), cancellationToken);
    }

    // ---- APPEND ----

    private async Task AppendAsync(ImapCommand command, IReadOnlyList<ImapToken> args, CancellationToken cancellationToken)
    {
        if (args.Count < 2 || args[^1] is not StringToken message)
        {
            throw new ImapParseException("APPEND expects a folder and a message literal");
        }

        var flags = new List<string>();
        DateTimeOffset? date = null;
        foreach (var token in args.Skip(1).Take(args.Count - 2))
        {
            switch (token)
            {
                case ListToken list:
                    flags.AddRange(list.Items.Select(i => i.AsAtom()));
                    break;
                case StringToken text:
                    date = ParseInternalDate(text.Text);
                    break;
                default:
                    throw new ImapParseException("Unexpected APPEND argument");
            }
        }

        var folder = mailboxes.GetFolder(_account!.Id, DecodeFolderName(args[0]));
        if (folder is null)
        {
            await Respond(command, null, "NO [TRYCREATE] Folder does not exist", cancellationToken);
            return;
        }

        if (message.Bytes.Length == 0)
        {
            await Respond(command, null, "NO Empty message", cancellationToken);
            return;
        }

        if (mailboxes.IsOverQuota(_account, message.Bytes.Length))
        {
            await Respond(command, null, "NO [OVERQUOTA] Mailbox quota exceeded", cancellationToken);
            return;
        }

        var stored = await mailboxes.AppendAsync(folder, message.Bytes, MessageFlags.Format(flags), date, cancellationToken);

        // The mail program keeps its own copy of a sent message: drop the one the server stored on submission.
        sentCopies.RemoveServerCopy(_account.Id, message.Bytes, MessageFlags.Format(flags));

        var response = new ImapResponse();
        _selected?.Sync(response, allowExpunge: true);
        response.Raw($"{command.Tag} OK [APPENDUID {folder.UidValidity} {stored.Uid}] APPEND completed\r\n");
        await connection.WriteAsync(response.ToMemory(), cancellationToken);
    }

    /// <summary>Parses "17-Jul-1996 02:44:25 -0700" (day may be space-padded).</summary>
    private static DateTimeOffset ParseInternalDate(string value)
    {
        var parts = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 &&
            DateTime.TryParseExact($"{parts[0]} {parts[1]}", "d-MMM-yyyy HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local) &&
            parts[2].Length == 5 && parts[2][0] is '+' or '-' &&
            int.TryParse(parts[2].AsSpan(1, 2), out var hours) && int.TryParse(parts[2].AsSpan(3, 2), out var minutes))
        {
            var offset = new TimeSpan(hours, minutes, 0) * (parts[2][0] == '-' ? -1 : 1);
            return new DateTimeOffset(local, offset);
        }

        throw new ImapParseException("Invalid date-time");
    }
}
