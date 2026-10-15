using System.Text;
using Mailserver.Core.Storage;
using Mailserver.Web.Webmail;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace Mailserver.Tests;

/// <summary>"Später senden" and "Wiedervorlage" in the webmail.</summary>
public sealed class LaterTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private FollowUps FollowUps => _server.Services.GetRequiredService<FollowUps>();
    private ScheduledMailService Service => _server.Services.GetRequiredService<ScheduledMailService>();

    private static string Tomorrow => DateTime.Now.AddDays(1).ToString("yyyy-MM-dd'T'09:30");

    private IReadOnlyList<StoredMessage> Messages(string localPart, string folder) =>
        _server.HostMailboxes.GetFolder(_server.User(localPart).Id, folder) is { } f ? _server.HostMailboxes.ListMessages(f.Id) : [];

    private async Task ScheduleAsync(WebClient web, string subject)
    {
        await web.PostMultipartAsync("/Mail/Compose", "/Mail/Compose?handler=Schedule",
            [("Form.From", "alice@example.test"), ("Form.To", "bob@example.test"), ("Form.Bcc", "someone@remote.test"),
             ("Form.Subject", subject), ("Form.Body", "Bis morgen"), ("sendChoice", "custom"), ("sendAt", Tomorrow)]);
    }

    [Fact]
    public async Task Scheduled_mail_is_sent_when_due()
    {
        using var web = new WebClient(_server.WebPort);
        await web.LoginAsync("alice@example.test", TestServer.Password);
        await ScheduleAsync(web, "Geplant");

        Assert.Contains("wird am", web.LastPage);
        var waiting = Assert.Single(Messages("alice", FollowUps.ScheduledFolder));
        var scheduled = Assert.Single(FollowUps.Scheduled(_server.User("alice").Id));
        Assert.Equal(waiting.FileName, scheduled.FileName);
        await web.GetAsync("/Mail/Later");
        Assert.Contains("Geplant", web.LastPage);
        Assert.Contains("an bob@example.test", web.LastPage);

        // Not yet due: nothing happens.
        await Service.RunOnceAsync(CancellationToken.None);
        Assert.Empty(_server.Inbox("bob"));

        FollowUps.SetSendTime(scheduled.Id, DateTimeOffset.UtcNow.AddMinutes(-1));
        await Service.RunOnceAsync(CancellationToken.None);

        await TestServer.WaitUntilAsync(() => _server.Inbox("bob").Count == 1, "delivery");
        var received = MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(await _server.ReadAsync(_server.Inbox("bob")[0]))));
        Assert.Equal("Geplant", received.Subject);
        Assert.Empty(received.Bcc);
        Assert.Null(received.Headers["X-Mailserver-Send-At"]);
        Assert.True(received.Date > DateTimeOffset.Now.AddMinutes(-2));
        Assert.Empty(Messages("alice", FollowUps.ScheduledFolder));
        Assert.Empty(FollowUps.Scheduled(_server.User("alice").Id));
        Assert.Single(Messages("alice", "Sent"));
    }

    [Fact]
    public async Task Deleted_or_cancelled_scheduled_mail_is_not_sent()
    {
        using var web = new WebClient(_server.WebPort);
        await web.LoginAsync("alice@example.test", TestServer.Password);
        var alice = _server.User("alice");

        // Thrown away in a mail program: deleted from "Geplant".
        await ScheduleAsync(web, "Gelöscht");
        var folder = _server.HostMailboxes.GetFolder(alice.Id, FollowUps.ScheduledFolder)!;
        var trash = _server.HostMailboxes.GetFolder(alice.Id, "Trash")!;
        _server.HostMailboxes.Move(folder.Id, [Messages("alice", FollowUps.ScheduledFolder).Single().Uid], trash.Id);

        // "Nicht senden" on the page: into the drafts.
        await ScheduleAsync(web, "Abgesagt");
        var cancelled = FollowUps.Scheduled(alice.Id).Single(s => s.FileName == Messages("alice", FollowUps.ScheduledFolder).Single().FileName);
        await web.PostAsync("/Mail/Later", $"/Mail/Later?handler=Cancel&id={cancelled.Id}");
        Assert.Contains("in den Entwürfen", web.LastPage);
        var draft = Assert.Single(Messages("alice", "Drafts"));
        Assert.Contains(MessageFlags.Draft, draft.Flags);

        foreach (var s in FollowUps.Scheduled(alice.Id))
        {
            FollowUps.SetSendTime(s.Id, DateTimeOffset.UtcNow.AddMinutes(-1));
        }

        await Service.RunOnceAsync(CancellationToken.None);
        await Task.Delay(500);
        Assert.Empty(_server.Inbox("bob"));
        Assert.Empty(FollowUps.Scheduled(alice.Id));
    }

    [Fact]
    public async Task Follow_up_brings_the_message_back_unread()
    {
        await _server.HostMailboxes.AppendAsync(_server.User("alice"),
            Encoding.UTF8.GetBytes("From: bob@example.test\r\nTo: alice@example.test\r\nSubject: Angebot\r\n\r\nBitte bis Freitag."));
        var message = Assert.Single(_server.Inbox("alice"));

        using var web = new WebClient(_server.WebPort);
        await web.LoginAsync("alice@example.test", TestServer.Password);
        await web.GetAsync($"/Mail/Read?folder=INBOX&uid={message.Uid}");
        Assert.Contains("Wiedervorlage setzen", web.LastPage);
        await web.PostAsync($"/Mail/Read?folder=INBOX&uid={message.Uid}", $"/Mail/Read?handler=FollowUp&folder=INBOX&uid={message.Uid}",
            ("choice", "custom"), ("at", Tomorrow));

        Assert.Contains("Wiedervorlage am", web.LastPage);
        Assert.Empty(_server.Inbox("alice"));
        Assert.Single(Messages("alice", FollowUps.FollowUpFolder));
        var reminder = Assert.Single(FollowUps.FollowUpsOf(_server.User("alice").Id));
        Assert.Equal(MailboxStore.Inbox, reminder.ReturnFolder);
        await web.GetAsync("/Mail/Later");
        Assert.Contains("Angebot", web.LastPage);

        await web.PostAsync("/Mail/Later", $"/Mail/Later?handler=BringBack&id={reminder.Id}");
        var back = Assert.Single(_server.Inbox("alice"));
        Assert.DoesNotContain(MessageFlags.Seen, back.Flags);
        Assert.Empty(Messages("alice", FollowUps.FollowUpFolder));
        Assert.Empty(FollowUps.FollowUpsOf(_server.User("alice").Id));
    }

    [Fact]
    public void Resolves_time_choices()
    {
        var monday = new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 12, 10, 0, 0)));
        DateTime Local(string key, DateTimeOffset now) => LaterChoices.Resolve(key, null, now)!.Value.DateTime;

        Assert.Equal(new DateTime(2026, 10, 12, 18, 0, 0), Local("evening", monday));
        Assert.Equal(new DateTime(2026, 10, 13, 8, 0, 0), Local("tomorrow8", monday));
        Assert.Equal(new DateTime(2026, 10, 13, 13, 0, 0), Local("tomorrow13", monday));
        Assert.Equal(new DateTime(2026, 10, 15, 8, 0, 0), Local("3days", monday));
        Assert.Equal(new DateTime(2026, 10, 19, 8, 0, 0), Local("monday8", monday));
        Assert.Equal(new DateTime(2026, 10, 19, 8, 0, 0), Local("week", monday));
        Assert.Equal(new DateTime(2026, 10, 19, 8, 0, 0), Local("monday8", monday.AddDays(3)));
        Assert.Equal(new DateTime(2026, 10, 20, 7, 45, 0), LaterChoices.Resolve("custom", "2026-10-20T07:45", monday)!.Value.DateTime);
        Assert.Null(LaterChoices.Resolve("custom", "morgen", monday));
        Assert.Null(LaterChoices.Resolve("irgendwann", null, monday));
        // A filled-in date wins over the list.
        Assert.Equal(new DateTime(2026, 10, 20, 7, 45, 0), LaterChoices.Resolve("tomorrow8", "2026-10-20T07:45", monday)!.Value.DateTime);
    }
}
