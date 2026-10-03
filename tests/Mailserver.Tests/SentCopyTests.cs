using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using Mailserver.Core.Storage;
using MimeKit;

namespace Mailserver.Tests;

/// <summary>Copies of mail sent via SMTP submission in the sender's Sent folder, without duplicates.</summary>
public sealed class SentCopyTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task Submitted_mail_is_stored_in_sent()
    {
        var message = await SubmitAsync("Ohne Kopie vom Programm");

        var sent = Assert.Single(Sent());
        Assert.True(sent.HasFlag(Mailserver.Core.Storage.MessageFlags.Seen));
        var content = await _server.ReadAsync(sent);
        Assert.Contains("Subject: Ohne Kopie vom Programm", content);
        Assert.Contains(message.MessageId, content);
        Assert.Contains("DKIM-Signature:", content);
    }

    [Theory]
    [InlineData("Sent")]
    [InlineData("Sent Messages")]
    public async Task Copy_stored_by_the_mail_program_replaces_the_server_copy(string folderName)
    {
        var message = await SubmitAsync("Programm legt selbst ab");
        message.Bcc.Add(MailboxAddress.Parse("geheim@remote.test"));

        using var imap = await ConnectImapAsync();
        var folder = folderName == "Sent" ? imap.GetFolder(SpecialFolder.Sent) : await imap.GetFolder(imap.PersonalNamespaces[0]).CreateAsync(folderName, true);
        await folder.AppendAsync(new AppendRequest(message, MailKit.MessageFlags.Seen));

        // "Sent Messages" is an alias of Sent, so in both cases exactly the program's copy remains in Sent.
        var copy = Assert.Single(Sent());
        Assert.Contains("Bcc: geheim@remote.test", await _server.ReadAsync(copy));
    }

    [Fact]
    public async Task Draft_with_the_same_message_id_keeps_the_server_copy()
    {
        var message = await SubmitAsync("Entwurf");

        using var imap = await ConnectImapAsync();
        await imap.GetFolder(SpecialFolder.Drafts).AppendAsync(new AppendRequest(message, MailKit.MessageFlags.Draft));

        Assert.Single(Sent());
    }

    [Fact]
    public async Task Server_copy_can_be_switched_off()
    {
        await _server.DisposeAsync();
        _server = await TestServer.StartAsync(new Dictionary<string, string?> { ["Mailserver:Smtp:SaveSentCopies"] = "false" });

        await SubmitAsync("Keine Kopie");

        Assert.Empty(Sent());
    }

    [Theory]
    [InlineData("Subject: x\r\nMessage-ID: <abc@host>\r\n\r\nBody", "abc@host")]
    [InlineData("message-id:\r\n <folded@host>\r\nSubject: x\r\n\r\n", "folded@host")]
    [InlineData("Subject: x\n\nMessage-ID: <in-body@host>\n", null)]
    [InlineData("Subject: x\r\n\r\n", null)]
    public void Reads_message_id_from_the_header(string message, string? expected) =>
        Assert.Equal(expected, SentCopies.ReadMessageId(Encoding.ASCII.GetBytes(message)));

    private IReadOnlyList<StoredMessage> Sent() =>
        _server.HostMailboxes.ListMessages(_server.HostMailboxes.GetFolder(_server.User("alice").Id, "Sent")!.Id);

    private async Task<MimeMessage> SubmitAsync(string subject)
    {
        var message = new MimeMessage { Subject = subject, Body = new TextPart("plain") { Text = "Hallo" } };
        message.From.Add(MailboxAddress.Parse("alice@example.test"));
        message.To.Add(MailboxAddress.Parse("bob@example.test"));
        message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId("client.test");

        using var smtp = new SmtpClient { ServerCertificateValidationCallback = (_, _, _, _) => true };
        await smtp.ConnectAsync("127.0.0.1", _server.SubmissionPort, SecureSocketOptions.StartTls);
        await smtp.AuthenticateAsync("alice@example.test", TestServer.Password);
        await smtp.SendAsync(message);
        await smtp.DisconnectAsync(true);
        return message;
    }

    private async Task<ImapClient> ConnectImapAsync()
    {
        var client = new ImapClient { ServerCertificateValidationCallback = (_, _, _, _) => true };
        await client.ConnectAsync("127.0.0.1", _server.ImapsPort, SecureSocketOptions.SslOnConnect);
        await client.AuthenticateAsync("alice@example.test", TestServer.Password);
        return client;
    }
}
