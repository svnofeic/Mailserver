using MimeKit;

namespace Mailserver.Web.Webmail;

public sealed record MailAttachment(string Name, string ContentType, long Size, MimeEntity Entity);

/// <summary>Attachments of a message in a stable order (the index is used in download links).</summary>
public static class MailAttachments
{
    public static IReadOnlyList<MailAttachment> List(MimeMessage message)
    {
        var result = new List<MailAttachment>();
        foreach (var entity in message.Attachments)
        {
            switch (entity)
            {
                case MimePart part:
                    result.Add(new MailAttachment(SafeName(part.FileName, "anhang"), part.ContentType.MimeType, EstimateSize(part), part));
                    break;
                case MessagePart attached:
                    var subject = attached.Message?.Subject;
                    result.Add(new MailAttachment(SafeName(string.IsNullOrWhiteSpace(subject) ? null : subject + ".eml", "nachricht.eml"),
                        "message/rfc822", 0, attached));
                    break;
            }
        }

        return result;
    }

    public static byte[] Content(MailAttachment attachment)
    {
        using var buffer = new MemoryStream();
        switch (attachment.Entity)
        {
            case MimePart { Content: not null } part:
                part.Content.DecodeTo(buffer);
                break;
            case MessagePart { Message: not null } attached:
                attached.Message.WriteTo(buffer);
                break;
        }

        return buffer.ToArray();
    }

    // Formats the browser may show itself, by declared type and by file name (many mailers send application/octet-stream).
    // No SVG: it can contain scripts.
    private static readonly Dictionary<string, string> Viewable = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png",
        [".gif"] = "image/gif", [".webp"] = "image/webp",
    };

    /// <summary>Whether the browser may show the attachment itself (PDF or picture) instead of only downloading it.</summary>
    public static bool CanView(MailAttachment attachment) =>
        attachment.Entity is MimePart &&
        (Viewable.ContainsValue(attachment.ContentType.ToLowerInvariant()) || Viewable.ContainsKey(Path.GetExtension(attachment.Name)));

    /// <summary>
    /// The type the content really has, recognised by its first bytes – whatever the sender declared – or null if it is none of
    /// the viewable formats.
    /// </summary>
    public static string? ViewType(ReadOnlySpan<byte> content) => content switch
    {
        _ when content[..Math.Min(content.Length, 1024)].IndexOf("%PDF-"u8) >= 0 => "application/pdf",
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, ..] => "image/png",
        [(byte)'G', (byte)'I', (byte)'F', (byte)'8', ..] => "image/gif",
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..] => "image/webp",
        _ => null,
    };

    /// <summary>File names come from the sender: strip paths and characters that are invalid on Windows.</summary>
    public static string SafeName(string? name, string fallback)
    {
        var fileName = Path.GetFileName((name ?? "").Replace('\\', '/'));
        fileName = new string(fileName.Where(c => !char.IsControl(c) && c is not ('<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*')).ToArray()).Trim(' ', '.');
        return fileName.Length == 0 ? fallback : fileName.Length > 150 ? fileName[..150] : fileName;
    }

    private static long EstimateSize(MimePart part)
    {
        if (part.Content?.Stream is not { CanSeek: true } stream)
        {
            return 0;
        }

        // Base64 encodes 3 bytes in 4 characters.
        return part.ContentTransferEncoding == ContentEncoding.Base64 ? stream.Length * 3 / 4 : stream.Length;
    }
}
