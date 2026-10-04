using System.IO.Compression;
using System.Text;
using MimeKit;

namespace Mailserver.Core.Antivirus;

public enum FindingKind
{
    /// <summary>Executable or script: never accepted from outside.</summary>
    Blocked,
    /// <summary>Office macros or a password-protected archive: often malware, sometimes legitimate.</summary>
    Suspicious,
}

/// <param name="Code">Short test name for logs and headers, e.g. ATTACH_EXECUTABLE.</param>
public sealed record AttachmentFinding(FindingKind Kind, string FileName, string Code, string Reason);

public sealed record Attachment(string FileName, byte[] Content);

/// <summary>
/// Looks at every attachment: file name (also double extensions like "rechnung.pdf.exe"), the actual content
/// (Windows programs disguised as PDF), the files inside ZIP archives, Office macros and encrypted archives.
/// </summary>
public static class AttachmentInspector
{
    private static readonly HashSet<string> MacroExtensions =
        ["docm", "dotm", "xlsm", "xltm", "xlam", "xlsb", "pptm", "potm", "ppsm", "ppam", "sldm"];

    private static readonly HashSet<string> OfficeOpenXml =
        ["docx", "docm", "dotx", "dotm", "xlsx", "xlsm", "xltx", "xltm", "xlam", "xlsb", "pptx", "pptm", "potx", "potm", "ppsx", "ppsm", "ppam"];

    private static readonly HashSet<string> LegacyOffice = ["doc", "dot", "xls", "xlt", "ppt", "pot", "pps"];

    // OLE2 storage names are UTF-16; a VBA project means macros.
    private static readonly byte[] VbaMarker = Encoding.Unicode.GetBytes("_VBA_PROJECT");

    private const int MaxDepth = 3;

    /// <summary>All attachments (decoded, including those of attached messages) for the virus scanner.</summary>
    public static IReadOnlyList<Attachment> Extract(MimeMessage message)
    {
        var result = new List<Attachment>();
        Collect(message, result, 0);
        return result;
    }

    public static IReadOnlyList<AttachmentFinding> Inspect(IEnumerable<Attachment> attachments, IReadOnlySet<string> blockedExtensions)
    {
        var findings = new List<AttachmentFinding>();
        foreach (var attachment in attachments)
        {
            InspectFile(attachment.FileName, attachment.Content, blockedExtensions, findings, 0);
        }

        return findings;
    }

    private static void Collect(MimeMessage message, List<Attachment> result, int depth)
    {
        foreach (var part in message.BodyParts)
        {
            switch (part)
            {
                case MessagePart { Message: { } inner } when depth < MaxDepth:
                    Collect(inner, result, depth + 1);
                    break;
                case MimePart mime when mime.Content is not null && IsAttachmentLike(mime):
                    using (var buffer = new MemoryStream())
                    {
                        mime.Content.DecodeTo(buffer);
                        result.Add(new Attachment(mime.FileName ?? $"unbenannt.{mime.ContentType.MediaSubtype}", buffer.ToArray()));
                    }

                    break;
            }
        }
    }

    /// <summary>Everything except the plain text and HTML body.</summary>
    private static bool IsAttachmentLike(MimePart part) =>
        part.FileName is not null || part.IsAttachment ||
        !(part.ContentType.IsMimeType("text", "plain") || part.ContentType.IsMimeType("text", "html"));

    private static void InspectFile(string fileName, byte[] content, IReadOnlySet<string> blocked, List<AttachmentFinding> findings, int depth)
    {
        var name = Path.GetFileName(fileName.Replace('\\', '/')).Trim().TrimEnd('.', ' ');
        var extension = Extension(name);

        if (blocked.Contains(extension))
        {
            findings.Add(new AttachmentFinding(FindingKind.Blocked, name, "ATTACH_BLOCKED_TYPE", $"Dateityp .{extension} ist nicht erlaubt"));
            return;
        }

        if (IsWindowsExecutable(content))
        {
            findings.Add(new AttachmentFinding(FindingKind.Blocked, name, "ATTACH_EXECUTABLE", "enthält ein Windows-Programm"));
            return;
        }

        if (extension == "zip" || (content.Length > 4 && content[0] == 'P' && content[1] == 'K' && !OfficeOpenXml.Contains(extension)))
        {
            InspectZip(name, content, blocked, findings, depth);
            return;
        }

        if (MacroExtensions.Contains(extension) || (OfficeOpenXml.Contains(extension) && ZipContains(content, "vbaProject.bin")))
        {
            findings.Add(new AttachmentFinding(FindingKind.Suspicious, name, "ATTACH_MACRO", "Office-Datei mit Makros"));
        }
        else if (LegacyOffice.Contains(extension) && content.AsSpan().IndexOf(VbaMarker) >= 0)
        {
            findings.Add(new AttachmentFinding(FindingKind.Suspicious, name, "ATTACH_MACRO", "Office-Datei mit Makros"));
        }
    }

    private static void InspectZip(string name, byte[] content, IReadOnlySet<string> blocked, List<AttachmentFinding> findings, int depth)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(content, writable: false), ZipArchiveMode.Read);
            foreach (var entry in zip.Entries.Where(e => e.Name.Length > 0).Take(1000))
            {
                var inner = $"{name} → {entry.FullName}";
                if (blocked.Contains(Extension(entry.Name)))
                {
                    findings.Add(new AttachmentFinding(FindingKind.Blocked, inner, "ATTACH_BLOCKED_TYPE", $"Archiv enthält .{Extension(entry.Name)}-Datei"));
                    continue;
                }

                if (IsEncrypted(entry))
                {
                    findings.Add(new AttachmentFinding(FindingKind.Suspicious, name, "ATTACH_ENCRYPTED_ARCHIVE", "passwortgeschütztes Archiv (Inhalt nicht prüfbar)"));
                    return;
                }

                if (depth < MaxDepth && entry.Length is > 0 and < 20 * 1024 * 1024)
                {
                    using var stream = entry.Open();
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    InspectFile(inner, buffer.ToArray(), blocked, findings, depth + 1);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            // Not a readable ZIP (or an unsupported compression method); the virus scanner still sees it.
        }
    }

    /// <summary>Bit 0 of the general purpose flag in the local file header marks encryption.</summary>
    private static bool IsEncrypted(ZipArchiveEntry entry) => entry.IsEncrypted;

    private static bool ZipContains(byte[] content, string entryName)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(content, writable: false), ZipArchiveMode.Read);
            return zip.Entries.Any(e => e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>"MZ" header with a PE signature where the header points to.</summary>
    public static bool IsWindowsExecutable(ReadOnlySpan<byte> content)
    {
        if (content.Length < 64 || content[0] != 'M' || content[1] != 'Z')
        {
            return false;
        }

        var offset = BitConverter.ToInt32(content.Slice(0x3C, 4));
        return offset > 0 && offset + 4 <= content.Length && content[offset] == 'P' && content[offset + 1] == 'E' &&
               content[offset + 2] == 0 && content[offset + 3] == 0;
    }

    private static string Extension(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot < 0 ? "" : fileName[(dot + 1)..].Trim().ToLowerInvariant();
    }
}
