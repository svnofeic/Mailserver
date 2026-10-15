using Mailserver.Core.Dav;

namespace Mailserver.Tests;

/// <summary>Reading iCalendar data: UID, type and date span, and occurrences of repeating events.</summary>
public sealed class CalendarDataTests
{
    private static string Event(string body, string uid = "abc-1") => $"""
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Test//DE
        BEGIN:VTIMEZONE
        TZID:Europe/Berlin
        BEGIN:STANDARD
        DTSTART:19701025T030000
        RRULE:FREQ=YEARLY;BYMONTH=10;BYDAY=-1SU
        TZOFFSETFROM:+0200
        TZOFFSETTO:+0100
        END:STANDARD
        BEGIN:DAYLIGHT
        DTSTART:19700329T020000
        RRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=-1SU
        TZOFFSETFROM:+0100
        TZOFFSETTO:+0200
        END:DAYLIGHT
        END:VTIMEZONE
        BEGIN:VEVENT
        UID:{uid}
        DTSTAMP:20261001T080000Z
        {body}
        END:VEVENT
        END:VCALENDAR
        """.ReplaceLineEndings("\r\n");

    [Fact]
    public void Reads_uid_and_span_with_time_zone()
    {
        var info = CalendarData.Inspect(Event("DTSTART;TZID=Europe/Berlin:20261020T100000\nDTEND;TZID=Europe/Berlin:20261020T113000\nSUMMARY:Besprechung".ReplaceLineEndings("\r\n")));
        Assert.Equal("abc-1", info.Uid);
        Assert.Equal("VEVENT", info.Component);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero), info.Start);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 9, 30, 0, TimeSpan.Zero), info.End);
    }

    [Fact]
    public void Repeating_events_are_open_ended_or_end_with_until()
    {
        Assert.Null(CalendarData.Inspect(Event("DTSTART:20261020T080000Z\nDURATION:PT1H\nRRULE:FREQ=WEEKLY".ReplaceLineEndings("\r\n"))).End);
        var until = CalendarData.Inspect(Event("DTSTART:20261020T080000Z\nDURATION:PT1H\nRRULE:FREQ=DAILY;UNTIL=20261025T080000Z".ReplaceLineEndings("\r\n")));
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero), until.End);

        var source = new DavObject(1, 1, "a.ics", "abc-1", "\"x\"", Event("DTSTART:20261020T080000Z\nDURATION:PT1H\nRRULE:FREQ=WEEKLY;COUNT=10\nSUMMARY:Sport".ReplaceLineEndings("\r\n")),
            "VEVENT", null, null, 1, DateTimeOffset.UtcNow);
        var occurrences = CalendarData.Occurrences(source, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal([3, 10, 17, 24], occurrences.Select(o => o.Start.Day));
        Assert.All(occurrences, o => Assert.True(o.Repeats));
        Assert.All(occurrences, o => Assert.Equal(TimeSpan.FromHours(1), o.End - o.Start));
        Assert.Equal("Sport", occurrences[0].Summary);
    }

    [Fact]
    public void All_day_events_last_the_day()
    {
        var info = CalendarData.Inspect(Event("DTSTART;VALUE=DATE:20261224\nDTEND;VALUE=DATE:20261227\nSUMMARY:Weihnachten".ReplaceLineEndings("\r\n")));
        Assert.Equal(TimeSpan.FromDays(3), info.End - info.Start);
        var source = new DavObject(1, 1, "a.ics", "abc-1", "\"x\"", Event("DTSTART;VALUE=DATE:20261224\nSUMMARY:Heiligabend".ReplaceLineEndings("\r\n")),
            "VEVENT", null, null, 1, DateTimeOffset.UtcNow);
        var occurrence = Assert.Single(CalendarData.Occurrences(source, new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.True(occurrence.AllDay);
    }

    [Fact]
    public void Rejects_broken_data()
    {
        Assert.Throws<FormatException>(() => CalendarData.Inspect("hallo"));
        Assert.Throws<FormatException>(() => CalendarData.Inspect("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nEND:VCALENDAR\r\n"));
        Assert.Throws<FormatException>(() => CalendarData.Inspect(Event("DTSTART:20261020T080000Z", uid: "")));
    }

    [Fact]
    public void Editor_creates_and_changes_events_without_losing_other_data()
    {
        var (uid, text) = CalendarEditor.Create(new EventFields("Elternabend", "Schule", "Raum 12", new DateTime(2026, 11, 3, 19, 0, 0),
            new DateTime(2026, 11, 3, 21, 0, 0), false, "WEEKLY", new DateOnly(2026, 12, 15), 30));
        var info = CalendarData.Inspect(text);
        Assert.Equal(uid, info.Uid);
        var fields = CalendarEditor.Read(text)!;
        Assert.Equal("Elternabend", fields.Summary);
        Assert.Equal(new DateTime(2026, 11, 3, 19, 0, 0), fields.Start);
        Assert.Equal(new DateTime(2026, 11, 3, 21, 0, 0), fields.End);
        Assert.Equal("WEEKLY", fields.Repeat);
        Assert.Equal(new DateOnly(2026, 12, 15), fields.RepeatUntil);
        Assert.Equal(30, fields.ReminderMinutes);

        // From the iPhone, with an attendee: changing the title keeps the attendee.
        var invited = Event("DTSTART:20261020T080000Z\nDTEND:20261020T090000Z\nSUMMARY:Planung\nATTENDEE;CN=Bob:mailto:bob@example.test".ReplaceLineEndings("\r\n"));
        var original = CalendarEditor.Read(invited)!;
        var changed = CalendarEditor.Update(invited, original with { Summary = "Planung Q4" });
        Assert.Contains("mailto:bob@example.test", changed);
        Assert.Contains("Planung Q4", changed);
        Assert.Equal("abc-1", CalendarData.Inspect(changed).Uid);

        // All-day: the form shows the last day, the data ends the day after.
        var (_, holiday) = CalendarEditor.Create(new EventFields("Urlaub", null, null, new DateTime(2026, 12, 24), new DateTime(2026, 12, 27), true));
        var read = CalendarEditor.Read(holiday)!;
        Assert.True(read.AllDay);
        Assert.Equal(new DateTime(2026, 12, 27), read.End);
        Assert.Equal(TimeSpan.FromDays(4), CalendarData.Inspect(holiday).End - CalendarData.Inspect(holiday).Start);

        Assert.Throws<ArgumentException>(() => CalendarEditor.Create(new EventFields(" ", null, null, DateTime.Now, DateTime.Now, false)));
        Assert.Throws<ArgumentException>(() => CalendarEditor.Create(new EventFields("x", null, null, DateTime.Now, DateTime.Now.AddHours(-1), false)));
    }
}
