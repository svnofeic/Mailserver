using System.Text;
using System.Text.RegularExpressions;
using Mailserver.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Mailserver.Tests;

/// <summary>Conversation view: replies grouped with the mails they answer, in the list and when reading.</summary>
public sealed partial class ConversationTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private Task AppendAsync(string folder, string headers, DateTimeOffset? date = null) =>
        _server.HostMailboxes.AppendAsync(_server.User("alice"), Encoding.UTF8.GetBytes(headers.ReplaceLineEndings("\r\n") + "\r\n\r\nText"),
            folder, internalDate: date);

    private static List<string> Rows(string page) => RowValue().Matches(page).Select(m => m.Groups[1].Value).ToList();

    [Fact]
    public async Task Replies_are_grouped_with_the_mail_they_answer()
    {
        var start = DateTimeOffset.UtcNow.AddHours(-3);
        await AppendAsync("INBOX", "From: Bob <bob@example.test>\nTo: alice@example.test\nSubject: Angebot\nMessage-ID: <a1@remote.test>", start);
        await AppendAsync("Sent", "From: alice@example.test\nTo: bob@example.test\nSubject: AW: Angebot\nMessage-ID: <a2@example.test>\nIn-Reply-To: <a1@remote.test>\nReferences: <a1@remote.test>",
            start.AddHours(1));
        await AppendAsync("INBOX", "From: Bob <bob@example.test>\nTo: alice@example.test\nSubject: Re: AW: Angebot\nMessage-ID: <a3@remote.test>\nIn-Reply-To: <a2@example.test>\nReferences: <a1@remote.test> <a2@example.test>",
            start.AddHours(2));
        await AppendAsync("INBOX", "From: Carol <carol@example.test>\nTo: alice@example.test\nSubject: Etwas anderes\nMessage-ID: <c1@remote.test>", start.AddHours(1.5));

        using var web = new WebClient(_server.WebPort);
        await web.LoginAsync("alice@example.test", TestServer.Password);
        await web.GetAsync("/Mail?folder=INBOX");
        var rows = Rows(web.LastPage);
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows[0].Split(',').Length);
        Assert.Contains("2 Konversation(en)", web.LastPage);
        Assert.Contains("title=\"3 Nachrichten in dieser Konversation\"", web.LastPage);

        // Reading the newest shows the whole conversation, the reply from "Sent" included, and reads it as a whole.
        var newest = _server.Inbox("alice").Single(m => m.InternalDate > start.AddHours(1.9));
        await web.GetAsync($"/Mail/Read?folder=INBOX&uid={newest.Uid}");
        Assert.Contains("Konversation · 3 Nachrichten", web.LastPage);
        Assert.Contains("<span class=\"pill small\">Gesendet</span>", web.LastPage);
        Assert.All(_server.Inbox("alice").Where(m => m.InternalDate < start.AddHours(1.2)), m => Assert.Contains(MessageFlags.Seen, m.Flags));
        Assert.False(_server.Inbox("alice").Single(m => m.InternalDate == start.AddHours(1.5)).HasFlag(MessageFlags.Seen));

        // An action on the conversation's line applies to all its messages in the folder.
        await web.PostAsync("/Mail?folder=INBOX", "/Mail?handler=Bulk", ("folder", "INBOX"), ("page", "0"), ("uids", rows[0]), ("op", "flag"));
        Assert.Equal(2, _server.Inbox("alice").Count(m => m.HasFlag(MessageFlags.Flagged)));

        // Switched off: each mail for itself, also after logging in again.
        await web.PostAsync("/Mail?folder=INBOX", "/Mail?handler=Conversations", ("folder", "INBOX"), ("on", "false"));
        await web.GetAsync("/Mail?folder=INBOX");
        Assert.Equal(3, Rows(web.LastPage).Count);
        await web.GetAsync($"/Mail/Read?folder=INBOX&uid={newest.Uid}");
        Assert.DoesNotContain("Konversation ·", web.LastPage);
    }

    [Fact]
    public async Task Conversations_join_when_the_reply_comes_first_and_by_subject_without_references()
    {
        var start = DateTimeOffset.UtcNow.AddDays(-1);
        // The reply is there before the mail it answers (e.g. imported in a different order).
        await AppendAsync("INBOX", "From: bob@example.test\nSubject: Re: Planung\nMessage-ID: <p2@remote.test>\nReferences: <p1@remote.test>", start.AddHours(1));
        await AppendAsync("INBOX", "From: carol@example.test\nSubject: Planung\nMessage-ID: <p0@remote.test>", start.AddMinutes(30));
        await _server.Services.GetRequiredService<Conversations>().IndexPendingAsync(100);
        await AppendAsync("INBOX", "From: bob@example.test\nSubject: Planung\nMessage-ID: <p1@remote.test>", start);
        // A mail program without References: matched by subject.
        await AppendAsync("INBOX", "From: dave@example.test\nSubject: AW: Termin\nMessage-ID: <t2@remote.test>", start.AddHours(3));
        await AppendAsync("INBOX", "From: alice@example.test\nSubject: Termin\nMessage-ID: <t1@remote.test>", start.AddHours(2));

        using var web = new WebClient(_server.WebPort);
        await web.LoginAsync("alice@example.test", TestServer.Password);
        await web.GetAsync("/Mail?folder=INBOX");
        var sizes = Rows(web.LastPage).Select(r => r.Split(',').Length).Order().ToList();
        // "Planung" without "Re:" and without references stays separate; the two others are conversations of two.
        Assert.Equal([1, 2, 2], sizes);

        // Drafts are never grouped.
        await AppendAsync("Drafts", "From: alice@example.test\nSubject: AW: Termin\nIn-Reply-To: <t2@remote.test>");
        await AppendAsync("Drafts", "From: alice@example.test\nSubject: AW: Termin\nIn-Reply-To: <t2@remote.test>");
        await web.GetAsync("/Mail?folder=Drafts");
        Assert.Equal(2, Rows(web.LastPage).Count);
    }

    [Fact]
    public void Subjects_are_compared_without_reply_prefixes()
    {
        Assert.Equal("angebot für 2027", Conversations.SubjectKey("AW: Re:  [Kunden] WG: Angebot  für 2027"));
        Assert.Equal("angebot", Conversations.SubjectKey("Re[2]: Angebot"));
        Assert.True(Conversations.IsReply("AW: Angebot"));
        Assert.True(Conversations.IsReply("[Liste] Re: Angebot"));
        Assert.False(Conversations.IsReply("WG: Angebot"));
        Assert.False(Conversations.IsReply("Rechnung"));
    }

    [GeneratedRegex("name=\"uids\" value=\"([0-9,]+)\"")]
    private static partial Regex RowValue();
}
