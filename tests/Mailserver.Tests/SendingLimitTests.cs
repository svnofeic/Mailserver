using MailKit.Net.Smtp;
using MailKit.Security;
using Mailserver.Core;
using Mailserver.Core.Accounts;
using Mailserver.Core.Security;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace Mailserver.Tests;

/// <summary>Sending limits per mailbox: a stolen password must not turn the server into a spam source.</summary>
public sealed class SendingLimitTests : IAsyncLifetime
{
    private const string Admin = "chef@example.test";
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private MailboxSettingsStore Settings => _server.Services.GetRequiredService<MailboxSettingsStore>();

    private static async Task<TestServer> StartAsync(params (string Key, string Value)[] extra)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Mailserver:Security:Sending:MaxRecipientsPerMessage"] = "3",
            ["Mailserver:Security:Sending:MaxRecipientsPerHour"] = "4",
        };
        foreach (var (key, value) in extra)
        {
            settings[key] = value;
        }

        var server = await TestServer.StartAsync(settings);
        var admin = server.HostAccounts.AddAccount(EmailAddress.Parse(Admin), TestServer.Password);
        server.HostAccounts.SetAdmin(admin.Address, true);
        return server;
    }

    [Fact]
    public async Task Too_many_recipients_in_one_message_are_refused()
    {
        var ex = await Assert.ThrowsAsync<SmtpCommandException>(() => SubmitAsync("a@remote.test", "b@remote.test", "c@remote.test", "d@remote.test"));

        Assert.Equal(550, (int)ex.StatusCode);
        Assert.Contains("Hoechstens 3 externe Empfaenger", ex.Message);
        Assert.False(Settings.Get(_server.User("alice").Id).Sending.IsBlocked);
    }

    [Fact]
    public async Task Local_recipients_do_not_count()
    {
        for (var i = 0; i < 3; i++)
        {
            await SubmitAsync("bob@example.test", Admin);
        }

        Assert.Equal((0, 0), _server.Services.GetRequiredService<SendingLimiter>().Usage(_server.User("alice").Id));
    }

    [Fact]
    public async Task Exceeding_the_hourly_limit_locks_the_mailbox_and_tells_the_admins()
    {
        await SubmitAsync("a@remote.test", "b@remote.test", "c@remote.test");

        var ex = await Assert.ThrowsAsync<SmtpCommandException>(() => SubmitAsync("d@remote.test", "e@remote.test"));
        Assert.Equal(550, (int)ex.StatusCode);
        Assert.Contains("gesperrt", ex.Message);

        var state = Settings.Get(_server.User("alice").Id).Sending;
        Assert.True(state.IsBlocked);
        Assert.Contains("mehr als 4 externe Empfänger in einer Stunde", state.BlockedReason);

        var notice = MimeMessage.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(await WaitForAdminMailAsync())));
        Assert.Equal("[Mailserver] Versand für alice@example.test gesperrt", notice.Subject);
        Assert.Contains("Passwort ändern", notice.TextBody);

        // Even mail to colleagues is refused now; receiving still works.
        await Assert.ThrowsAsync<SmtpCommandException>(() => SubmitAsync("bob@example.test"));
        Assert.Empty(_server.Inbox("bob"));
    }

    [Fact]
    public async Task Admin_releases_the_mailbox_and_sets_own_limits()
    {
        Settings.BlockSending(_server.User("alice").Id, "Test");
        using var web = new WebClient(_server.WebPort);
        await web.LoginAsync(Admin, TestServer.Password);

        await web.GetAsync("/Admin/Mailboxes");
        Assert.Contains("Versand gesperrt", web.LastPage);

        await web.PostAsync("/Admin/Mailboxes/Edit?address=alice%40example.test", "/Admin/Mailboxes/Edit?handler=Unblock&address=alice%40example.test");
        Assert.Contains("Versand für alice@example.test wieder freigegeben.", web.LastPage);
        Assert.False(Settings.Get(_server.User("alice").Id).Sending.IsBlocked);

        await web.PostAsync("/Admin/Mailboxes/Edit?address=alice%40example.test", "/Admin/Mailboxes/Edit?handler=SendingLimits&address=alice%40example.test",
            ("perHour", "0"), ("perDay", "50"));
        Assert.Contains("Versandlimits gespeichert.", web.LastPage);
        var state = Settings.Get(_server.User("alice").Id).Sending;
        Assert.Equal((0, 50), (state.PerHour, state.PerDay));

        // 0 = unlimited for this mailbox, only the per-message limit still applies.
        for (var i = 0; i < 3; i++)
        {
            await SubmitAsync("a@remote.test", "b@remote.test", "c@remote.test");
        }
    }

    [Fact]
    public async Task Without_locking_the_sender_is_asked_to_try_later()
    {
        await _server.DisposeAsync();
        _server = await StartAsync(("Mailserver:Security:Sending:BlockOnLimit", "false"));

        await SubmitAsync("a@remote.test", "b@remote.test", "c@remote.test");
        var ex = await Assert.ThrowsAsync<SmtpCommandException>(() => SubmitAsync("d@remote.test", "e@remote.test"));

        Assert.Equal(451, (int)ex.StatusCode);
        Assert.False(Settings.Get(_server.User("alice").Id).Sending.IsBlocked);
    }

    [Fact]
    public async Task Webmail_shows_why_a_message_was_not_sent()
    {
        Settings.BlockSending(_server.User("alice").Id, "Test");
        using var web = new WebClient(_server.WebPort);
        await web.LoginAsync("alice@example.test", TestServer.Password);

        await web.PostMultipartAsync("/Mail/Compose", "/Mail/Compose?handler=Send",
            [("Form.From", "alice@example.test"), ("Form.To", "x@remote.test"), ("Form.Subject", "Hallo"), ("Form.Body", "x")]);

        Assert.Contains("Nicht gesendet", web.LastPage);
        Assert.Contains("ist gesperrt", web.LastPage);
    }

    private async Task<string> WaitForAdminMailAsync()
    {
        await TestServer.WaitUntilAsync(() => _server.Inbox("chef").Count > 0, "admin notification");
        return await _server.ReadAsync(_server.Inbox("chef").Single());
    }

    private async Task SubmitAsync(params string[] recipients)
    {
        var message = new MimeMessage { Subject = "Test", Body = new TextPart("plain") { Text = "Hallo" } };
        message.From.Add(MailboxAddress.Parse("alice@example.test"));
        foreach (var recipient in recipients)
        {
            message.To.Add(MailboxAddress.Parse(recipient));
        }

        using var client = new SmtpClient { ServerCertificateValidationCallback = (_, _, _, _) => true };
        await client.ConnectAsync("127.0.0.1", _server.SubmissionPort, SecureSocketOptions.StartTls);
        await client.AuthenticateAsync("alice@example.test", TestServer.Password);
        await client.SendAsync(message);
        await client.DisconnectAsync(true);
    }
}
