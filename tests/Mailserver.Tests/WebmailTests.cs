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
    public async Task Embedded_images_are_linked_not_copied_into_the_body()
    {
        var picture = Enumerable.Range(0, 50_000).Select(i => (byte)i).ToArray();
        var image = new MimePart("image", "png")
            { ContentId = "logo@test", ContentTransferEncoding = ContentEncoding.Base64, Content = new MimeContent(new MemoryStream(picture)) };
        var builder = new BodyBuilder { HtmlBody = string.Concat(Enumerable.Repeat("<p><img src=\"cid:logo@test\"></p>", 20)) };
        builder.LinkedResources.Add(image);
        var message = new MimeMessage { Subject = "Bilder", Body = builder.ToMessageBody() };
        using var raw = new MemoryStream();
        message.WriteTo(raw);
        var stored = await _server.HostMailboxes.AppendAsync(_server.User("alice"), raw.ToArray());

        var response = await _web.GetAsync($"/Mail/Body?folder=INBOX&uid={stored.Uid}");
        Assert.True(_web.LastPage.Length < 10_000, $"body is {_web.LastPage.Length} characters");
        Assert.DoesNotContain("data:image", _web.LastPage);
        Assert.Contains("/Mail/Inline", string.Join(" ", response.Headers.GetValues("Content-Security-Policy")));
        var link = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(_web.LastPage, "src=\"(/Mail/Inline[^\"]+)\"").Groups[1].Value);
        Assert.NotEmpty(link);

        // The sandboxed frame sends no cookies: the signed link alone must be enough – and nothing else.
        using var anonymous = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true })
            { BaseAddress = new Uri($"https://127.0.0.1:{_server.WebPort}") };
        var served = await anonymous.GetAsync(link);
        Assert.Equal("image/png", served.Content.Headers.ContentType?.MediaType);
        Assert.Equal(picture, await served.Content.ReadAsByteArrayAsync());
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await anonymous.GetAsync(link.Replace("&s=", "&s=0"))).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await anonymous.GetAsync(link.Replace($"m={stored.Id}", $"m={stored.Id + 1}"))).StatusCode);
    }

    [Fact]
    public async Task Trash_and_spam_can_be_emptied_at_once()
    {
        var alice = _server.User("alice");
        for (var i = 0; i < 3; i++)
        {
            await _server.HostMailboxes.AppendAsync(alice, Encoding.ASCII.GetBytes($"Subject: Weg {i}\r\n\r\nText"), "Trash");
            await _server.HostMailboxes.AppendAsync(alice, Encoding.ASCII.GetBytes($"Subject: Spam {i}\r\n\r\nText"), "Junk");
        }

        await _server.HostMailboxes.AppendAsync(alice, Encoding.ASCII.GetBytes("Subject: Bleibt\r\n\r\nText"));

        await _web.GetAsync("/Mail?folder=Trash");
        Assert.Contains("data-select-all", _web.LastPage);
        Assert.Contains("Papierkorb leeren", _web.LastPage);
        Assert.Contains("Alle 3 Nachrichten werden endgültig gelöscht", _web.LastPage);

        await _web.PostAsync("/Mail?folder=Trash", "/Mail?handler=Empty", ("folder", "Trash"));
        Assert.Contains("3 Nachricht(en) endgültig gelöscht", _web.LastPage);
        await _web.PostAsync("/Mail?folder=Junk", "/Mail?handler=Empty", ("folder", "Junk"));
        Assert.Empty(_server.HostMailboxes.ListMessages(_server.HostMailboxes.GetFolder(alice.Id, "Trash")!.Id));
        Assert.Empty(_server.HostMailboxes.ListMessages(_server.HostMailboxes.GetFolder(alice.Id, "Junk")!.Id));

        // Other folders are never emptied this way.
        await _web.GetAsync("/Mail?folder=INBOX");
        Assert.DoesNotContain("leeren", _web.LastPage);
        await _web.PostAsync("/Mail?folder=INBOX", "/Mail?handler=Empty", ("folder", "INBOX"));
        Assert.Contains("Nur Papierkorb und Spam", _web.LastPage);
        Assert.Single(_server.Inbox("alice"));
    }

    [Fact]
    public async Task Pages_through_a_long_folder_to_the_oldest_mail()
    {
        var start = new DateTimeOffset(2024, 1, 1, 8, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 120; i++)
        {
            await _server.HostMailboxes.AppendAsync(_server.User("alice"), Encoding.ASCII.GetBytes($"Subject: Nummer {i:000}\r\n\r\nText"),
                internalDate: start.AddDays(i));
        }

        await _web.GetAsync("/Mail?folder=INBOX");
        Assert.Contains("Seite 1 von 3", _web.LastPage);
        Assert.Contains("Nummer 119", _web.LastPage);

        await _web.GetAsync("/Mail?folder=INBOX&page=1");
        Assert.Contains("Seite 2 von 3", _web.LastPage);
        Assert.Contains("Nummer 069", _web.LastPage);

        await _web.GetAsync("/Mail?folder=INBOX&page=2");
        Assert.Contains("Seite 3 von 3", _web.LastPage);
        Assert.Contains("Nummer 000", _web.LastPage);
        Assert.DoesNotContain("Ältere", _web.LastPage);

        await _web.GetAsync("/Mail?folder=INBOX&page=7");
        Assert.Contains("Seite 3 von 3", _web.LastPage);
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
        Assert.Matches(@"img-src data: https://127\.0\.0\.1:\d+/Mail/Inline;", csp); // only own embedded images, nothing from outside
        Assert.Equal("SAMEORIGIN", body.Headers.GetValues("X-Frame-Options").Single());
        Assert.DoesNotContain("<script", _web.LastPage);
        Assert.Contains("Hallo <b>Alice</b>", _web.LastPage);

        var withImages = await _web.GetAsync($"/Mail/Body?folder=INBOX&uid={uid}&images=1");
        Assert.Matches(@"img-src data: \S+/Mail/Inline https: http:", withImages.Headers.GetValues("Content-Security-Policy").Single());
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
    public async Task Pdf_attachment_can_be_viewed_in_the_browser_before_downloading()
    {
        await DeliverAsync(HtmlMessage("Mit PDF"));
        var uid = _server.Inbox("alice").Single().Uid;

        await _web.GetAsync($"/Mail/Read?folder=INBOX&uid={uid}");
        Assert.Contains($"index=0&amp;view=1", _web.LastPage);
        Assert.Contains("Herunterladen", _web.LastPage);

        var shown = await _web.GetAsync($"/Mail/Attachment?folder=INBOX&uid={uid}&index=0&view=1");
        Assert.Equal("application/pdf", shown.Content.Headers.ContentType!.MediaType);
        Assert.Equal("inline", shown.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("angebot.pdf", shown.Content.Headers.ContentDisposition.FileName);
        Assert.DoesNotContain("script-src", string.Join(" ", shown.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal(Pdf, await _web.GetBytesAsync($"/Mail/Attachment?folder=INBOX&uid={uid}&index=0&view=1"));
    }

    [Fact]
    public async Task Picture_attachment_is_shown_but_svg_is_only_downloaded()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 2, 3];
        var builder = new BodyBuilder { TextBody = "Fotos" };
        builder.Attachments.Add("urlaub.jpg", jpeg, new ContentType("application", "octet-stream"));
        builder.Attachments.Add("logo.svg", Encoding.ASCII.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"),
            new ContentType("image", "svg+xml"));
        var message = new MimeMessage { Subject = "Fotos", Body = builder.ToMessageBody() };
        message.From.Add(MailboxAddress.Parse("freund@remote.test"));
        message.To.Add(MailboxAddress.Parse("alice@example.test"));
        await DeliverAsync(message);
        var uid = _server.Inbox("alice").Single().Uid;

        await _web.GetAsync($"/Mail/Read?folder=INBOX&uid={uid}");
        Assert.Contains("index=0&amp;view=1", _web.LastPage);
        Assert.DoesNotContain("index=1&amp;view=1", _web.LastPage);

        var photo = await _web.GetAsync($"/Mail/Attachment?folder=INBOX&uid={uid}&index=0&view=1");
        Assert.Equal("image/jpeg", photo.Content.Headers.ContentType!.MediaType);
        Assert.Equal("inline", photo.Content.Headers.ContentDisposition!.DispositionType);

        var svg = await _web.GetAsync($"/Mail/Attachment?folder=INBOX&uid={uid}&index=1&view=1");
        Assert.Equal("application/octet-stream", svg.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", svg.Content.Headers.ContentDisposition!.DispositionType);
    }

    [Fact]
    public async Task Disguised_html_attachment_is_never_shown_in_the_browser()
    {
        var builder = new BodyBuilder { TextBody = "Rechnung anbei" };
        builder.Attachments.Add("rechnung.pdf", Encoding.ASCII.GetBytes("<html><script>alert(document.cookie)</script></html>"),
            new ContentType("application", "pdf"));
        var message = new MimeMessage { Subject = "Falsches PDF", Body = builder.ToMessageBody() };
        message.From.Add(MailboxAddress.Parse("evil@remote.test"));
        message.To.Add(MailboxAddress.Parse("alice@example.test"));
        await DeliverAsync(message);
        var uid = _server.Inbox("alice").Single().Uid;

        var response = await _web.GetAsync($"/Mail/Attachment?folder=INBOX&uid={uid}&index=0&view=1");
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
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

public sealed class WebmailEditorAndFolderTests : IAsyncLifetime
{
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
    public async Task Compose_page_loads_editor_script_from_own_server()
    {
        var page = await _web.GetAsync("/Mail/Compose");
        Assert.Contains("<script src=\"/assets/editor.js\"></script>", _web.LastPage);
        Assert.Contains("name=\"Form.BodyHtml\"", _web.LastPage);
        Assert.Contains("script-src 'self'", page.Headers.GetValues("Content-Security-Policy").Single());

        var script = await _web.GetAsync("/assets/editor.js");
        Assert.Equal("text/javascript", script.Content.Headers.ContentType!.MediaType);
        Assert.Contains("contentEditable", _web.LastPage);
    }

    [Fact]
    public async Task Formatted_mail_is_sanitized_and_sent_with_text_alternative()
    {
        await _web.PostMultipartAsync("/Mail/Compose", "/Mail/Compose?handler=Send",
            [("Form.From", "alice@example.test"), ("Form.To", "bob@example.test"), ("Form.Subject", "Formatiert"),
             ("Form.Body", "Hallo Bob wichtig"),
             ("Form.BodyHtml", "<p>Hallo <b>Bob</b> <span style=\"color: rgb(180, 35, 24);\">wichtig</span></p><ul><li>Punkt</li></ul>" +
                               "<script>alert(1)</script><a href=\"javascript:alert(1)\" onclick=\"x()\">Link</a><img src=\"data:image/png;base64,AAAA\">")]);

        var received = MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(await _server.ReadAsync(Assert.Single(_server.Inbox("bob"))))));
        Assert.Equal("Hallo Bob wichtig", received.TextBody.Trim());
        var html = received.HtmlBody;
        Assert.Contains("<b>Bob</b>", html);
        Assert.Contains("180, 35, 24", html); // text colour kept (the sanitizer may normalise rgb/rgba)
        Assert.Contains("<li>Punkt</li>", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("javascript:", html);
        Assert.DoesNotContain("onclick", html);
        Assert.DoesNotContain("data:image", html);
    }

    [Fact]
    public async Task Reply_quotes_original_formatting()
    {
        var builder = new BodyBuilder { HtmlBody = "<html><head><style>p{}</style></head><body><p>Original <i>kursiv</i></p></body></html>", TextBody = "Original kursiv" };
        var original = new MimeMessage { Subject = "Frage", Body = builder.ToMessageBody() };
        original.From.Add(MailboxAddress.Parse("kunde@remote.test"));
        original.To.Add(MailboxAddress.Parse("alice@example.test"));
        await _server.HostMailboxes.AppendAsync(_server.User("alice"), Encoding.UTF8.GetBytes(original.ToString()));

        var uid = _server.Inbox("alice").Single().Uid;
        await _web.GetAsync($"/Mail/Compose?mode=reply&folder=INBOX&uid={uid}");
        var hidden = System.Text.RegularExpressions.Regex.Match(_web.LastPage, "name=\"Form.BodyHtml\" value=\"([^\"]*)\"").Groups[1].Value;
        var html = System.Net.WebUtility.HtmlDecode(hidden);
        Assert.Contains("<blockquote", html);
        Assert.Contains("<i>kursiv</i>", html);
        Assert.DoesNotContain("<style", html);
        Assert.Contains("schrieb", html);
    }

    [Fact]
    public async Task Creates_renames_and_deletes_folders()
    {
        await _web.PostAsync("/Mail/Folders", "/Mail/Folders?handler=Create", ("name", "Projekte"), ("parent", ""));
        await _web.PostAsync("/Mail/Folders", "/Mail/Folders?handler=Create", ("name", "Kunde Müller"), ("parent", "Projekte"));
        var account = _server.User("alice");
        Assert.NotNull(_server.HostMailboxes.GetFolder(account.Id, "Projekte/Kunde Müller"));
        Assert.Contains("Ordner „Projekte/Kunde Müller“ angelegt", _web.LastPage);

        // A rule that files mail into the folder follows the rename.
        var rules = _server.Services.GetRequiredService<Mailserver.Core.Rules.RuleStore>();
        rules.Add("alice@example.test", "Müller", [new(Mailserver.Core.Rules.RuleField.From, Mailserver.Core.Rules.RuleOperator.Contains, "mueller")],
            Mailserver.Core.Rules.RuleAction.Move, "Projekte/Kunde Müller");

        await _web.PostAsync("/Mail/Folders", "/Mail/Folders?handler=Rename", ("name", "Projekte"), ("newName", "Aufträge"));
        Assert.NotNull(_server.HostMailboxes.GetFolder(account.Id, "Aufträge/Kunde Müller"));
        Assert.Equal("Aufträge/Kunde Müller", rules.List("alice@example.test").Single().Argument);

        await _web.PostAsync("/Mail/Folders", "/Mail/Folders?handler=Delete", ("name", "Aufträge"));
        Assert.Contains("zuerst die Unterordner", _web.LastPage);

        var sub = _server.HostMailboxes.GetFolder(account.Id, "Aufträge/Kunde Müller")!;
        await _server.HostMailboxes.AppendAsync(sub, Encoding.ASCII.GetBytes("Subject: x\r\n\r\ny"));
        await _web.PostAsync("/Mail/Folders", "/Mail/Folders?handler=Delete", ("name", "Aufträge/Kunde Müller"));
        Assert.Null(_server.HostMailboxes.GetFolder(account.Id, "Aufträge/Kunde Müller"));
        var trash = _server.HostMailboxes.GetFolder(account.Id, "Trash")!;
        Assert.Single(_server.HostMailboxes.ListMessages(trash.Id));
        Assert.Contains("in den Papierkorb verschoben", _web.LastPage);
    }

    [Theory]
    [InlineData("Create", "name", "Gelöschte Elemente", "gibt es schon")]
    [InlineData("Create", "name", "Sent", "existiert bereits")]
    [InlineData("Create", "name", "a/b", "Schrägstrich")]
    [InlineData("Create", "name", "Stern*", "nicht enthalten")]
    [InlineData("Rename", "name", "INBOX", "Systemordner")]
    [InlineData("Delete", "name", "Trash", "Systemordner")]
    public async Task Protects_system_folders_and_rejects_bad_names(string handler, string field, string value, string error)
    {
        await _web.PostAsync("/Mail/Folders", $"/Mail/Folders?handler={handler}", (field, value), ("parent", ""), ("newName", "Neu"));
        Assert.Contains(error, _web.LastPage);
        Assert.NotNull(_server.HostMailboxes.GetFolder(_server.User("alice").Id, "Trash"));
    }

    [Fact]
    public async Task Subfolders_appear_indented_in_navigation()
    {
        await _web.PostAsync("/Mail/Folders", "/Mail/Folders?handler=Create", ("name", "Archiv"), ("parent", ""));
        await _web.PostAsync("/Mail/Folders", "/Mail/Folders?handler=Create", ("name", "2025"), ("parent", "Archiv"));

        await _web.GetAsync("/Mail");
        Assert.Contains("href=\"/Mail?folder=Archiv%2F2025\"", _web.LastPage);
        Assert.Contains("Archiv › 2025", _web.LastPage);
        Assert.Contains("Ordner verwalten", _web.LastPage);
    }
}
