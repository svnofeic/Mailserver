using Mailserver.Core.Storage;

namespace Mailserver.Web.Webmail;

/// <summary>German names for system folders and hierarchy helpers for display.</summary>
public static class FolderDisplay
{
    public static int Depth(string name) => name.Count(c => c == MailboxStore.HierarchyDelimiter);

    public static string Leaf(string name)
    {
        var index = name.LastIndexOf(MailboxStore.HierarchyDelimiter);
        return index < 0 ? Format.FolderName(name) : name[(index + 1)..];
    }

    /// <summary>"INBOX/Projekte/2026" → "Posteingang › Projekte › 2026".</summary>
    public static string Path(string name) =>
        string.Join(" › ", name.Split(MailboxStore.HierarchyDelimiter).Select((segment, i) => i == 0 ? Format.FolderName(segment) : segment));
}
