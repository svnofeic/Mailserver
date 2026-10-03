using Mailserver.Core.Storage;
using Mailserver.Imap.Protocol;

namespace Mailserver.Imap.Session;

/// <summary>
/// The session's view of the selected folder: the sequence-number-to-UID mapping and the flags the client has been told.
/// Changes made by other sessions are reported when <see cref="Sync"/> runs.
/// </summary>
internal sealed class SelectedFolder
{
    private readonly MailboxStore _mailboxes;
    private readonly List<long> _uids = [];
    private readonly Dictionary<long, string> _knownFlags = [];
    private Dictionary<long, StoredMessage> _messages = [];
    private long _changeCounter = -1;
    private long _highestUid;
    private bool _pendingExpunges;

    public SelectedFolder(MailboxStore mailboxes, Folder folder, bool readOnly)
    {
        _mailboxes = mailboxes;
        Folder = folder;
        ReadOnly = readOnly;
        Sync(response: null, allowExpunge: true);
    }

    public Folder Folder { get; private set; }

    public bool ReadOnly { get; }

    public int Count => _uids.Count;

    public IReadOnlyList<long> Uids => _uids;

    public long MaxUid => _uids.Count == 0 ? 0 : _uids[^1];

    public StoredMessage? GetMessage(long uid) => _messages.GetValueOrDefault(uid);

    public int SequenceOf(long uid) => _uids.BinarySearch(uid) + 1;

    public void RememberFlags(StoredMessage message)
    {
        _knownFlags[message.Uid] = message.Flags;
        if (_messages.ContainsKey(message.Uid))
        {
            _messages[message.Uid] = message;
        }
    }

    /// <summary>Messages addressed by a sequence set or UID set, in ascending order, as (sequence number, UID).</summary>
    public List<(int Sequence, long Uid)> Resolve(SequenceSet set, bool byUid)
    {
        var result = new List<(int, long)>();
        for (var i = 0; i < _uids.Count; i++)
        {
            if (byUid ? set.Contains(_uids[i], MaxUid) : set.Contains(i + 1, _uids.Count))
            {
                result.Add((i + 1, _uids[i]));
            }
        }

        return result;
    }

    /// <summary>
    /// Reports changes since the last sync as untagged responses. EXPUNGE responses are only allowed when no command
    /// that uses sequence numbers is in progress (RFC 3501 7.4.1); otherwise expunged messages stay visible until later.
    /// </summary>
    public void Sync(ImapResponse? response, bool allowExpunge)
    {
        var folder = _mailboxes.GetFolder(Folder.Id);
        if (folder is not null && folder.ChangeCounter == _changeCounter && !(allowExpunge && _pendingExpunges))
        {
            return;
        }

        var current = folder is null ? [] : _mailboxes.ListMessages(Folder.Id).ToDictionary(m => m.Uid);
        if (folder is not null)
        {
            Folder = folder;
        }

        _pendingExpunges = false;
        for (var i = 0; i < _uids.Count;)
        {
            if (current.ContainsKey(_uids[i]))
            {
                i++;
                continue;
            }

            if (!allowExpunge)
            {
                _pendingExpunges = true;
                i++;
                continue;
            }

            response?.Raw($"* {i + 1} EXPUNGE\r\n");
            _knownFlags.Remove(_uids[i]);
            _uids.RemoveAt(i);
        }

        var added = current.Values.Where(m => m.Uid > _highestUid).OrderBy(m => m.Uid).ToList();
        foreach (var message in added)
        {
            _uids.Add(message.Uid);
            _knownFlags[message.Uid] = message.Flags;
            _highestUid = message.Uid;
        }

        if (added.Count > 0)
        {
            response?.Raw($"* {_uids.Count} EXISTS\r\n");
        }

        for (var i = 0; i < _uids.Count; i++)
        {
            if (current.TryGetValue(_uids[i], out var message) && _knownFlags.TryGetValue(message.Uid, out var known) && known != message.Flags)
            {
                response?.Raw($"* {i + 1} FETCH (UID {message.Uid} FLAGS ({message.Flags}))\r\n");
                _knownFlags[message.Uid] = message.Flags;
            }
        }

        // Messages that are expunged but still visible have no row here; FETCH and STORE skip them.
        _messages = current;
        _changeCounter = folder?.ChangeCounter ?? -1;
    }
}
