using Microsoft.AspNetCore.Html;

namespace Mailserver.Web;

/// <summary>Line icons (24×24, stroked with currentColor) drawn inline as SVG – no external files or fonts.</summary>
public static class Icons
{
    private static readonly Dictionary<string, string> Paths = new(StringComparer.Ordinal)
    {
        ["mail"] = """<rect x="3" y="5" width="18" height="14" rx="2.5"/><path d="m3.5 7.5 8.5 6 8.5-6"/>""",
        ["inbox"] = """<path d="M22 12h-6l-2 3h-4l-2-3H2"/><path d="M5.5 5.1 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.5-6.9A2 2 0 0 0 16.8 4H7.2a2 2 0 0 0-1.7 1.1z"/>""",
        ["grid"] = """<rect x="3" y="3" width="7" height="9" rx="1.5"/><rect x="14" y="3" width="7" height="5" rx="1.5"/><rect x="14" y="12" width="7" height="9" rx="1.5"/><rect x="3" y="16" width="7" height="5" rx="1.5"/>""",
        ["filter"] = """<path d="M3 5h18l-7 8v6l-4 2v-8z"/>""",
        ["shield"] = """<path d="M12 3 4 6v6c0 5 3.5 8 8 9 4.5-1 8-4 8-9V6z"/><path d="m9 12 2 2 4-4"/>""",
        ["shield-alert"] = """<path d="M12 3 4 6v6c0 5 3.5 8 8 9 4.5-1 8-4 8-9V6z"/><path d="M12 8v4M12 16h.01"/>""",
        ["plane"] = """<path d="M17.8 19.2 16 11l3.5-3.5C21 6 21.5 4 21 3c-1-.5-3 0-4.5 1.5L13 8 4.8 6.2c-.5-.1-.9.1-1.1.5l-.3.5c-.2.5-.1 1 .3 1.3L9 12l-2 3H4l-1 1 3 2 2 3 1-1v-3l3-2 3.5 5.3c.3.4.8.5 1.3.3l.5-.2c.4-.3.6-.7.5-1.2z"/>""",
        ["key"] = """<circle cx="7.5" cy="15.5" r="4.5"/><path d="m10.7 12.3 9.3-9.3M17 6l3 3M14 9l2 2"/>""",
        ["server"] = """<rect x="3" y="4" width="18" height="7" rx="2"/><rect x="3" y="13" width="18" height="7" rx="2"/><path d="M7 7.5h.01M7 16.5h.01"/>""",
        ["globe"] = """<circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3a14 14 0 0 1 0 18M12 3a14 14 0 0 0 0 18"/>""",
        ["users"] = """<circle cx="9" cy="8" r="4"/><path d="M2 21a7 7 0 0 1 14 0"/><path d="M16 4a4 4 0 0 1 0 8M22 21a7 7 0 0 0-4-6.3"/>""",
        ["user"] = """<circle cx="12" cy="8" r="4"/><path d="M4 21a8 8 0 0 1 16 0"/>""",
        ["at"] = """<circle cx="12" cy="12" r="4"/><path d="M16 8v5a3 3 0 0 0 6 0v-1a10 10 0 1 0-4 8"/>""",
        ["list"] = """<path d="M10 6h11M10 12h11M10 18h11M3 6l1.5 1.5L7 5M3 12l1.5 1.5L7 11M3 18l1.5 1.5L7 17"/>""",
        ["clock"] = """<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>""",
        ["activity"] = """<path d="M3 12h4l3-8 4 16 3-8h4"/>""",
        ["ban"] = """<circle cx="12" cy="12" r="9"/><path d="m5.6 5.6 12.8 12.8"/>""",
        ["lock"] = """<rect x="4" y="11" width="16" height="10" rx="2"/><path d="M8 11V7a4 4 0 0 1 8 0v4"/>""",
        ["cloud"] = """<path d="M7 18a5 5 0 1 1 .9-9.9A6 6 0 0 1 19 10a4 4 0 0 1-1 8z"/><path d="M12 12v6M9.5 14.5 12 12l2.5 2.5"/>""",
        ["pulse"] = """<path d="M20.8 4.6a5.5 5.5 0 0 0-7.8 0L12 5.7l-1-1.1a5.5 5.5 0 0 0-7.8 7.8L12 21.2l8.8-8.8a5.5 5.5 0 0 0 0-7.8z"/><path d="M3.5 12h4l1.5-3 3 6 1.5-3h7"/>""",
        ["settings"] = """<circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z"/>""",
        ["logout"] = """<path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4M16 17l5-5-5-5M21 12H9"/>""",
        ["sun"] = """<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/>""",
        ["moon"] = """<path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z"/>""",
        ["send"] = """<path d="m22 2-7 20-4-9-9-4z"/><path d="M22 2 11 13"/>""",
        ["plus"] = """<path d="M12 5v14M5 12h14"/>""",
        ["folder"] = """<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>""",
        ["pencil"] = """<path d="M12 20h9M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4z"/>""",
        ["trash"] = """<path d="M3 6h18M8 6V4h8v2M6 6l1 14h10l1-14M10 11v5M14 11v5"/>""",
        ["archive"] = """<rect x="2" y="4" width="20" height="5" rx="1"/><path d="M4 9v10a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V9M10 13h4"/>""",
        ["star"] = """<path d="m12 3 2.8 5.7 6.2.9-4.5 4.4 1 6.2L12 17.3 6.5 20.2l1-6.2L3 9.6l6.2-.9z"/>""",
        ["paperclip"] = """<path d="m21 11-8.5 8.5a5 5 0 0 1-7-7L14 4a3.5 3.5 0 0 1 5 5l-8.5 8.5a2 2 0 0 1-3-3L15 7"/>""",
        ["reply"] = """<path d="M9 14 4 9l5-5"/><path d="M4 9h11a5 5 0 0 1 5 5v6"/>""",
        ["forward"] = """<path d="m15 14 5-5-5-5"/><path d="M20 9H9a5 5 0 0 0-5 5v6"/>""",
        ["search"] = """<circle cx="11" cy="11" r="7"/><path d="m21 21-4.3-4.3"/>""",
        ["trend-up"] = """<path d="m3 17 6-6 4 4 8-8M15 7h6v6"/>""",
        ["trend-down"] = """<path d="m3 7 6 6 4-4 8 8M15 17h6v-6"/>""",
        ["check-circle"] = """<circle cx="12" cy="12" r="9"/><path d="m8 12 3 3 5-6"/>""",
        ["alert"] = """<path d="M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z"/><path d="M12 9v4M12 17h.01"/>""",
        ["x-circle"] = """<circle cx="12" cy="12" r="9"/><path d="m15 9-6 6M9 9l6 6"/>""",
        ["info"] = """<circle cx="12" cy="12" r="9"/><path d="M12 16v-4M12 8h.01"/>""",
        ["drive"] = """<path d="M22 12H2M5.5 5.1 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.5-6.9A2 2 0 0 0 16.8 4H7.2a2 2 0 0 0-1.7 1.1zM6 16h.01M10 16h.01"/>""",
        ["calendar"] = """<rect x="3" y="5" width="18" height="16" rx="2"/><path d="M16 3v4M8 3v4M3 11h18"/>""",
        ["chevron"] = """<path d="m9 6 6 6-6 6"/>""",
        ["eye"] = """<path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12z"/><circle cx="12" cy="12" r="3"/>""",
        ["refresh"] = """<path d="M21 12a9 9 0 1 1-2.6-6.4L21 8M21 3v5h-5"/>""",
    };

    public static IHtmlContent Get(string name, string? extraClass = null) =>
        new HtmlString($"""<svg class="icon{(extraClass is null ? "" : " " + extraClass)}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">{Paths[name]}</svg>""");

    /// <summary>The icon of a mail folder (system folders by name, everything else a folder).</summary>
    public static IHtmlContent Folder(string name) => Get(name switch
    {
        "INBOX" => "inbox",
        "Sent" => "send",
        "Drafts" => "pencil",
        "Trash" => "trash",
        "Junk" => "shield-alert",
        "Archive" => "archive",
        _ => "folder",
    });
}
