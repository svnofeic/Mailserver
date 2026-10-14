using System.Text;
using Mailserver.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace Mailserver.Tests;

/// <summary>Webmail search over all folders, in the text and attachment names, with filters.</summary>
public sealed class SearchTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private SearchIndex Index => _server.Services.GetRequiredService<SearchIndex>();

    private async Task AddAsync(string subject, string body, string folder = MailboxStore.Inbox, string? attachment = null, string flags = "",
        string from = "shop@remote.test", DateTimeOffset? date = null)
    {
        var builder = new BodyBuilder { TextBody = body };
        if (attachment is not null)
        {
            builder.Attachments.Add(attachment, "PDF"u8.ToArray(), new ContentType("application", "pdf"));
        }

        var message = new MimeMessage { Subject = subject, Body = builder.ToMessageBody(), Date = date ?? DateTimeOffset.Now };
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse("alice@example.test"));
        var alice = _server.User("alice");
        _server.HostMailboxes.GetOrCreateFolder(alice.Id, folder);
        using var raw = new MemoryStream();
        await message.WriteToAsync(raw);
        await _server.HostMailboxes.AppendAsync(alice, raw.ToArray(), folder, flags, date);
    }

    [Fact]
    public async Task Finds_words_and_word_parts_in_all_folders()
    {
        await AddAsync("Ihre Abrechnung", "Die Stromrechnung für März liegt bei.", attachment: "Rechnung-2026-03.pdf");
        await AddAsync("Projekt", "Treffen am Montag", folder: "Archiv/Kunden", from: "kunde@remote.test");
        await AddAsync("Newsletter", "Angebote der Woche", folder: "Junk", flags: @"\Seen");
        await AddAsync("Alt", "Rechnung von früher", date: DateTimeOffset.Now.AddYears(-2), flags: @"\Seen \Flagged");
        // Someone else's mail is never found.
        await _server.HostMailboxes.AppendAsync(_server.User("bob"), Encoding.UTF8.GetBytes("Subject: Rechnung Bob\r\n\r\nRechnung"));

        var alice = _server.User("alice").Id;
        Assert.True(Index.Pending(alice) > 0);
        await Index.IndexPendingAsync(1000);
        Assert.Equal(0, Index.Pending());

        IReadOnlyList<string> Subjects(MailSearch search) =>
            Index.Search(alice, search, 0, 50).Hits.Select(h => MimeMessage.Load(_server.HostMailboxes.GetMessagePath(h.Message)).Subject).ToList();

        Assert.Equal(["Ihre Abrechnung", "Alt"], Subjects(new MailSearch("rechnung")));
        Assert.Equal(["Ihre Abrechnung"], Subjects(new MailSearch("strom MÄRZ")));
        Assert.Equal(["Ihre Abrechnung"], Subjects(new MailSearch("2026-03.pdf")));
        Assert.Equal(["Projekt"], Subjects(new MailSearch("kunde@remote")));
        Assert.Equal(["Projekt"], Subjects(new MailSearch("montag")));
        Assert.Empty(Subjects(new MailSearch("montag", InBody: false)));
        Assert.Equal(["Ihre Abrechnung"], Subjects(new MailSearch("abrechnung", InBody: false)));
        Assert.Equal(["Ihre Abrechnung"], Subjects(new MailSearch("rechnung", WithAttachment: true)));
        Assert.Equal(["Alt"], Subjects(new MailSearch("rechnung", Flagged: true)));
        Assert.Equal(["Ihre Abrechnung"], Subjects(new MailSearch("rechnung", Since: DateTimeOffset.Now.AddDays(-1))));
        Assert.Equal(["Projekt", "Ihre Abrechnung"], Subjects(new MailSearch(null, Unread: true)));
        var junk = _server.HostMailboxes.GetFolder(alice, "Junk")!.Id;
        Assert.Equal(["Newsletter"], Subjects(new MailSearch("woche", FolderId: junk)));
        // FTS syntax in the input is taken literally.
        Assert.Empty(Subjects(new MailSearch("rechnung OR \"montag")));

        // Deleted mails leave the index.
        var inbox = _server.HostMailboxes.GetFolder(alice, MailboxStore.Inbox)!;
        var uids = _server.HostMailboxes.ListMessages(inbox.Id).Select(m => m.Uid).ToList();
        _server.HostMailboxes.UpdateFlags(inbox.Id, uids, FlagOperation.Add, [MessageFlags.Deleted]);
        _server.HostMailboxes.Expunge(inbox.Id, uids);
        Assert.Empty(Subjects(new MailSearch("rechnung")));
        Assert.Equal(2, Index.RemoveStale());
    }

    [Fact]
    public async Task Search_page_shows_hits_from_every_folder()
    {
        await AddAsync("Ihre Abrechnung", "Die Stromrechnung liegt bei.");
        await AddAsync("Projekt", "Stromrechnung Kunde", folder: "Archiv");

        using var web = new WebClient(_server.WebPort);
        await web.LoginAsync("alice@example.test", TestServer.Password);
        await web.GetAsync("/Mail/Search?q=stromrechnung");
        Assert.Contains("2 Nachricht(en) gefunden", web.LastPage);
        Assert.Contains("/Mail/Read?folder=Archiv&uid=", web.LastPage);

        await web.GetAsync("/Mail/Search?q=stromrechnung&folder=INBOX");
        Assert.Contains("1 Nachricht(en) gefunden", web.LastPage);

        await web.GetAsync("/Mail/Search?q=ab");
        Assert.Contains("Nicht berücksichtigt", web.LastPage);
        Assert.DoesNotContain("gefunden", web.LastPage);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await web.GetAsync("/Mail/Search?q=x&folder=Gibtsnicht")).StatusCode);
    }
}
