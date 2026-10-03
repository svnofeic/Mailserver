using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using SmtpClient = MailKit.Net.Smtp.SmtpClient;

namespace Mailserver.Tests;

/// <summary>Drives the IMAP server with MailKit, a strict real-world client.</summary>
public sealed class ImapIntegrationTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task Advertises_extensions_after_login()
    {
        using var client = await ConnectAsync("alice");

        Assert.True(client.Capabilities.HasFlag(ImapCapabilities.Idle));
        Assert.True(client.Capabilities.HasFlag(ImapCapabilities.Move));
        Assert.True(client.Capabilities.HasFlag(ImapCapabilities.UidPlus));
        Assert.True(client.Capabilities.HasFlag(ImapCapabilities.SpecialUse));
        Assert.True(client.Capabilities.HasFlag(ImapCapabilities.Namespace));
    }

    [Fact]
    public async Task Plain_port_requires_starttls_before_login()
    {
        using (var plain = new ImapClient())
        {
            await plain.ConnectAsync("127.0.0.1", _server.ImapPort, SecureSocketOptions.None);
            Assert.True(plain.Capabilities.HasFlag(ImapCapabilities.LoginDisabled));
            Assert.True(plain.Capabilities.HasFlag(ImapCapabilities.StartTLS));
            await Assert.ThrowsAnyAsync<Exception>(() => plain.AuthenticateAsync("alice@example.test", TestServer.Password));
        }

        using var tls = new ImapClient { ServerCertificateValidationCallback = (_, _, _, _) => true };
        await tls.ConnectAsync("127.0.0.1", _server.ImapPort, SecureSocketOptions.StartTls);
        await tls.AuthenticateAsync("alice@example.test", TestServer.Password);
        Assert.True(tls.IsAuthenticated);
    }

    [Fact]
    public async Task Rejects_wrong_password()
    {
        using var client = new ImapClient { ServerCertificateValidationCallback = (_, _, _, _) => true };
        await client.ConnectAsync("127.0.0.1", _server.ImapsPort, SecureSocketOptions.SslOnConnect);

        await Assert.ThrowsAsync<AuthenticationException>(() => client.AuthenticateAsync("alice@example.test", "wrong"));
    }

    [Fact]
    public async Task Lists_default_folders_with_special_use()
    {
        using var client = await ConnectAsync("alice");

        Assert.Equal("Sent", client.GetFolder(SpecialFolder.Sent).FullName);
        Assert.Equal("Drafts", client.GetFolder(SpecialFolder.Drafts).FullName);
        Assert.Equal("Trash", client.GetFolder(SpecialFolder.Trash).FullName);
        Assert.Equal("Junk", client.GetFolder(SpecialFolder.Junk).FullName);
        var names = (await client.GetFolder(client.PersonalNamespaces[0]).GetSubfoldersAsync()).Select(f => f.FullName).ToList();
        Assert.Contains("INBOX", names);
    }

    [Fact]
    public async Task Fetches_structure_envelope_and_parts_of_delivered_mail()
    {
        var original = CreateMultipartMessage();
        await DeliverAsync(original, "alice@example.test");

        using var client = await ConnectAsync("alice");
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite);
        Assert.Equal(1, inbox.Count);

        var summary = Assert.Single(await inbox.FetchAsync(0, -1,
            MessageSummaryItems.Envelope | MessageSummaryItems.BodyStructure | MessageSummaryItems.Flags |
            MessageSummaryItems.UniqueId | MessageSummaryItems.Size | MessageSummaryItems.InternalDate));
        Assert.Equal("Rechnung März – Übersicht", summary.Envelope.Subject);
        Assert.Equal("Müller GmbH", summary.Envelope.From.Mailboxes.Single().Name);
        Assert.Equal("sender@remote.test", summary.Envelope.From.Mailboxes.Single().Address);
        Assert.Equal(original.MessageId, summary.Envelope.MessageId);
        Assert.False(summary.Flags!.Value.HasFlag(MessageFlags.Seen));
        Assert.True(summary.Size > 0);

        var attachment = Assert.Single(summary.Attachments);
        Assert.Equal("rechnung.pdf", attachment.FileName);
        Assert.Equal("text/plain", summary.TextBody.ContentType.MimeType);
        Assert.Equal("text/html", summary.HtmlBody.ContentType.MimeType);

        var text = (TextPart)await inbox.GetBodyPartAsync(summary.UniqueId, summary.TextBody);
        Assert.Equal("Hallo, anbei die Rechnung. Grüße", text.Text.Trim());
        var pdf = (MimePart)await inbox.GetBodyPartAsync(summary.UniqueId, attachment);
        using var decoded = new MemoryStream();
        await pdf.Content.DecodeToAsync(decoded);
        Assert.Equal(PdfBytes, decoded.ToArray());

        var message = await inbox.GetMessageAsync(summary.UniqueId);
        Assert.Equal(original.Subject, message.Subject);
        Assert.Equal(original.Attachments.Count(), message.Attachments.Count());
    }

    [Fact]
    public async Task Peek_and_partial_fetches_do_not_mark_seen()
    {
        await DeliverAsync(CreateMultipartMessage(), "alice@example.test");
        using var client = await ConnectAsync("alice");
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite);
        var uid = (await inbox.SearchAsync(SearchQuery.All)).Single();

        var headers = await inbox.GetHeadersAsync(uid);
        Assert.Equal("sender@remote.test", MailboxAddress.Parse(headers[HeaderId.From]).Address);

        await using var partial = await inbox.GetStreamAsync(uid, 0, 9);
        Assert.Equal("Received:", await new StreamReader(partial).ReadToEndAsync());

        var flags = (await inbox.FetchAsync([uid], MessageSummaryItems.Flags)).Single().Flags!.Value;
        Assert.False(flags.HasFlag(MessageFlags.Seen));
    }

    [Fact]
    public async Task Stores_flags_and_searches()
    {
        await DeliverAsync(CreateSimpleMessage("Erste Nachricht", "Inhalt eins mit Grüßen"), "alice@example.test");
        await DeliverAsync(CreateSimpleMessage("Zweite Nachricht", "Inhalt zwei"), "alice@example.test");
        using var client = await ConnectAsync("alice");
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite);
        var uids = await inbox.SearchAsync(SearchQuery.All);
        Assert.Equal(2, uids.Count);

        await inbox.AddFlagsAsync(uids[1], MessageFlags.Flagged | MessageFlags.Seen, silent: false);
        await inbox.StoreAsync(uids[0], new StoreFlagsRequest(StoreAction.Add, new HashSet<string> { "$Wichtig" }) { Silent = true });

        Assert.Equal([uids[1]], await inbox.SearchAsync(SearchQuery.Flagged));
        Assert.Equal([uids[0]], await inbox.SearchAsync(SearchQuery.NotSeen));
        Assert.Equal([uids[0]], await inbox.SearchAsync(SearchQuery.HasKeyword("$Wichtig")));
        Assert.Equal([uids[1]], await inbox.SearchAsync(SearchQuery.SubjectContains("zweite")));
        Assert.Equal([uids[0]], await inbox.SearchAsync(SearchQuery.BodyContains("Grüßen")));
        Assert.Equal(uids, await inbox.SearchAsync(SearchQuery.FromContains("sender@remote.test")));
        Assert.Equal(uids, await inbox.SearchAsync(SearchQuery.DeliveredAfter(DateTime.UtcNow.AddDays(-2))));
        Assert.Empty(await inbox.SearchAsync(SearchQuery.DeliveredBefore(DateTime.UtcNow.AddDays(-2))));
        Assert.Equal([uids[1]], await inbox.SearchAsync(SearchQuery.Not(SearchQuery.NotSeen).And(SearchQuery.Flagged)));
        Assert.Equal(uids, await inbox.SearchAsync(SearchQuery.Flagged.Or(SearchQuery.HasKeyword("$Wichtig"))));

        await inbox.RemoveFlagsAsync(uids[1], MessageFlags.Flagged, silent: false);
        Assert.Empty(await inbox.SearchAsync(SearchQuery.Flagged));
    }

    [Fact]
    public async Task Append_returns_uid_and_keeps_flags()
    {
        using var client = await ConnectAsync("alice");
        var drafts = client.GetFolder(SpecialFolder.Drafts);

        var uid = await drafts.AppendAsync(new AppendRequest(CreateSimpleMessage("Entwurf", "noch nicht fertig"), MessageFlags.Draft | MessageFlags.Seen));

        Assert.NotNull(uid);
        await drafts.OpenAsync(FolderAccess.ReadOnly);
        var summary = Assert.Single(await drafts.FetchAsync(0, -1, MessageSummaryItems.Flags | MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope));
        Assert.Equal(uid.Value, summary.UniqueId);
        Assert.True(summary.Flags!.Value.HasFlag(MessageFlags.Draft));
        Assert.Equal("Entwurf", summary.Envelope.Subject);
    }

    [Fact]
    public async Task Copies_moves_and_expunges()
    {
        await DeliverAsync(CreateSimpleMessage("Eins", "1"), "alice@example.test");
        await DeliverAsync(CreateSimpleMessage("Zwei", "2"), "alice@example.test");
        using var client = await ConnectAsync("alice");
        var inbox = client.Inbox;
        var archive = await client.GetFolder(client.PersonalNamespaces[0]).CreateAsync("Archiv", isMessageFolder: true);
        var trash = client.GetFolder(SpecialFolder.Trash);
        await inbox.OpenAsync(FolderAccess.ReadWrite);
        var uids = await inbox.SearchAsync(SearchQuery.All);

        var copied = await inbox.CopyToAsync(uids[0], archive);
        Assert.NotNull(copied);

        var moved = await inbox.MoveToAsync(uids[1], trash);
        Assert.NotNull(moved);
        Assert.Equal(1, inbox.Count);

        await inbox.AddFlagsAsync(uids[0], MessageFlags.Deleted, silent: true);
        await inbox.ExpungeAsync();
        Assert.Equal(0, inbox.Count);

        await archive.OpenAsync(FolderAccess.ReadOnly);
        Assert.Equal("Eins", (await archive.GetMessageAsync(copied.Value)).Subject);
        await trash.OpenAsync(FolderAccess.ReadOnly);
        Assert.Equal("Zwei", (await trash.GetMessageAsync(moved.Value)).Subject);
    }

    [Fact]
    public async Task Creates_renames_and_deletes_folders_with_umlauts()
    {
        using var client = await ConnectAsync("alice");
        var root = client.GetFolder(client.PersonalNamespaces[0]);

        var invoices = await root.CreateAsync("Rechnungen", isMessageFolder: true);
        var year = await invoices.CreateAsync("Geschäftlich 2024", isMessageFolder: true);
        Assert.Equal("Rechnungen/Geschäftlich 2024", year.FullName);

        await year.RenameAsync(invoices, "Geschäftlich 2025");
        var children = await invoices.GetSubfoldersAsync();
        Assert.Equal(["Geschäftlich 2025"], children.Select(f => f.Name));

        await Assert.ThrowsAsync<ImapCommandException>(() => invoices.DeleteAsync());
        await children[0].DeleteAsync();
        await invoices.DeleteAsync();
        Assert.DoesNotContain("Rechnungen", (await root.GetSubfoldersAsync()).Select(f => f.Name));
    }

    [Fact]
    public async Task Idle_reports_new_mail_immediately()
    {
        using var client = await ConnectAsync("alice");
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly);
        var arrived = new TaskCompletionSource();
        inbox.CountChanged += (_, _) => arrived.TrySetResult();

        using var done = new CancellationTokenSource();
        var idle = client.IdleAsync(done.Token);
        await Task.Delay(200);
        await DeliverAsync(CreateSimpleMessage("Neu", "Push!"), "alice@example.test");

        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await done.CancelAsync();
        await idle;
        Assert.Equal(1, inbox.Count);
    }

    [Fact]
    public async Task Changes_from_other_sessions_are_reported()
    {
        await DeliverAsync(CreateSimpleMessage("Geteilt", "x"), "alice@example.test");
        using var first = await ConnectAsync("alice");
        using var second = await ConnectAsync("alice");
        await first.Inbox.OpenAsync(FolderAccess.ReadWrite);
        await second.Inbox.OpenAsync(FolderAccess.ReadWrite);
        var flagsChanged = new TaskCompletionSource<MessageFlagsChangedEventArgs>();
        first.Inbox.MessageFlagsChanged += (_, e) => flagsChanged.TrySetResult(e);
        var expunged = new TaskCompletionSource();
        first.Inbox.MessageExpunged += (_, _) => expunged.TrySetResult();

        var uid = (await second.Inbox.SearchAsync(SearchQuery.All)).Single();
        await second.Inbox.AddFlagsAsync(uid, MessageFlags.Flagged, silent: true);
        await first.NoOpAsync();
        Assert.True((await flagsChanged.Task.WaitAsync(TimeSpan.FromSeconds(5))).Flags.HasFlag(MessageFlags.Flagged));

        await second.Inbox.AddFlagsAsync(uid, MessageFlags.Deleted, silent: true);
        await second.Inbox.ExpungeAsync();
        await first.NoOpAsync();
        await expunged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, first.Inbox.Count);
    }

    [Fact]
    public async Task Users_only_see_their_own_mailbox()
    {
        await DeliverAsync(CreateSimpleMessage("Für Alice", "privat"), "alice@example.test");

        using var bob = await ConnectAsync("bob");
        await bob.Inbox.OpenAsync(FolderAccess.ReadOnly);
        Assert.Equal(0, bob.Inbox.Count);
    }

    private async Task<ImapClient> ConnectAsync(string user)
    {
        var client = new ImapClient { ServerCertificateValidationCallback = (_, _, _, _) => true };
        await client.ConnectAsync("127.0.0.1", _server.ImapsPort, SecureSocketOptions.SslOnConnect);
        await client.AuthenticateAsync($"{user}@example.test", TestServer.Password);
        return client;
    }

    private async Task DeliverAsync(MimeMessage message, string recipient)
    {
        using var smtp = new SmtpClient { ServerCertificateValidationCallback = (_, _, _, _) => true };
        await smtp.ConnectAsync("127.0.0.1", _server.InboundPort, SecureSocketOptions.StartTls);
        await smtp.SendAsync(message, MailboxAddress.Parse("sender@remote.test"), [MailboxAddress.Parse(recipient)]);
        await smtp.DisconnectAsync(true);
    }

    private static readonly byte[] PdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4\n% fake pdf for tests\n%%EOF\n");

    private static MimeMessage CreateMultipartMessage()
    {
        var builder = new BodyBuilder
        {
            TextBody = "Hallo, anbei die Rechnung. Grüße",
            HtmlBody = "<p>Hallo, anbei die <b>Rechnung</b>. Grüße</p>",
        };
        builder.Attachments.Add("rechnung.pdf", PdfBytes, new ContentType("application", "pdf"));

        var message = new MimeMessage { Subject = "Rechnung März – Übersicht", Body = builder.ToMessageBody() };
        message.From.Add(new MailboxAddress("Müller GmbH", "sender@remote.test"));
        message.To.Add(new MailboxAddress("Alice", "alice@example.test"));
        message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId("remote.test");
        return message;
    }

    private static MimeMessage CreateSimpleMessage(string subject, string body)
    {
        var message = new MimeMessage { Subject = subject, Body = new TextPart("plain") { Text = body } };
        message.From.Add(MailboxAddress.Parse("sender@remote.test"));
        message.To.Add(MailboxAddress.Parse("alice@example.test"));
        return message;
    }
}
