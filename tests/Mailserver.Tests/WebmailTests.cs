using System.Net;
using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using Mailserver.Core.SpamLogging;
using Mailserver.Core.Storage;
using Mailserver.Web.Webmail;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace Mailserver.Tests;

public class MailRendererTests
{
    private static MimeMessage Html(string html, params MimeEntity[] related)
    {
        var builder = new BodyBuilder { HtmlBody = html };
        foreach (var part in related)
        {
            builder.LinkedResources.Add(part);
        }

        return new MimeMessage { Body = builder.ToMessageBody() };
    }

    [Theory]
    [InlineData("<script>alert(1)</script><p>Hallo</p>", "<script")]
    [InlineData("<img src=x onerror=\"alert(1)\">", "onerror")]
    [InlineData("<a href=\"javascript:alert(1)\">klick</a>", "javascript:")]
    [InlineData("<iframe src=\"https://evil.test\"></iframe>", "<iframe")]
    [InlineData("<form action=\"https://evil.test\"><input name=p></form>", "<form")]
    [InlineData("<a href=\"data:text/html,<script>alert(1)</script>\">x</a>", "data:text/html")]
    [InlineData("<svg onload=alert(1)>", "onload")]
    [InlineData("<meta http-equiv=\"refresh\" content=\"0;url=https://evil.test\">", "http-equiv")]
    public void Removes_active_content(string html, string forbidden)
    {
        var rendered = MailRenderer.Render(Html(html));
        Assert.DoesNotContain(forbidden, rendered.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Keeps_formatting_links_and_styles()
    {
        var rendered = MailRenderer.Render(Html("<style>p{color:red}</style><p style=\"font-weight:bold\"><a href=\"https://shop.test/a\">Shop</a></p>"));
        Assert.Contains("<a href=\"https://shop.test/a\">Shop</a>", rendered.Html);
        Assert.Contains("font-weight: bold", rendered.Html);
        Assert.Contains("<style>", rendered.Html);
        Assert.False(rendered.HasRemoteContent);
    }

    [Fact]
    public void Inlines_embedded_images_and_detects_remote_ones()
    {
        var image = new MimePart("image", "png") { ContentId = "logo@test", Content = new MimeContent(new MemoryStream([1, 2, 3])) };
        var rendered = MailRenderer.Render(Html("<img src=\"cid:logo@test\"><img src=\"https://tracker.test/pixel.gif\">", image));

        Assert.Contains("src=\"data:image/png;base64,AQID\"", rendered.Html);
        Assert.True(rendered.HasRemoteContent);
    }

    [Fact]
    public void Plain_text_is_escaped_and_linkified()
    {
        var message = new MimeMessage { Body = new TextPart("plain") { Text = "<b>nicht fett</b> siehe https://beispiel.test/x?a=1&b=2" } };
        var rendered = MailRenderer.Render(message).Html;

        Assert.Contains("&lt;b&gt;nicht fett&lt;/b&gt;", rendered);
        Assert.Contains("<a href=\"https://beispiel.test/x?a=1&amp;b=2\"", rendered);
    }

    [Theory]
    [InlineData("..\\..\\windows\\system32\\evil.exe", "evil.exe")]
    [InlineData("rechnung:2024?.pdf", "rechnung2024.pdf")]
    [InlineData("", "anhang")]
    public void Sanitizes_file_names(string name, string expected) => Assert.Equal(expected, MailAttachments.SafeName(name, "anhang"));
}

public sealed class WebmailTests : IAsyncLifetime
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4 Testinhalt %%EOF");
    private TestServer _server = null!;
    private WebClient _web = null!;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync();
        _web = new WebClient(_server.WebPort);
        await _web.LoginAsync("alice@example.test", TestServer.Password);
    }

    public async Task DisposeAsync()
    {
        _web.Dispose();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task Lists_reads_and_marks_message_as_seen()
    {
        await DeliverAsync(HtmlMessage("Angebot März"));

        await _web.GetAsync("/Mail");
        Assert.Contains("Angebot März", _web.LastPage);
        Assert.Contains("class=\"unread\"", _web.LastPage);

        var uid = _server.Inbox("alice").Single().Uid;
        var read = await _web.GetAsync($"/Mail/Read?folder=INBOX&uid={uid}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Contains("sandbox=\"allow-popups allow-popups-to-escape-sandbox\"", _web.LastPage);
        Assert.Contains("angebot.pdf", _web.LastPage);
        Assert.Contains("Externe Bilder wurden", _web.LastPage);
        Assert.True(_server.Inbox("alice").Single().HasFlag(MessageFlags.Seen));
    }

    [Fact]
    public async Task Body_is_sanitized_and_sandboxed()
    {
        await DeliverAsync(HtmlMessage("Test"));
        var uid = _server.Inbox("alice").Single().Uid;

        var body = await _web.GetAsync($"/Mail/Body?folder=INBOX&uid={uid}");
        var csp = body.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("sandbox", csp);
        Assert.Contains("img-src data:;", csp);
        Assert.Equal("SAMEORIGIN", body.Headers.GetValues("X-Frame-Options").Single());
        Assert.DoesNotContain("<script", _web.LastPage);
        Assert.Contains("Hallo <b>Alice</b>", _web.LastPage);

        var withImages = await _web.GetAsync($"/Mail/Body?folder=INBOX&uid={uid}&images=1");
        Assert.Contains("img-src data: https: http:", withImages.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Downloads_attachment_as_octet_stream()
    {
        await DeliverAsync(HtmlMessage("Mit Anhang"));
        var uid = _server.Inbox("alice").Single().Uid;

        var response = await _web.GetAsync($"/Mail/Attachment?folder=INBOX&uid={uid}&index=0");
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("angebot.pdf", response.Content.Headers.ContentDisposition!.FileName);
        Assert.Equal(Pdf, await _web.GetBytesAsync($"/Mail/Attachment?folder=INBOX&uid={uid}&index=0"));
    }

    [Fact]
    public async Task Users_cannot_read_other_mailboxes()
    {
        await DeliverAsync(HtmlMessage("Privat"));
        var uid = _server.Inbox("alice").Single().Uid;

        using var bob = new WebClient(_server.WebPort);
        await bob.LoginAsync("bob@example.test", TestServer.Password);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/Mail/Read?folder=INBOX&uid={uid}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/Mail/Body?folder=INBOX&uid={uid}")).StatusCode);
        await bob.GetAsync("/Mail");
        Assert.DoesNotContain("Privat", bob.LastPage);
    }

    [Fact]
    public async Task Sends_signed_mail_with_attachment_and_keeps_copy_in_sent()
    {
        var big = new byte[2 * 1024 * 1024];
        Random.Shared.NextBytes(big);
        await _web.PostMultipartAsync("/Mail/Compose", "/Mail/Compose?handler=Send",
            [("Form.From", "alice@example.test"), ("Form.To", "Bob <bob@example.test>"), ("Form.Bcc", "someone@remote.test"),
             ("Form.Subject", "Unterlagen"), ("Form.Body", "Hallo Bob,\nanbei die Unterlagen.")],
            ("Files", "unterlagen.pdf", big));

        Assert.Contains("Nachricht gesendet", _web.LastPage);
        var received = await _server.ReadAsync(Assert.Single(_server.Inbox("bob")));
        var message = MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(received)));
        Assert.Equal("Unterlagen", message.Subject);
        Assert.Contains("DKIM-Signature:", received);
        Assert.Empty(message.Bcc);
        Assert.Equal("unterlagen.pdf", message.Attachments.OfType<MimePart>().Single().FileName);

        await TestServer.WaitUntilAsync(() => !_server.Remote.Messages.IsEmpty, "Bcc delivered to remote");
        Assert.DoesNotContain("someone@remote.test", _server.Remote.Messages.Single().Content);

        var sent = _server.HostMailboxes.ListMessages(_server.HostMailboxes.GetFolder(_server.User("alice").Id, "Sent")!.Id);
        Assert.Single(sent);
        var history = _server.Services.GetRequiredService<SpamLog>().Query(new SpamLogQuery(Stage: SpamLogStage.Submission));
        Assert.Contains("Webmail", Assert.Single(history).Detail);
    }

    [Fact]
    public async Task Refuses_foreign_sender_and_invalid_recipients()
    {
        await _web.PostMultipartAsync("/Mail/Compose", "/Mail/Compose?handler=Send",
            [("Form.From", "bob@example.test"), ("Form.To", "x@remote.test"), ("Form.Subject", "Fake"), ("Form.Body", "x")]);
        Assert.Contains("Absenderadresse darf nicht verwendet werden", _web.LastPage);

        await _web.PostMultipartAsync("/Mail/Compose", "/Mail/Compose?handler=Send",
            [("Form.From", "alice@example.test"), ("Form.To", "kein-gültiger-empfänger"), ("Form.Subject", "x"), ("Form.Body", "x")]);
        Assert.Contains("Ungültige Adresse", _web.LastPage);
        Assert.Contains("kein-gültiger-empfänger", _web.LastPage); // input kept
        Assert.Empty(_server.Inbox("bob"));
    }

    [Fact]
    public async Task Reply_sets_threading_headers_and_answered_flag()
    {
        await DeliverAsync(HtmlMessage("Frage", from: "bob@example.test"));
        var original = _server.Inbox("alice").Single();
        await _web.GetAsync($"/Mail/Compose?mode=reply&folder=INBOX&uid={original.Uid}");
        Assert.Contains("Re: Frage", _web.LastPage);
        Assert.Contains("&gt; Hallo Alice", _web.LastPage);

        var messageId = MimeMessage.Load(_server.HostMailboxes.GetMessagePath(original)).MessageId;
        await _web.PostMultipartAsync($"/Mail/Compose?mode=reply&folder=INBOX&uid={original.Uid}", "/Mail/Compose?handler=Send",
            [("Form.Mode", "reply"), ("Form.OriginalFolder", "INBOX"), ("Form.OriginalUid", original.Uid.ToString()), ("Form.InReplyTo", messageId),
             ("Form.References", messageId), ("Form.From", "alice@example.test"), ("Form.To", "bob@example.test"), ("Form.Subject", "Re: Frage"),
             ("Form.Body", "Antwort")]);

        var reply = MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(await _server.ReadAsync(_server.Inbox("bob").Single()))));
        Assert.Equal(messageId, reply.InReplyTo);
        Assert.True(_server.Inbox("alice").Single().HasFlag(MessageFlags.Answered));
    }

    [Fact]
    public async Task Forward_carries_original_attachments()
    {
        await DeliverAsync(HtmlMessage("Weiterleiten"));
        var uid = _server.Inbox("alice").Single().Uid;
        await _web.GetAsync($"/Mail/Compose?mode=forward&folder=INBOX&uid={uid}");
        Assert.Contains("WG: Weiterleiten", _web.LastPage);
        Assert.Contains("angebot.pdf", _web.LastPage);

        await _web.PostMultipartAsync($"/Mail/Compose?mode=forward&folder=INBOX&uid={uid}", "/Mail/Compose?handler=Send",
            [("Form.Mode", "forward"), ("Form.OriginalFolder", "INBOX"), ("Form.OriginalUid", uid.ToString()), ("Form.KeepAttachments", "true"),
             ("Form.From", "alice@example.test"), ("Form.To", "bob@example.test"), ("Form.Subject", "WG: Weiterleiten"), ("Form.Body", "FYI")]);

        var forwarded = MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(await _server.ReadAsync(_server.Inbox("bob").Single()))));
        var part = Assert.Single(forwarded.Attachments.OfType<MimePart>());
        using var decoded = new MemoryStream();
        part.Content.DecodeTo(decoded);
        Assert.Equal(Pdf, decoded.ToArray());
    }

    [Fact]
    public async Task Drafts_are_saved_and_replaced_when_sent()
    {
        await _web.PostMultipartAsync("/Mail/Compose", "/Mail/Compose?handler=Draft",
            [("Form.From", "alice@example.test"), ("Form.To", "bob@example.test"), ("Form.Subject", "Entwurf 1"), ("Form.Body", "Halb fertig")]);
        Assert.Contains("Entwurf gespeichert", _web.LastPage);
        var drafts = _server.HostMailboxes.GetFolder(_server.User("alice").Id, "Drafts")!;
        var draft = Assert.Single(_server.HostMailboxes.ListMessages(drafts.Id));
        Assert.True(draft.HasFlag(MessageFlags.Draft));
        Assert.Contains("Halb fertig", _web.LastPage);

        await _web.PostMultipartAsync($"/Mail/Compose?mode=draft&folder=Drafts&uid={draft.Uid}", "/Mail/Compose?handler=Send",
            [("Form.Mode", "draft"), ("Form.OriginalFolder", "Drafts"), ("Form.OriginalUid", draft.Uid.ToString()),
             ("Form.From", "alice@example.test"), ("Form.To", "bob@example.test"), ("Form.Subject", "Entwurf 1"), ("Form.Body", "Jetzt fertig")]);

        Assert.Empty(_server.HostMailboxes.ListMessages(drafts.Id));
        Assert.Single(_server.Inbox("bob"));
    }

    [Fact]
    public async Task Bulk_actions_move_delete_and_record_spam_feedback()
    {
        await DeliverAsync(HtmlMessage("Eins"));
        await DeliverAsync(HtmlMessage("Zwei"));
        var uids = _server.Inbox("alice").Select(m => m.Uid.ToString()).ToArray();

        await _web.PostAsync("/Mail", "/Mail?handler=Bulk", ("folder", "INBOX"), ("page", "0"), ("op", "spam"), ("uids", uids[0]));
        Assert.Contains("als Spam verschoben", _web.LastPage);
        var junk = _server.HostMailboxes.GetFolder(_server.User("alice").Id, "Junk")!;
        Assert.Single(_server.HostMailboxes.ListMessages(junk.Id));
        Assert.Single(_server.Services.GetRequiredService<SpamLog>().Query(new SpamLogQuery(Action: SpamLogAction.MarkedSpam)));

        await _web.PostAsync("/Mail", "/Mail?handler=Bulk", ("folder", "INBOX"), ("page", "0"), ("op", "delete"), ("uids", uids[1]));
        var trash = _server.HostMailboxes.GetFolder(_server.User("alice").Id, "Trash")!;
        var trashed = Assert.Single(_server.HostMailboxes.ListMessages(trash.Id));

        await _web.PostAsync("/Mail?folder=Trash", "/Mail?handler=Bulk", ("folder", "Trash"), ("page", "0"), ("op", "delete"), ("uids", trashed.Uid.ToString()));
        Assert.Empty(_server.HostMailboxes.ListMessages(trash.Id));
        Assert.Empty(_server.Inbox("alice"));
    }

    [Fact]
    public async Task Search_filters_the_list()
    {
        await DeliverAsync(HtmlMessage("Rechnung 4711"));
        await DeliverAsync(HtmlMessage("Newsletter"));

        await _web.GetAsync("/Mail?folder=INBOX&q=rechnung");
        Assert.Contains("Rechnung 4711", _web.LastPage);
        Assert.DoesNotContain("Newsletter", _web.LastPage);
    }

    private static MimeMessage HtmlMessage(string subject, string from = "shop@remote.test")
    {
        var builder = new BodyBuilder
        {
            HtmlBody = "<p>Hallo <b>Alice</b></p><script>alert('x')</script><img src=\"https://tracker.test/p.gif\">",
            TextBody = "Hallo Alice",
        };
        builder.Attachments.Add("angebot.pdf", Pdf, new ContentType("application", "pdf"));
        var message = new MimeMessage { Subject = subject, Body = builder.ToMessageBody() };
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse("alice@example.test"));
        return message;
    }

    private async Task DeliverAsync(MimeMessage message)
    {
        using var smtp = new SmtpClient();
        await smtp.ConnectAsync("127.0.0.1", _server.InboundPort, SecureSocketOptions.None);
        // Local senders must authenticate on port 25; deliver those as the envelope of a remote sender.
        await smtp.SendAsync(message, MailboxAddress.Parse("shop@remote.test"), [MailboxAddress.Parse("alice@example.test")]);
        await smtp.DisconnectAsync(true);
    }
}
