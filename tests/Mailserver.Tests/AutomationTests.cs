using MailKit.Net.Smtp;
using MailKit.Security;
using Mailserver.Core;
using Mailserver.Core.Accounts;
using Mailserver.Core.Rules;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace Mailserver.Tests;

/// <summary>Mailbox forwarding and out-of-office replies on real deliveries.</summary>
public sealed class AutomationTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private MailboxSettingsStore Settings => _server.Services.GetRequiredService<MailboxSettingsStore>();

    [Fact]
    public async Task Forwards_and_keeps_a_copy()
    {
        Settings.SetForwarding(_server.User("alice"), new Forwarding([EmailAddress.Parse("handy@remote.test")], KeepCopy: true));

        await SendAsync("sender@remote.test", "alice@example.test", "Weiterleiten bitte");

        await TestServer.WaitUntilAsync(() => !_server.Remote.Messages.IsEmpty, "forwarded copy");
        var forwarded = Assert.Single(_server.Remote.Messages);
        Assert.Equal("alice@example.test", forwarded.From); // envelope sender rewritten, so SPF passes at the destination
        Assert.Equal(["handy@remote.test"], forwarded.To);
        Assert.Contains("From: sender@remote.test", forwarded.Content);
        Assert.Single(_server.Inbox("alice"));
    }

    [Fact]
    public async Task Forwards_without_copy()
    {
        Settings.SetForwarding(_server.User("alice"), new Forwarding([EmailAddress.Parse("handy@remote.test")], KeepCopy: false));

        await SendAsync("sender@remote.test", "alice@example.test", "Nur weiterleiten");

        await TestServer.WaitUntilAsync(() => !_server.Remote.Messages.IsEmpty, "forwarded copy");
        Assert.Empty(_server.Inbox("alice"));
    }

    [Fact]
    public async Task Forwards_to_a_local_mailbox_without_looping()
    {
        // Two mailboxes forwarding to each other: each message arrives once in each, nothing bounces back and forth.
        Settings.SetForwarding(_server.User("alice"), new Forwarding([EmailAddress.Parse("bob@example.test")], KeepCopy: true));
        Settings.SetForwarding(_server.User("bob"), new Forwarding([EmailAddress.Parse("alice@example.test")], KeepCopy: true));

        await SendAsync("sender@remote.test", "alice@example.test", "Kreis");

        Assert.Single(_server.Inbox("alice"));
        Assert.Single(_server.Inbox("bob"));
        await Task.Delay(1500);
        Assert.Empty(_server.Remote.Messages);
    }

    [Fact]
    public async Task Does_not_forward_spam()
    {
        Settings.SetForwarding(_server.User("alice"), new Forwarding([EmailAddress.Parse("handy@remote.test")], KeepCopy: false));
        _server.Services.GetRequiredService<RuleStore>().Add("alice@example.test", "Spam",
            [new RuleCondition(RuleField.Subject, RuleOperator.Contains, "Gewinnspiel")], RuleAction.Junk);

        await SendAsync("sender@remote.test", "alice@example.test", "Ihr Gewinnspiel");

        // Spam stays in Junk even without "keep copy" and is not passed on.
        var junk = _server.HostMailboxes.ListMessages(_server.HostMailboxes.GetFolder(_server.User("alice").Id, "Junk")!.Id);
        Assert.Single(junk);
        await Task.Delay(1500);
        Assert.Empty(_server.Remote.Messages);
    }

    [Fact]
    public async Task Sends_an_out_of_office_reply_once_per_sender()
    {
        Settings.SetAutoReply(_server.User("alice").Id, new AutoReply(true, "", "Ich bin bis Montag im Urlaub.", null, null, 7));

        var original = await SendAsync("sender@remote.test", "alice@example.test", "Frage zum Angebot");

        await TestServer.WaitUntilAsync(() => !_server.Remote.Messages.IsEmpty, "out-of-office reply");
        var reply = Assert.Single(_server.Remote.Messages);
        Assert.Equal("", reply.From); // null envelope sender: no loops, no bounces
        Assert.Equal(["sender@remote.test"], reply.To);
        Assert.Contains("Subject: Automatische Antwort: Frage zum Angebot", reply.Content);
        Assert.Contains("Auto-Submitted: auto-replied", reply.Content);
        Assert.Contains($"In-Reply-To: <{original.MessageId}>", reply.Content);
        Assert.Contains("DKIM-Signature:", reply.Content);
        Assert.Contains("Urlaub", reply.Content);
        Assert.Single(_server.Inbox("alice"));

        await SendAsync("sender@remote.test", "alice@example.test", "Noch eine Frage");
        await Task.Delay(2000);
        Assert.Single(_server.Remote.Messages);
        Assert.Equal(2, _server.Inbox("alice").Count);
    }

    [Theory]
    [InlineData("List-Id", "<news.remote.test>")]
    [InlineData("Precedence", "bulk")]
    [InlineData("Auto-Submitted", "auto-replied")]
    public async Task Does_not_answer_automatic_mail(string header, string value)
    {
        Settings.SetAutoReply(_server.User("alice").Id, new AutoReply(true, "Abwesend", "Bin weg.", null, null, 7));

        await SendAsync("sender@remote.test", "alice@example.test", "Newsletter", (header, value));

        await Task.Delay(1500);
        Assert.Empty(_server.Remote.Messages);
    }

    [Fact]
    public async Task Does_not_answer_when_only_in_bcc_or_outside_the_period()
    {
        var alice = _server.User("alice");
        Settings.SetAutoReply(alice.Id, new AutoReply(true, "Abwesend", "Bin weg.", null, null, 7));
        await SendAsync("sender@remote.test", "alice@example.test", "Massenmail", toHeader: "viele@remote.test");

        var tomorrow = DateOnly.FromDateTime(DateTime.Now).AddDays(1);
        Settings.SetAutoReply(alice.Id, new AutoReply(true, "Abwesend", "Bin weg.", tomorrow, null, 7));
        await SendAsync("other@remote.test", "alice@example.test", "Zu früh");

        await Task.Delay(1500);
        Assert.Empty(_server.Remote.Messages);
        Assert.Equal(2, _server.Inbox("alice").Count);
    }

    [Theory]
    [InlineData("alice@example.test", "an sich selbst")]
    [InlineData("kein-at-zeichen", "keine gültige")]
    [InlineData("gibtsnicht@example.test", "gibt es nicht")]
    public void Rejects_invalid_forwarding(string target, string error)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Settings.SetForwarding(_server.User("alice"), new Forwarding(MailboxSettingsStore.ParseTargets(target), true)));
        Assert.Contains(error, ex.Message);
    }

    [Fact]
    public void Rejects_invalid_out_of_office_reply()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        Assert.Contains("Text", Assert.Throws<ArgumentException>(() =>
            Settings.SetAutoReply(1, new AutoReply(true, "", " ", null, null, 7))).Message);
        Assert.Contains("Enddatum", Assert.Throws<ArgumentException>(() =>
            Settings.SetAutoReply(1, new AutoReply(true, "", "x", today, today.AddDays(-1), 7))).Message);
    }

    private async Task<MimeMessage> SendAsync(string from, string to, string subject, (string Name, string Value)? header = null,
        string? toHeader = null)
    {
        var message = new MimeMessage { Subject = subject, Body = new TextPart("plain") { Text = "Hallo" } };
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse(toHeader ?? to));
        if (header is { } h)
        {
            message.Headers.Add(h.Name, h.Value);
        }

        using var client = new SmtpClient { ServerCertificateValidationCallback = (_, _, _, _) => true };
        await client.ConnectAsync("127.0.0.1", _server.InboundPort, SecureSocketOptions.StartTls);
        await client.SendAsync(message, MailboxAddress.Parse(from), [MailboxAddress.Parse(to)]);
        await client.DisconnectAsync(true);
        return message;
    }
}

public sealed class AutomationWebTests : IAsyncLifetime
{
    private TestServer _server = null!;
    private WebClient _web = null!;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync();
        _web = new WebClient(_server.WebPort);
    }

    public async Task DisposeAsync()
    {
        _web.Dispose();
        await _server.DisposeAsync();
    }

    private MailboxSettingsStore Settings => _server.Services.GetRequiredService<MailboxSettingsStore>();

    [Fact]
    public async Task User_sets_forwarding_and_out_of_office_reply()
    {
        await _web.LoginAsync("alice@example.test", TestServer.Password);
        var until = DateOnly.FromDateTime(DateTime.Now).AddDays(7);

        await _web.PostAsync("/Account/Away", "/Account/Away",
            ("Form.ForwardTo", "handy@remote.test\nbob@example.test"), ("Form.KeepCopy", "false"),
            ("Form.AutoReplyEnabled", "true"), ("Form.Subject", "Im Urlaub"), ("Form.Body", "Bin weg.\nGrüße"),
            ("Form.Until", until.ToString("yyyy-MM-dd")), ("Form.IntervalDays", "3"));

        Assert.Contains("Gespeichert.", _web.LastPage);
        var settings = Settings.Get(_server.User("alice").Id);
        Assert.Equal(["handy@remote.test", "bob@example.test"], settings.Forwarding.Targets.Select(t => t.ToString()));
        Assert.False(settings.Forwarding.KeepCopy);
        Assert.Equal(new AutoReply(true, "Im Urlaub", "Bin weg.\nGrüße", null, until, 3), settings.AutoReply);

        // Every page reminds the user that mail is being forwarded and answered.
        await _web.GetAsync("/Mail");
        Assert.Contains("Weiterleitung an handy@remote.test, bob@example.test (ohne Kopie)", _web.LastPage);
        Assert.Contains($"Abwesenheitsnotiz aktiv bis {until:dd.MM.yyyy}", _web.LastPage);
    }

    [Fact]
    public async Task Invalid_input_keeps_what_was_typed()
    {
        await _web.LoginAsync("alice@example.test", TestServer.Password);

        await _web.PostAsync("/Account/Away", "/Account/Away", ("Form.ForwardTo", "kaputt"), ("Form.KeepCopy", "true"),
            ("Form.AutoReplyEnabled", "true"), ("Form.Body", "Mein langer Text"), ("Form.IntervalDays", "7"));

        Assert.Contains("„kaputt“ ist keine gültige E-Mail-Adresse.", _web.LastPage);
        Assert.Contains("Mein langer Text", _web.LastPage);
        Assert.Equal(MailboxSettings.Default, Settings.Get(_server.User("alice").Id));
    }

    [Fact]
    public async Task Admin_sets_forwarding_for_another_mailbox()
    {
        var admin = _server.HostAccounts.AddAccount(EmailAddress.Parse("chef@example.test"), TestServer.Password);
        _server.HostAccounts.SetAdmin(admin.Address, true);
        await _web.LoginAsync("chef@example.test", TestServer.Password);

        await _web.PostAsync("/Admin/Mailboxes/Edit?address=bob%40example.test", "/Admin/Mailboxes/Edit?handler=Automation&address=bob%40example.test",
            ("Form.ForwardTo", "info@example.test"), ("Form.KeepCopy", "true"), ("Form.KeepCopy", "false"), ("Form.IntervalDays", "7"));

        Assert.Contains("Weiterleitung und Abwesenheitsnotiz gespeichert.", _web.LastPage);
        Assert.Equal("info@example.test", Settings.Get(_server.User("bob").Id).Forwarding.Targets.Single().ToString());
        await _web.GetAsync("/Admin/Mailboxes");
        Assert.Contains(">Weiterleitung</span>", _web.LastPage);
    }
}
