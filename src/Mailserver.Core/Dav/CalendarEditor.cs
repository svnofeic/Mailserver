using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;

namespace Mailserver.Core.Dav;

/// <summary>What the calendar page edits of an event; times are local (the server's time zone).</summary>
/// <param name="Repeat">"" (once), DAILY, WEEKLY, MONTHLY or YEARLY.</param>
/// <param name="RepeatUntil">Last day of a repeating event; null repeats without end.</param>
public sealed record EventFields(string Summary, string? Location, string? Description, DateTime Start, DateTime End, bool AllDay,
    string Repeat = "", DateOnly? RepeatUntil = null, int? ReminderMinutes = null);

/// <summary>
/// Creates and changes events for the calendar page. Changing keeps what the page does not show (attendees, other reminders,
/// exceptions of a series), as the event may come from the iPhone or Outlook.
/// </summary>
public static class CalendarEditor
{
    public static readonly IReadOnlyList<string> Repeats = ["", "DAILY", "WEEKLY", "MONTHLY", "YEARLY"];

    /// <summary>A new event as iCalendar text, with a new UID.</summary>
    public static (string Uid, string Text) Create(EventFields fields)
    {
        var uid = Guid.NewGuid().ToString("D").ToUpperInvariant();
        var calendar = new Ical.Net.Calendar { ProductId = "-//Mailserver//Kalender//DE" };
        var evt = new CalendarEvent { Uid = uid, DtStamp = new CalDateTime(DateTime.UtcNow, "UTC"), Created = new CalDateTime(DateTime.UtcNow, "UTC") };
        calendar.Events.Add(evt);
        Apply(calendar, evt, fields);
        return (uid, Serialize(calendar));
    }

    /// <summary>The event with the new fields; the rest of the data stays as it was.</summary>
    public static string Update(string text, EventFields fields)
    {
        var calendar = CalendarData.Load(text);
        var evt = Master(calendar) ?? throw new FormatException("Kein Termin enthalten.");
        Apply(calendar, evt, fields);
        evt.DtStamp = new CalDateTime(DateTime.UtcNow, "UTC");
        evt.LastModified = new CalDateTime(DateTime.UtcNow, "UTC");
        evt.Sequence++;
        return Serialize(calendar);
    }

    /// <summary>The fields of the (first) event, for the edit form; null if the object holds no event.</summary>
    public static EventFields? Read(string text)
    {
        Ical.Net.Calendar calendar;
        try
        {
            calendar = CalendarData.Load(text);
        }
        catch (FormatException)
        {
            return null;
        }

        if (Master(calendar) is not { } evt)
        {
            return null;
        }

        var allDay = !evt.DtStart.HasTime;
        var start = Local(evt.DtStart);
        var end = evt.DtEnd is not null ? Local(evt.DtEnd) : start + (evt.Duration > TimeSpan.Zero ? evt.Duration : allDay ? TimeSpan.FromDays(1) : TimeSpan.FromHours(1));
        if (allDay)
        {
            // Whole days end at the start of the next day; the form shows the last day.
            end = end.AddDays(-1);
        }

        var rule = evt.RecurrenceRules.FirstOrDefault();
        var repeat = rule?.Frequency switch
        {
            Ical.Net.FrequencyType.Daily => "DAILY",
            Ical.Net.FrequencyType.Weekly => "WEEKLY",
            Ical.Net.FrequencyType.Monthly => "MONTHLY",
            Ical.Net.FrequencyType.Yearly => "YEARLY",
            _ => "",
        };
        DateOnly? until = rule is not null && rule.Until != DateTime.MinValue ? DateOnly.FromDateTime(rule.Until.Kind == DateTimeKind.Utc ? rule.Until.ToLocalTime() : rule.Until) : null;
        var reminder = evt.Alarms.Select(a => a.Trigger?.Duration).FirstOrDefault(d => d is not null && d.Value <= TimeSpan.Zero);
        return new EventFields(evt.Summary ?? "", evt.Location, evt.Description, start, end, allDay, repeat, until,
            reminder is { } r ? (int)Math.Round(-r.TotalMinutes) : null);
    }

    /// <summary>Whether the series has more than its rule (single changed or cancelled dates), which the page cannot show.</summary>
    public static bool HasExceptions(string text)
    {
        try
        {
            var calendar = CalendarData.Load(text);
            return calendar.Events.Count > 1 || calendar.Events.Any(e => e.ExceptionDates.Count > 0 || e.RecurrenceRules.Count > 1);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// A calendar file (export from Google, Outlook, another server) as one object per event or task, as CalDAV keeps
    /// them; each with the time zones of the file. Throws <see cref="FormatException"/>.
    /// </summary>
    public static IReadOnlyList<(string Uid, string Text)> Split(string text, int max = 20_000)
    {
        if (!text.TrimStart().StartsWith("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("Keine iCalendar-Datei (.ics).");
        }

        Ical.Net.CalendarCollection calendars;
        try
        {
            calendars = Ical.Net.CalendarCollection.Load(text);
        }
        catch (Exception ex)
        {
            throw new FormatException("Die Datei ist nicht lesbar: " + ex.Message);
        }

        var result = new List<(string, string)>();
        foreach (var calendar in calendars)
        {
            var groups = calendar.Events.Cast<RecurringComponent>().Concat(calendar.Todos)
                .Where(c => !string.IsNullOrWhiteSpace(c.Uid))
                .GroupBy(c => (c.Uid, c.Name));
            foreach (var group in groups)
            {
                if (result.Count >= max)
                {
                    throw new FormatException($"Mehr als {max} Termine in einer Datei.");
                }

                var single = new Ical.Net.Calendar { ProductId = "-//Mailserver//Kalender//DE" };
                foreach (var zone in calendar.TimeZones)
                {
                    single.TimeZones.Add(zone);
                }

                foreach (var component in group)
                {
                    single.Children.Add(component);
                }

                result.Add((group.Key.Uid, Serialize(single)));
            }
        }

        return result;
    }

    private static CalendarEvent? Master(Ical.Net.Calendar calendar) =>
        calendar.Events.FirstOrDefault(e => e.RecurrenceId is null) ?? calendar.Events.FirstOrDefault();

    private static void Apply(Ical.Net.Calendar calendar, CalendarEvent evt, EventFields fields)
    {
        if (string.IsNullOrWhiteSpace(fields.Summary))
        {
            throw new ArgumentException("Bitte einen Titel eingeben.");
        }

        if (fields.End < fields.Start)
        {
            throw new ArgumentException("Das Ende liegt vor dem Beginn.");
        }

        evt.Summary = fields.Summary.Trim();
        evt.Location = string.IsNullOrWhiteSpace(fields.Location) ? null : fields.Location.Trim();
        evt.Description = string.IsNullOrWhiteSpace(fields.Description) ? null : fields.Description.Trim();
        evt.Duration = default;
        if (fields.AllDay)
        {
            evt.DtStart = new CalDateTime(fields.Start.Date) { HasTime = false };
            evt.DtEnd = new CalDateTime(fields.End.Date.AddDays(1)) { HasTime = false };
        }
        else
        {
            var zone = ZoneId();
            evt.DtStart = Point(fields.Start, zone);
            evt.DtEnd = Point(fields.End, zone);
            if (zone is not null && calendar.TimeZones.All(z => z.TzId != zone))
            {
                calendar.AddTimeZone(VTimeZone.FromDateTimeZone(zone));
            }
        }

        // The rule the page offers; a more detailed one from another program is kept if the choice did not change.
        var current = evt.RecurrenceRules.FirstOrDefault();
        var currentRepeat = current?.Frequency.ToString().ToUpperInvariant() ?? "";
        var currentUntil = current is not null && current.Until != DateTime.MinValue ? DateOnly.FromDateTime(current.Until) : (DateOnly?)null;
        if (fields.Repeat != currentRepeat || fields.RepeatUntil != currentUntil || evt.RecurrenceRules.Count > 1)
        {
            evt.RecurrenceRules.Clear();
            if (fields.Repeat is { Length: > 0 } repeat && Enum.TryParse<Ical.Net.FrequencyType>(repeat, true, out var frequency))
            {
                var rule = new RecurrencePattern(frequency, 1);
                if (fields.RepeatUntil is { } until)
                {
                    // Through the end of the last day, in UTC as the standard asks when the start has a time zone.
                    rule.Until = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(until.ToDateTime(new TimeOnly(23, 59, 59)), DateTimeKind.Unspecified), TimeZoneInfo.Local);
                }

                evt.RecurrenceRules.Add(rule);
            }
        }

        // One reminder as the page shows it; others (several reminders, from the iPhone) stay when the choice did not change.
        var displayed = evt.Alarms.Select(a => a.Trigger?.Duration).FirstOrDefault(d => d is not null && d.Value <= TimeSpan.Zero);
        var displayedMinutes = displayed is { } d0 ? (int?)Math.Round(-d0.TotalMinutes) : null;
        if (displayedMinutes != fields.ReminderMinutes)
        {
            evt.Alarms.Clear();
            if (fields.ReminderMinutes is { } minutes and >= 0)
            {
                evt.Alarms.Add(new Alarm
                {
                    Action = Ical.Net.AlarmAction.Display,
                    Description = evt.Summary,
                    Trigger = new Trigger(TimeSpan.FromMinutes(-minutes)),
                });
            }
        }
    }

    /// <summary>The IANA name of the server's time zone ("Europe/Berlin"), or null for UTC.</summary>
    public static string? ZoneId()
    {
        var local = TimeZoneInfo.Local;
        if (local.Id is "UTC" or "Etc/UTC" or "Coordinated Universal Time" || local.BaseUtcOffset == TimeSpan.Zero && !local.SupportsDaylightSavingTime)
        {
            return null;
        }

        if (local.HasIanaId)
        {
            return local.Id;
        }

        return TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, out var iana) ? iana : null;
    }

    private static CalDateTime Point(DateTime local, string? zone) => zone is null
        ? new CalDateTime(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.Local), "UTC")
        : new CalDateTime(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone);

    private static DateTime Local(Ical.Net.DataTypes.IDateTime value) =>
        value.HasTime ? TimeZoneInfo.ConvertTimeFromUtc(CalendarData.ToUtc(value).UtcDateTime, TimeZoneInfo.Local) : value.Value.Date;

    private static string Serialize(Ical.Net.Calendar calendar) => new CalendarSerializer().SerializeToString(calendar);
}
