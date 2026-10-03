using Mailserver.Core.Storage;
using Mailserver.Imap.Mime;
using MimeKit;

namespace Mailserver.Imap.Session;

/// <summary>A loaded message: raw bytes, offset tree for FETCH and (lazily) a MimeKit object for SEARCH.</summary>
internal sealed class MessageContent(byte[] data)
{
    private MimeNode? _structure;
    private MimeMessage? _parsed;

    public byte[] Data { get; } = data;

    public MimeNode Structure => _structure ??= MimeNode.Parse(Data);

    public MimeMessage Parsed => _parsed ??= MimeMessage.Load(new MemoryStream(Data, writable: false));
}

/// <summary>Keeps the last few loaded messages; clients often fetch headers, structure and body of the same message in a row.</summary>
internal sealed class MessageCache(MailboxStore mailboxes)
{
    private const int Capacity = 16;
    private readonly LinkedList<(long Id, MessageContent Content)> _entries = new();

    public async Task<MessageContent?> GetAsync(StoredMessage message, CancellationToken cancellationToken)
    {
        for (var node = _entries.First; node is not null; node = node.Next)
        {
            if (node.Value.Id == message.Id)
            {
                _entries.Remove(node);
                _entries.AddFirst(node);
                return node.Value.Content;
            }
        }

        byte[] data;
        try
        {
            data = await File.ReadAllBytesAsync(mailboxes.GetMessagePath(message), cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return null; // expunged by another session in the meantime
        }

        var content = new MessageContent(data);
        _entries.AddFirst((message.Id, content));
        if (_entries.Count > Capacity)
        {
            _entries.RemoveLast();
        }

        return content;
    }
}
