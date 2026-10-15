using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;

namespace Mailserver.Core.Dav;

/// <summary>An occurrence of an event, for the calendar page: a single date, or one of a repeating event.</summary>
public sealed record EventOccurrence(DavObject Source, string Uid, string Summary, string? Location, DateTimeOffset Start, DateTimeOffset End,
    bool AllDay, bool Repeats);

/// <summary>Reading iCalendar text (RFC 5545): what is stored with an object, and the occurrences shown on the calendar page.</summary>
public static class CalendarData
{
    public const int MaxBytes = 2 * 1024 * 1024;

    /// <summary>The UID, type and date span of a calendar object. Throws <see cref="FormatException"/> with a German message.</summary>
    public static DavObjectInfo Inspect(string text)
    {
        var calendar = Load(text);
        var components = calendar.Events.Cast<RecurringComponent>().Concat(calendar.Todos).Concat(calendar.Journals).ToList();
        if (components.Count == 0)
        {
            throw new FormatException("Kein Termin und keine Aufgabe enthalten.");
        }

        // The library makes up a UID when there is none; a calendar object without one is refused instead.
        if (!System.Text.RegularExpressions.Regex.IsMatch(text, @"^UID(;[^:\r\n]*)?:[ \t]*\S", System.Text.RegularExpressions.RegexOptions.Multiline |
                System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
        {
            throw new FormatException("Die UID fehlt.");
        }

        var uids = components.Select(c => c.Uid).Distinct().ToList();
        if (uids.Count != 1 || string.IsNullOrWhiteSpace(uids[0]))
        {
            throw new FormatException("Alle Teile müssen dieselbe UID haben.");
        }

        var kind = components.Select(c => c.Name).Distinct().ToList();
        if (kind.Count != 1)
        {
            throw new FormatException("Termine und Aufgaben gehören in getrennte Dateien.");
        }

        DateTimeOffset? start = null, end = null;
        var openEnded = false;
        foreach (var component in components)
        {
            var (first, last, open) = Span(component);
            if (first is { } f && (start is null || f < start))
            {
                start = f;
            }

            if (open)
            {
                openEnded = true;
            }
            else if (last is { } l && (end is null || l > end))
            {
                end = l;
            }
        }

        return new DavObjectInfo(uids[0], kind[0], start, openEnded ? null : end ?? start);
    }

    /// <summary>The occurrences of the events of an object between two points in time, oldest first.</summary>
    public static IReadOnlyList<EventOccurrence> Occurrences(DavObject source, DateTimeOffset from, DateTimeOffset to)
    {
        Ical.Net.Calendar calendar;
        try
        {
            calendar = Load(source.Data);
        }
        catch (FormatException)
        {
            return [];
        }

        var result = new List<EventOccurrence>();
        foreach (var occurrence in calendar.GetOccurrences(new CalDateTime(from.UtcDateTime, "UTC"), new CalDateTime(to.UtcDateTime, "UTC")))
        {
            if (occurrence.Source is not CalendarEvent evt)
            {
                continue;
            }

            var allDay = !evt.DtStart.HasTime;
            var start = ToUtc(occurrence.Period.StartTime);
            var end = occurrence.Period.EndTime is { } e ? ToUtc(e) : start + (occurrence.Period.Duration > TimeSpan.Zero
                ? occurrence.Period.Duration
                : allDay ? TimeSpan.FromDays(1) : TimeSpan.Zero);
            if (end < from || start >= to)
            {
                continue;
            }

            result.Add(new EventOccurrence(source, evt.Uid, string.IsNullOrWhiteSpace(evt.Summary) ? "(ohne Titel)" : evt.Summary,
                string.IsNullOrWhiteSpace(evt.Location) ? null : evt.Location, start, end, allDay,
                evt.RecurrenceRules.Count > 0 || evt.RecurrenceDates.Count > 0 || evt.RecurrenceId is not null));
        }

        return result.OrderBy(o => o.Start).ToList();
    }

    public static Ical.Net.Calendar Load(string text)
    {
        if (text.Length > MaxBytes)
        {
            throw new FormatException("Zu groß.");
        }

        if (!text.TrimStart().StartsWith("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("Keine iCalendar-Daten.");
        }

        try
        {
            return Ical.Net.Calendar.Load(text) ?? throw new FormatException("Keine iCalendar-Daten.");
        }
        catch (Exception ex) when (ex is not FormatException)
        {
            throw new FormatException("Die iCalendar-Daten sind nicht lesbar: " + ex.Message);
        }
    }

    /// <summary>
    /// UTC point of a date or time: with time zone as given, UTC as is, and "floating" times and whole days in the time zone
    /// of the server.
    /// </summary>
    public static DateTimeOffset ToUtc(IDateTime value)
    {
        if (value.IsUtc)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));
        }

        if (!string.IsNullOrEmpty(value.TzId) && value.HasTime)
        {
            try
            {
                return new DateTimeOffset(DateTime.SpecifyKind(value.AsUtc, DateTimeKind.Utc));
            }
            catch (Exception)
            {
                // An unknown time zone: as local time.
            }
        }

        var local = DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, TimeZoneInfo.Local));
    }

    private static (DateTimeOffset? First, DateTimeOffset? Last, bool OpenEnded) Span(RecurringComponent component)
    {
        IDateTime? startValue = component.DtStart;
        IDateTime? endValue = component switch
        {
            CalendarEvent e => e.DtEnd,
            Todo t => t.Due,
            _ => null,
        };
        if (startValue is null && endValue is null)
        {
            return (null, null, true);
        }

        var start = ToUtc(startValue ?? endValue!);
        var duration = component switch
        {
            CalendarEvent e when e.DtEnd is not null => ToUtc(e.DtEnd) - start,
            CalendarEvent e when e.Duration > TimeSpan.Zero => e.Duration,
            _ when startValue is { HasTime: false } => TimeSpan.FromDays(1),
            _ => endValue is not null ? ToUtc(endValue) - start : TimeSpan.Zero,
        };
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        var end = start + duration;
        if (component.RecurrenceRules.Count == 0 && component.RecurrenceDates.Count == 0)
        {
            return (start, end, false);
        }

        // Repeating: up to the last UNTIL; COUNT or no end at all counts as open (time-range queries then simply include it).
        DateTimeOffset last = end;
        foreach (var rule in component.RecurrenceRules)
        {
            if (rule.Until == DateTime.MinValue)
            {
                return (start, null, true);
            }

            var until = new DateTimeOffset(DateTime.SpecifyKind(rule.Until, rule.Until.Kind == DateTimeKind.Utc ? DateTimeKind.Utc : DateTimeKind.Local))
                .ToUniversalTime() + duration;
            if (until > last)
            {
                last = until;
            }
        }

        foreach (var period in component.RecurrenceDates.SelectMany(list => list))
        {
            var at = ToUtc(period.StartTime) + duration;
            if (at > last)
            {
                last = at;
            }
        }

        return (start, last, false);
    }
}
