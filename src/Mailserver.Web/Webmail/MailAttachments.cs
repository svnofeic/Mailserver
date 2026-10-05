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

    /// <summary>Whether the browser may show the attachment itself: PDFs (by type or name; many mailers send octet-stream).</summary>
    public static bool CanView(MailAttachment attachment) =>
        attachment.Entity is MimePart &&
        (attachment.ContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) ||
         attachment.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));

    /// <summary>The content really is a PDF ("%PDF-" within the first KB), whatever the sender declared.</summary>
    public static bool IsPdf(ReadOnlySpan<byte> content) => content[..Math.Min(content.Length, 1024)].IndexOf("%PDF-"u8) >= 0;

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
