using System.Globalization;
using Mailserver.Core.Dav;
using Microsoft.AspNetCore.Mvc;

namespace Mailserver.Web.Pages.Calendar;

/// <summary>A new event, or an existing one to change or delete (a series as a whole).</summary>
public sealed class EventModel(DavStore store, ILogger<EventModel> logger) : MailPageModel
{
    public sealed class EventForm
    {
        public long CalendarId { get; set; }
        public string? Title { get; set; } = "";
        public bool AllDay { get; set; }
        public string StartDate { get; set; } = "";
        public string StartTime { get; set; } = "10:00";
        public string EndDate { get; set; } = "";
        public string EndTime { get; set; } = "11:00";
        public string? Location { get; set; }
        public string? Description { get; set; }
        // Empty choices ("Nie", "Keine") arrive as null.
        public string? Repeat { get; set; } = "";
        public string? Until { get; set; }
        public string? Reminder { get; set; } = "";
    }

    public static readonly IReadOnlyList<(string Value, string Label)> RepeatChoices =
        [("", "Nie"), ("DAILY", "Täglich"), ("WEEKLY", "Wöchentlich"), ("MONTHLY", "Monatlich"), ("YEARLY", "Jährlich")];

    public static readonly IReadOnlyList<(string Value, string Label)> ReminderChoices =
        [("", "Keine"), ("0", "Zum Beginn"), ("5", "5 Minuten vorher"), ("15", "15 Minuten vorher"), ("30", "30 Minuten vorher"), ("60", "1 Stunde vorher"),
         ("120", "2 Stunden vorher"), ("1440", "1 Tag vorher"), ("2880", "2 Tage vorher"), ("10080", "1 Woche vorher")];

    [BindProperty]
    public EventForm Form { get; set; } = new();

    /// <summary>The event being changed: its calendar and file name; null for a new one.</summary>
    [BindProperty]
    public long? OriginalCalendar { get; set; }

    [BindProperty]
    public string? OriginalHref { get; set; }

    public IReadOnlyList<DavCollection> Calendars { get; private set; } = [];
    public bool IsNew => OriginalHref is null;
    public bool HasExceptions { get; private set; }
    public string? ExtraReminder { get; private set; }

    public IActionResult OnGet(long? cal, string? href, string? date)
    {
        Calendars = store.Collections(CurrentAccount.Id, DavStore.Calendar);
        if (cal is { } calendarId && href is not null)
        {
            if (store.CollectionById(CurrentAccount.Id, calendarId) is not { Kind: DavStore.Calendar } calendar ||
                store.Object(calendar.Id, href) is not { } item || CalendarEditor.Read(item.Data) is not { } fields)
            {
                return NotFound();
            }

            OriginalCalendar = calendar.Id;
            OriginalHref = item.Href;
            HasExceptions = CalendarEditor.HasExceptions(item.Data);
            Form = ToForm(calendar.Id, fields);
            if (fields.ReminderMinutes is { } minutes && ReminderChoices.All(c => c.Value != minutes.ToString(CultureInfo.InvariantCulture)))
            {
                ExtraReminder = minutes.ToString(CultureInfo.InvariantCulture);
            }

            return Page();
        }

        var day = DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : DateOnly.FromDateTime(DateTime.Now);
        Form = new EventForm
        {
            CalendarId = Calendars.FirstOrDefault()?.Id ?? 0,
            StartDate = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            EndDate = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        return Page();
    }

    public IActionResult OnPostSave()
    {
        Calendars = store.Collections(CurrentAccount.Id, DavStore.Calendar);
        if (store.CollectionById(CurrentAccount.Id, Form.CalendarId) is not { Kind: DavStore.Calendar } target)
        {
            ErrorMessage = "Bitte einen Kalender wählen.";
            return Page();
        }

        DavObject? original = null;
        DavCollection? originalCalendar = null;
        if (OriginalHref is not null)
        {
            if (OriginalCalendar is not { } id || store.CollectionById(CurrentAccount.Id, id) is not { Kind: DavStore.Calendar } found ||
                store.Object(found.Id, OriginalHref) is not { } existing)
            {
                ErrorMessage = "Den Termin gibt es nicht mehr (auf einem anderen Gerät gelöscht?).";
                return Page();
            }

            original = existing;
            originalCalendar = found;
        }

        try
        {
            var fields = ToFields(Form);
            string uid, text;
            if (original is null)
            {
                (uid, text) = CalendarEditor.Create(fields);
            }
            else
            {
                text = CalendarEditor.Update(original.Data, fields);
                uid = original.Uid;
            }

            var href = original?.Href ?? uid + ".ics";
            var info = CalendarData.Inspect(text);
            store.Put(target.Id, href, text, info);
            if (originalCalendar is not null && originalCalendar.Id != target.Id)
            {
                // Moved to another calendar.
                store.Delete(originalCalendar.Id, href);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or DavConflictException)
        {
            ErrorMessage = ex.Message;
            return Page();
        }

        var start = DateOnly.ParseExact(Form.StartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        Message = $"Termin „{Form.Title?.Trim()}“ gespeichert.";
        logger.LogInformation("{Account} saved an event in {Calendar}", CurrentAccount.Address, target.Name);
        return Redirect($"/Calendar?y={start.Year}&m={start.Month}");
    }

    public IActionResult OnPostDelete()
    {
        if (OriginalCalendar is { } id && OriginalHref is not null && store.CollectionById(CurrentAccount.Id, id) is { Kind: DavStore.Calendar } calendar &&
            store.Object(calendar.Id, OriginalHref) is { } item)
        {
            var title = CalendarEditor.Read(item.Data)?.Summary;
            store.Delete(calendar.Id, item.Href);
            Message = $"Termin „{title}“ gelöscht.";
        }

        return Redirect("/Calendar");
    }

    private static EventForm ToForm(long calendarId, EventFields fields) => new()
    {
        CalendarId = calendarId,
        Title = fields.Summary,
        AllDay = fields.AllDay,
        StartDate = fields.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        StartTime = fields.AllDay ? "10:00" : fields.Start.ToString("HH:mm", CultureInfo.InvariantCulture),
        EndDate = fields.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        EndTime = fields.AllDay ? "11:00" : fields.End.ToString("HH:mm", CultureInfo.InvariantCulture),
        Location = fields.Location,
        Description = fields.Description,
        Repeat = fields.Repeat,
        Until = fields.RepeatUntil?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Reminder = fields.ReminderMinutes?.ToString(CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>Throws <see cref="ArgumentException"/> with a German message.</summary>
    private static EventFields ToFields(EventForm form)
    {
        DateOnly Date(string value, string what) =>
            DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date
                : throw new ArgumentException($"Bitte {what} angeben.");
        TimeOnly Time(string value) =>
            TimeOnly.TryParseExact(value, ["HH:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                ? time
                : throw new ArgumentException("Bitte eine Uhrzeit angeben.");

        var startDate = Date(form.StartDate, "den Beginn");
        var endDate = string.IsNullOrWhiteSpace(form.EndDate) ? startDate : Date(form.EndDate, "das Ende");
        var start = form.AllDay ? startDate.ToDateTime(TimeOnly.MinValue) : startDate.ToDateTime(Time(form.StartTime));
        var end = form.AllDay ? endDate.ToDateTime(TimeOnly.MinValue) : endDate.ToDateTime(Time(form.EndTime));
        var repeat = form.Repeat ?? "";
        if (!CalendarEditor.Repeats.Contains(repeat))
        {
            throw new ArgumentException("Unbekannte Wiederholung.");
        }

        DateOnly? until = string.IsNullOrWhiteSpace(form.Until) || repeat == "" ? null : Date(form.Until, "das Ende der Wiederholung");
        if (until is { } u && u < startDate)
        {
            throw new ArgumentException("Die Wiederholung endet vor dem ersten Termin.");
        }

        int? reminder = int.TryParse(form.Reminder, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) && minutes <= 60 * 24 * 28 ? minutes : null;
        return new EventFields(form.Title ?? "", form.Location, form.Description, start, end, form.AllDay, repeat, until, reminder);
    }
}
