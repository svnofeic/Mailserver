using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Mailserver.Tests;

public sealed class SmtpIntegrationTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task Inbound_mail_for_local_user_lands_in_inbox()
    {
        await SendInboundAsync("sender@remote.test", "alice@example.test");

        var inbox = _server.Inbox("alice");
        var message = Assert.Single(inbox);
        var content = await _server.ReadAsync(message);
        Assert.StartsWith("Received: from ", content);
        Assert.Contains("by mail.example.test with ESMTPS", content);
        Assert.Contains("Subject: Test", content);
    }

    [Fact]
    public async Task Inbound_mail_to_alias_reaches_all_targets_once()
    {
        await SendInboundAsync("sender@remote.test", "info@example.test", "alice@example.test");

        Assert.Single(_server.Inbox("alice"));
        Assert.Single(_server.Inbox("bob"));
    }

    [Fact]
    public async Task Inbound_rejects_relaying()
    {
        var ex = await Assert.ThrowsAsync<SmtpCommandException>(() => SendInboundAsync("sender@remote.test", "victim@remote.test"));
        Assert.Equal(SmtpStatusCode.MailboxUnavailable, ex.StatusCode);
        Assert.Contains("Relaying denied", ex.Message);
    }

    [Fact]
    public async Task Inbound_rejects_unknown_local_user()
    {
        var ex = await Assert.ThrowsAsync<SmtpCommandException>(() => SendInboundAsync("sender@remote.test", "nobody@example.test"));
        Assert.Contains("User unknown", ex.Message);
    }

    [Fact]
    public async Task Inbound_rejects_spoofed_local_sender()
    {
        var ex = await Assert.ThrowsAsync<SmtpCommandException>(() => SendInboundAsync("alice@example.test", "bob@example.test"));
        Assert.Contains("submission port", ex.Message);
        Assert.Empty(_server.Inbox("bob"));
    }

    [Fact]
    public async Task Inbound_port_does_not_accept_logins()
    {
        using var client = await ConnectAsync(_server.InboundPort);
        await Assert.ThrowsAnyAsync<Exception>(() => client.AuthenticateAsync("alice@example.test", TestServer.Password));
    }

    [Fact]
    public async Task Submission_requires_authentication()
    {
        using var client = await ConnectAsync(_server.SubmissionPort);
        // MailKit reports the server's "530 authentication required" as ServiceNotAuthenticatedException.
        await Assert.ThrowsAsync<MailKit.ServiceNotAuthenticatedException>(() =>
            client.SendAsync(CreateMessage("alice@example.test", "someone@remote.test")));
        Assert.Empty(_server.Remote.Messages);
    }

    [Fact]
    public async Task Submission_rejects_wrong_password()
    {
        using var client = await ConnectAsync(_server.SubmissionPort);
        await Assert.ThrowsAsync<AuthenticationException>(() => client.AuthenticateAsync("alice@example.test", "wrong password"));
    }

    [Fact]
    public async Task Submission_rejects_foreign_sender_address()
    {
        using var client = await ConnectAsync(_server.SubmissionPort);
        await client.AuthenticateAsync("alice@example.test", TestServer.Password);

        var ex = await Assert.ThrowsAsync<SmtpCommandException>(() =>
            client.SendAsync(CreateMessage("bob@example.test", "someone@remote.test")));
        Assert.Contains("not allowed to send as", ex.Message);
    }

    [Fact]
    public async Task Submitted_mail_is_dkim_signed_and_delivered_remotely()
    {
        await SendSubmissionAsync("alice@example.test", "someone@remote.test", "bob@example.test");

        await TestServer.WaitUntilAsync(() => !_server.Remote.Messages.IsEmpty, "remote delivery");
        var delivered = Assert.Single(_server.Remote.Messages);
        Assert.Equal("alice@example.test", delivered.From);
        Assert.Equal(["someone@remote.test"], delivered.To);
        Assert.Contains("DKIM-Signature: ", delivered.Content);
        Assert.Contains("d=example.test", delivered.Content);
        Assert.Contains("s=test", delivered.Content);
        Assert.Contains("Message-Id: ", delivered.Content, StringComparison.OrdinalIgnoreCase);

        // The local recipient of the same message gets it directly.
        Assert.Single(_server.Inbox("bob"));
    }

    [Fact]
    public async Task Dkim_signature_verifies()
    {
        await SendSubmissionAsync("alice@example.test", "someone@remote.test");
        await TestServer.WaitUntilAsync(() => !_server.Remote.Messages.IsEmpty, "remote delivery");

        var raw = _server.Remote.Messages.Single().Content;
        var message = MimeMessage.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(raw)));
        var dkimKeys = (Mailserver.Core.Dkim.DkimKeyStore)_server.Services.GetService(typeof(Mailserver.Core.Dkim.DkimKeyStore))!;
        var verifier = new MimeKit.Cryptography.DkimVerifier(new StaticKeyLocator(dkimKeys.GetDnsRecord("example.test", "test")));
        var signature = message.Headers[message.Headers.IndexOf(HeaderId.DkimSignature)];

        Assert.True(await verifier.VerifyAsync(message, signature));
    }

    [Fact]
    public async Task Permanent_remote_rejection_bounces_to_sender()
    {
        await SendSubmissionAsync("alice@example.test", "reject-me@remote.test");

        await TestServer.WaitUntilAsync(() => _server.Inbox("alice").Count == 1, "bounce in sender's inbox");
        var bounce = await _server.ReadAsync(_server.Inbox("alice")[0]);
        Assert.Contains("MAILER-DAEMON@mail.example.test", bounce);
        Assert.Contains("reject-me@remote.test", bounce);
        Assert.Contains("Status: 5.1.1", bounce);
        Assert.Empty(_server.Remote.Messages);
    }

    [Fact]
    public async Task Temporary_remote_failure_stays_queued()
    {
        await SendSubmissionAsync("alice@example.test", "later@remote.test");

        var queue = (Mailserver.Core.Queue.OutboundQueue)_server.Services.GetService(typeof(Mailserver.Core.Queue.OutboundQueue))!;
        await TestServer.WaitUntilAsync(() => queue.List().Any(e => e.Attempts > 0), "deferred attempt");
        var entry = Assert.Single(queue.List());
        Assert.Contains("4", entry.LastError);
        Assert.Empty(_server.Inbox("alice"));
    }

    [Fact]
    public async Task Alias_forward_uses_alias_as_envelope_sender()
    {
        await SendInboundAsync("sender@remote.test", "forward@example.test");

        await TestServer.WaitUntilAsync(() => !_server.Remote.Messages.IsEmpty, "forwarded delivery");
        var forwarded = Assert.Single(_server.Remote.Messages);
        Assert.Equal("forward@example.test", forwarded.From);
        Assert.Equal(["someone@remote.test"], forwarded.To);
    }

    private async Task SendInboundAsync(string from, params string[] to)
    {
        using var client = await ConnectAsync(_server.InboundPort);
        await client.SendAsync(CreateMessage(from, to));
        await client.DisconnectAsync(true);
    }

    private async Task SendSubmissionAsync(string from, params string[] to)
    {
        using var client = await ConnectAsync(_server.SubmissionPort);
        await client.AuthenticateAsync(from, TestServer.Password);
        await client.SendAsync(CreateMessage(from, to));
        await client.DisconnectAsync(true);
    }

    private static async Task<SmtpClient> ConnectAsync(int port)
    {
        var client = new SmtpClient { ServerCertificateValidationCallback = (_, _, _, _) => true };
        await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.StartTls);
        return client;
    }

    private static MimeMessage CreateMessage(string from, params string[] to)
    {
        var message = new MimeMessage { Subject = "Test", Body = new TextPart("plain") { Text = "Grüße aus dem Test – äöü" } };
        message.From.Add(MailboxAddress.Parse(from));
        foreach (var recipient in to)
        {
            message.To.Add(MailboxAddress.Parse(recipient));
        }

        return message;
    }

    private sealed class StaticKeyLocator(string record) : MimeKit.Cryptography.DkimPublicKeyLocatorBase
    {
        public override Org.BouncyCastle.Crypto.AsymmetricKeyParameter LocatePublicKey(string methods, string domain, string selector,
            CancellationToken cancellationToken = default) => GetPublicKey(record);

        public override Task<Org.BouncyCastle.Crypto.AsymmetricKeyParameter> LocatePublicKeyAsync(string methods, string domain, string selector,
            CancellationToken cancellationToken = default) => Task.FromResult(GetPublicKey(record));
    }
}
