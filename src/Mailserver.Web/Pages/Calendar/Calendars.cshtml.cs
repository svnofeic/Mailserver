using System.Text;
using Mailserver.Core;
using Mailserver.Core.Dav;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Mailserver.Web.Pages.Calendar;

/// <summary>The calendars of the mailbox: add, rename, colour, delete, import and export; and how to set up phones.</summary>
public sealed class CalendarsModel(DavStore store, IOptions<MailserverOptions> options, Dav.DavActivity activity) : MailPageModel
{
    public static readonly IReadOnlyList<(string Value, string Label)> Colors =
    [
        ("#7C5CFFFF", "Violett"), ("#3B82F6FF", "Blau"), ("#10B981FF", "Grün"), ("#F59E0BFF", "Orange"), ("#EF4444FF", "Rot"),
        ("#EC4899FF", "Pink"), ("#14B8A6FF", "Türkis"), ("#64748BFF", "Grau"),
    ];

    private const long MaxImportBytes = 20 * 1024 * 1024;

    public IReadOnlyList<(DavCollection Calendar, int Events)> Items { get; private set; } = [];
    public string ServerUrl => Dav.DavHandler.ServerUrl(options.Value);
    public IReadOnlyList<Dav.DavClient> Clients { get; private set; } = [];

    public void OnGet()
    {
        Items = store.Collections(CurrentAccount.Id, DavStore.Calendar).Select(c => (c, store.Objects(c.Id).Count)).ToList();
        Clients = activity.Clients(CurrentAccount.Id);
    }

    public static string StatusText(int status) => status switch
    {
        200 or 207 => "OK",
        201 or 204 => "gespeichert",
        304 => "unverändert",
        401 => "Anmeldung fehlgeschlagen",
        403 => "nicht erlaubt",
        404 => "nicht gefunden",
        409 => "Konflikt (gleiche UID)",
        412 => "auf dem Server inzwischen geändert",
        413 => "zu groß",
        415 => "Daten nicht lesbar",
        429 => "zu viele Fehlversuche, gesperrt",
        _ => status.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    public IActionResult OnPostCreate(string name, string? color)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            ErrorMessage = "Bitte einen Namen eingeben.";
            return RedirectToPage();
        }

        try
        {
            store.CreateCollection(CurrentAccount.Id, DavStore.Calendar, Guid.NewGuid().ToString("N")[..12], name.Trim()[..Math.Min(name.Trim().Length, 100)],
                Colors.Any(c => c.Value == color) ? color : Colors[0].Value);
            Message = $"Kalender „{name.Trim()}“ angelegt.";
        }
        catch (DavConflictException ex)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage();
    }

    public IActionResult OnPostUpdate(long id, string? name, string? color)
    {
        if (store.CollectionById(CurrentAccount.Id, id) is { Kind: DavStore.Calendar } calendar)
        {
            store.UpdateCollection(calendar.Id, name?.Trim() is { Length: > 0 and <= 100 } n ? n : null, Colors.Any(c => c.Value == color) ? color : null);
            Message = "Gespeichert.";
        }

        return RedirectToPage();
    }

    public IActionResult OnPostDelete(long id)
    {
        if (store.CollectionById(CurrentAccount.Id, id) is { Kind: DavStore.Calendar } calendar)
        {
            store.DeleteCollection(calendar.Id);
            Message = $"Kalender „{calendar.Name}“ mit allen Terminen gelöscht.";
        }

        return RedirectToPage();
    }

    public IActionResult OnGetExport(long id)
    {
        if (store.CollectionById(CurrentAccount.Id, id) is not { Kind: DavStore.Calendar } calendar)
        {
            return NotFound();
        }

        var text = Dav.DavHandler.MergeCalendars(store.Objects(calendar.Id).Select(o => o.Data), calendar.Name);
        var file = string.Concat(calendar.Name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        return File(Encoding.UTF8.GetBytes(text), "text/calendar", $"{file}.ics");
    }

    public async Task<IActionResult> OnPostImportAsync(long id, IFormFile? file, CancellationToken cancellationToken)
    {
        if (store.CollectionById(CurrentAccount.Id, id) is not { Kind: DavStore.Calendar } calendar || file is null || file.Length == 0)
        {
            ErrorMessage = "Bitte eine .ics-Datei und einen Kalender wählen.";
            return RedirectToPage();
        }

        if (file.Length > MaxImportBytes)
        {
            ErrorMessage = "Die Datei ist zu groß (höchstens 20 MB).";
            return RedirectToPage();
        }

        string text;
        using (var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8))
        {
            text = await reader.ReadToEndAsync(cancellationToken);
        }

        int imported = 0, skipped = 0;
        try
        {
            foreach (var (uid, single) in CalendarEditor.Split(text))
            {
                try
                {
                    var info = CalendarData.Inspect(single);
                    // Imported again: the event is replaced, not doubled.
                    var href = store.ObjectByUid(calendar.Id, uid)?.Href ?? SafeName(uid) + ".ics";
                    store.Put(calendar.Id, href, single, info);
                    imported++;
                }
                catch (Exception ex) when (ex is FormatException or ArgumentException or DavConflictException)
                {
                    skipped++;
                }
            }
        }
        catch (FormatException ex)
        {
            ErrorMessage = ex.Message;
            return RedirectToPage();
        }

        Message = $"{imported} Termin(e) in „{calendar.Name}“ übernommen" + (skipped > 0 ? $", {skipped} nicht lesbar." : ".");
        return RedirectToPage();
    }

    private static string SafeName(string uid)
    {
        var name = string.Concat(uid.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_')).Trim('.');
        return name.Length is > 0 and <= 120 ? name : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(uid)))[..32];
    }
}
