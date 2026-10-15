using System.Text;
using Mailserver.Core.Dav;
using Microsoft.Extensions.DependencyInjection;

namespace Mailserver.Tests;

/// <summary>The calendar in the web interface: month view, creating, changing, moving, deleting, import and export.</summary>
public sealed class CalendarPageTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private DavStore Store => _server.Services.GetRequiredService<DavStore>();

    [Fact]
    public async Task Events_are_created_changed_moved_and_deleted()
    {
        using var web = new WebClient(_server.WebPort);
        await web.LoginAsync("alice@example.test", TestServer.Password);
        var alice = _server.User("alice");
        await web.GetAsync("/Calendar?y=2026&m=11");
        Assert.Contains("November 2026", web.LastPage);
        var calendar = Store.Collections(alice.Id, DavStore.Calendar).Single();

        await web.PostAsync("/Calendar/Event?date=2026-11-10", "/Calendar/Event?handler=Save",
            ("Form.CalendarId", calendar.Id.ToString()), ("Form.Title", "Elternabend"), ("Form.StartDate", "2026-11-10"), ("Form.StartTime", "19:00"),
            ("Form.EndDate", "2026-11-10"), ("Form.EndTime", "20:30"), ("Form.Location", "Schule"), ("Form.Repeat", "WEEKLY"), ("Form.Until", "2026-11-24"),
            ("Form.Reminder", "30"));
        Assert.Contains("Elternabend", web.LastPage);
        var item = Assert.Single(Store.Objects(calendar.Id));
        Assert.Contains("RRULE:FREQ=WEEKLY", item.Data);
        Assert.Contains("TRIGGER:-PT30M", item.Data);

        // Three occurrences in the month view (10th, 17th, 24th).
        await web.GetAsync("/Calendar?y=2026&m=11");
        Assert.Equal(3, web.LastPage.Split("<span class=\"s\">Elternabend</span>").Length - 1);

        // Errors are shown and nothing is stored.
        await web.PostAsync("/Calendar/Event?date=2026-11-10", "/Calendar/Event?handler=Save",
            ("Form.CalendarId", calendar.Id.ToString()), ("Form.Title", "Falsch"), ("Form.StartDate", "2026-11-10"), ("Form.StartTime", "19:00"),
            ("Form.EndDate", "2026-11-09"), ("Form.EndTime", "20:30"));
        Assert.Contains("Das Ende liegt vor dem Beginn", web.LastPage);
        Assert.Single(Store.Objects(calendar.Id));

        // Change and move to another calendar: same UID, now in "Familie".
        var family = Store.CreateCollection(alice.Id, DavStore.Calendar, "familie", "Familie");
        var edit = $"/Calendar/Event?cal={calendar.Id}&href={Uri.EscapeDataString(item.Href)}";
        await web.GetAsync(edit);
        Assert.Contains("value=\"Elternabend\"", web.LastPage);
        Assert.Contains("ganze Serie", web.LastPage);
        await web.PostAsync(edit, "/Calendar/Event?handler=Save",
            ("OriginalCalendar", calendar.Id.ToString()), ("OriginalHref", item.Href),
            ("Form.CalendarId", family.Id.ToString()), ("Form.Title", "Elternabend 4b"), ("Form.StartDate", "2026-11-10"), ("Form.StartTime", "19:30"),
            ("Form.EndDate", "2026-11-10"), ("Form.EndTime", "21:00"), ("Form.Repeat", ""), ("Form.Reminder", ""));
        Assert.True(Store.Objects(calendar.Id).Count == 0, System.Text.RegularExpressions.Regex.Match(web.LastPage, "class=\"notice[^\"]*\"[^>]*>([^<]*)").Groups[1].Value + web.LastPage.Length);
        var moved = Assert.Single(Store.Objects(family.Id));
        Assert.Equal(item.Uid, moved.Uid);
        Assert.DoesNotContain("RRULE", moved.Data);
        Assert.Contains("Elternabend 4b", moved.Data);

        await web.PostAsync($"/Calendar/Event?cal={family.Id}&href={Uri.EscapeDataString(moved.Href)}", "/Calendar/Event?handler=Delete",
            ("OriginalCalendar", family.Id.ToString()), ("OriginalHref", moved.Href));
        Assert.Empty(Store.Objects(family.Id));
    }

    [Fact]
    public async Task Calendars_are_managed_imported_and_exported()
    {
        using var web = new WebClient(_server.WebPort);
        await web.LoginAsync("alice@example.test", TestServer.Password);
        var alice = _server.User("alice");
        await web.PostAsync("/Calendar/Calendars", "/Calendar/Calendars?handler=Create", ("name", "Arbeit"), ("color", "#10B981FF"));
        var work = Store.Collections(alice.Id, DavStore.Calendar).Single(c => c.Name == "Arbeit");
        Assert.Equal("#10B981FF", work.Color);

        var file = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Google Inc//Google Calendar 70.9054//EN
            BEGIN:VEVENT
            UID:eins@google.com
            DTSTART:20261103T090000Z
            DTEND:20261103T100000Z
            SUMMARY:Import eins
            END:VEVENT
            BEGIN:VEVENT
            UID:zwei@google.com
            DTSTART;VALUE=DATE:20261105
            SUMMARY:Import zwei
            END:VEVENT
            BEGIN:VEVENT
            UID:zwei@google.com
            RECURRENCE-ID;VALUE=DATE:20261105
            DTSTART;VALUE=DATE:20261106
            SUMMARY:Import zwei (verschoben)
            END:VEVENT
            END:VCALENDAR
            """.ReplaceLineEndings("\r\n");
        await web.PostMultipartAsync("/Calendar/Calendars", "/Calendar/Calendars?handler=Import", [("id", work.Id.ToString())],
            ("file", "google.ics", Encoding.UTF8.GetBytes(file)));
        Assert.Contains("2 Termin(e)", web.LastPage);
        // Importing again replaces instead of doubling.
        await web.PostMultipartAsync("/Calendar/Calendars", "/Calendar/Calendars?handler=Import", [("id", work.Id.ToString())],
            ("file", "google.ics", Encoding.UTF8.GetBytes(file)));
        var objects = Store.Objects(work.Id);
        Assert.Equal(2, objects.Count);
        Assert.Contains("verschoben", objects.Single(o => o.Uid == "zwei@google.com").Data);

        var exported = Encoding.UTF8.GetString(await web.GetBytesAsync($"/Calendar/Calendars?handler=Export&id={work.Id}"));
        Assert.Equal(2, CalendarEditor.Split(exported).Count);

        await web.PostAsync("/Calendar/Calendars", $"/Calendar/Calendars?handler=Update&id={work.Id}", ("name", "Büro"), ("color", "#EF4444FF"));
        Assert.Equal("Büro", Store.CollectionById(alice.Id, work.Id)!.Name);
        await web.PostAsync("/Calendar/Calendars", $"/Calendar/Calendars?handler=Delete&id={work.Id}");
        Assert.Null(Store.CollectionById(alice.Id, work.Id));
        Assert.Contains("/dav/", web.LastPage);
    }
}
