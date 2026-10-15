using Mailserver.Core.Dav;
using Microsoft.AspNetCore.Mvc;

namespace Mailserver.Web.Pages.Calendar;

/// <summary>The month: all events of all calendars of the mailbox, repeating ones included.</summary>
public sealed class IndexModel(DavStore store) : MailPageModel
{
    public sealed record Item(EventOccurrence Occurrence, DavCollection Calendar)
    {
        public DateTime LocalStart => TimeZoneInfo.ConvertTimeFromUtc(Occurrence.Start.UtcDateTime, TimeZoneInfo.Local);
        public DateTime LocalEnd => TimeZoneInfo.ConvertTimeFromUtc(Occurrence.End.UtcDateTime, TimeZoneInfo.Local);
        public string EditUrl => $"/Calendar/Event?cal={Calendar.Id}&href={Uri.EscapeDataString(Occurrence.Source.Href)}";
    }

    public static readonly string[] DayNames = ["Mo", "Di", "Mi", "Do", "Fr", "Sa", "So"];
    public static readonly string[] MonthNames = ["Januar", "Februar", "März", "April", "Mai", "Juni", "Juli", "August", "September", "Oktober", "November", "Dezember"];

    public DateOnly Month { get; private set; }
    public DateOnly GridStart { get; private set; }
    public int Weeks { get; private set; }
    public DateOnly Today { get; } = DateOnly.FromDateTime(DateTime.Now);
    public IReadOnlyList<DavCollection> Calendars { get; private set; } = [];
    public IReadOnlyDictionary<DateOnly, List<Item>> ByDay { get; private set; } = new Dictionary<DateOnly, List<Item>>();

    public string Title => $"{MonthNames[Month.Month - 1]} {Month.Year}";
    public string MonthUrl(DateOnly month) => $"/Calendar?y={month.Year}&m={month.Month}";

    public IActionResult OnGet(int? y, int? m)
    {
        Month = y is >= 1900 and <= 2200 && m is >= 1 and <= 12 ? new DateOnly(y.Value, m.Value, 1) : new DateOnly(Today.Year, Today.Month, 1);
        // Weeks from Monday; as many as the month needs.
        GridStart = Month.AddDays(-(((int)Month.DayOfWeek + 6) % 7));
        var last = Month.AddMonths(1).AddDays(-1);
        Weeks = (last.DayNumber - GridStart.DayNumber) / 7 + 1;
        var gridEnd = GridStart.AddDays(Weeks * 7);

        var from = Utc(GridStart);
        var to = Utc(gridEnd);
        Calendars = store.Collections(CurrentAccount.Id, DavStore.Calendar);
        var byDay = new Dictionary<DateOnly, List<Item>>();
        foreach (var calendar in Calendars)
        {
            foreach (var source in store.ObjectsInRange(calendar.Id, from, to, "VEVENT"))
            {
                foreach (var occurrence in CalendarData.Occurrences(source, from, to))
                {
                    var item = new Item(occurrence, calendar);
                    // On each day it touches (an event ending at midnight does not reach the next day).
                    var firstDay = DateOnly.FromDateTime(item.LocalStart);
                    var lastDay = DateOnly.FromDateTime(item.LocalEnd > item.LocalStart ? item.LocalEnd.AddTicks(-1) : item.LocalStart);
                    for (var day = firstDay; day <= lastDay && day < gridEnd; day = day.AddDays(1))
                    {
                        if (day < GridStart)
                        {
                            continue;
                        }

                        if (!byDay.TryGetValue(day, out var list))
                        {
                            byDay[day] = list = [];
                        }

                        list.Add(item);
                    }
                }
            }
        }

        foreach (var list in byDay.Values)
        {
            list.Sort((a, b) => a.Occurrence.AllDay != b.Occurrence.AllDay ? (a.Occurrence.AllDay ? -1 : 1) : a.Occurrence.Start.CompareTo(b.Occurrence.Start));
        }

        ByDay = byDay;
        return Page();
    }

    public IReadOnlyList<Item> On(DateOnly day) => ByDay.TryGetValue(day, out var list) ? list : [];

    public static string Time(Item item) => item.Occurrence.AllDay ? "ganztägig" : item.LocalStart.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    public static string Range(Item item)
    {
        if (item.Occurrence.AllDay)
        {
            return "ganztägig";
        }

        var start = item.LocalStart;
        var end = item.LocalEnd;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return start.Date == end.Date || end == start
            ? $"{start.ToString("HH:mm", culture)}–{end.ToString("HH:mm", culture)}"
            : $"{start.ToString("HH:mm", culture)} – {end.ToString("dd.MM. HH:mm", culture)}";
    }

    public static DateTimeOffset Utc(DateOnly day) =>
        new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified), TimeZoneInfo.Local), TimeSpan.Zero);
}
