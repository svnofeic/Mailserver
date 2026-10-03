using System.Net;
using MailKit.Net.Smtp;
using MailKit.Security;
using Mailserver.AntiSpam.Dns;
using Mailserver.Core.Rules;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace Mailserver.Tests;

/// <summary>End-to-end: SMTP from 127.0.0.1 with loopback trust switched off and a fake DNS.</summary>
public sealed class SpamIntegrationTests : IAsyncLifetime
{
    private readonly FakeDns _dns = new();
    private TestServer _server = null!;

    public async Task InitializeAsync()
    {
        _dns.Ptr["127.0.0.1"] = "mx.sender.test";
        _dns.AddTxt("sender.test", "v=spf1 ip4:127.0.0.1 -all");              // legitimate sender
        _dns.AddTxt("spoofer.test", "v=spf1 ip4:198.51.100.1 -all");           // SPF fails from 127.0.0.1
        _dns.AddTxt("_dmarc.strict.test", "v=DMARC1; p=reject");                // DMARC reject, SPF does not cover us
        _dns.AddTxt("strict.test", "v=spf1 ip4:198.51.100.1 -all");
        _dns.AddA("1.0.0.127.listed.test", "127.0.0.2");                        // blocklist hit for 127.0.0.1

        _server = await TestServer.StartAsync(new Dictionary<string, string?>
        {
            ["Mailserver:Spam:TrustLoopback"] = "false",
            ["Mailserver:Spam:Greylisting:Enabled"] = "false",
            ["Mailserver:Spam:DnsBlocklists:0:Zone"] = "unlisted.test",
        }, services => services.AddSingleton<IDnsResolver>(_dns));
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task Clean_mail_gets_authentication_results_and_lands_in_inbox()
    {
        await SendAsync("news@sender.test", "Monatsbericht");

        var message = Assert.Single(_server.Inbox("alice"));
        var content = await _server.ReadAsync(message);
        Assert.Contains("Authentication-Results: mail.example.test;", content);
        Assert.Contains("spf=pass smtp.mailfrom=news@sender.test", content);
        Assert.Contains("X-Spam-Status: No", content);
    }

    [Fact]
    public async Task Spf_fail_without_ptr_goes_to_junk()
    {
        _dns.Ptr.Clear();
        await SendAsync("ceo@spoofer.test", "Dringende Überweisung");

        Assert.Empty(_server.Inbox("alice"));
        var junk = Messages("alice", "Junk");
        var content = await _server.ReadAsync(Assert.Single(junk));
        Assert.Contains("X-Spam-Flag: YES", content);
        Assert.Contains("SPF_FAIL", content);
        Assert.Contains("NO_REVERSE_DNS", content);
    }

    [Fact]
    public async Task Dmarc_reject_policy_refuses_the_message()
    {
        var ex = await Assert.ThrowsAsync<SmtpCommandException>(() => SendAsync("billing@strict.test", "Rechnung"));
        Assert.Contains("DMARC", ex.Message);
        Assert.Empty(_server.Inbox("alice"));
    }

    [Fact]
    public async Task Forged_spam_headers_are_removed()
    {
        await SendAsync("news@sender.test", "Hallo", extraHeaders: ("X-Spam-Status", "No, score=-100"));

        var content = await _server.ReadAsync(Assert.Single(_server.Inbox("alice")));
        Assert.DoesNotContain("score=-100", content);
    }

    [Fact]
    public async Task User_rules_move_or_delete_by_subject()
    {
        var rules = _server.Services.GetRequiredService<RuleStore>();
        var junk = RuleParser.Parse(["--if", "betreff", "enthält", "exklusives Angebot", "--then", "spam"]);
        rules.Add("alice@example.test", junk.Name, junk.Conditions, junk.Action);
        var delete = RuleParser.Parse(["--if", "betreff", "enthält", "Sie haben gewonnen", "--then", "löschen"]);
        rules.Add("example.test", delete.Name, delete.Conditions, delete.Action);

        await SendAsync("news@sender.test", "Ihr exklusives Angebot");
        await SendAsync("news@sender.test", "Sie haben gewonnen!");
        await SendAsync("news@sender.test", "Normale Nachricht");

        Assert.Equal("Normale Nachricht", Subject(await _server.ReadAsync(Assert.Single(_server.Inbox("alice")))));
        Assert.Equal("Ihr exklusives Angebot", Subject(await _server.ReadAsync(Assert.Single(Messages("alice", "Junk")))));
        // The deleted message is stored nowhere: alice has exactly the two messages above.
        Assert.Equal(2, _server.HostMailboxes.ListFolders(_server.User("alice").Id).Sum(f => _server.HostMailboxes.ListMessages(f.Id).Count));
    }

    [Fact]
    public async Task Allow_rule_rescues_mail_the_filter_marks_as_spam()
    {
        _dns.Ptr.Clear();
        var rules = _server.Services.GetRequiredService<RuleStore>();
        var allow = RuleParser.Parse(["--if", "von", "endet", "@spoofer.test", "--then", "kein-spam"]);
        rules.Add("alice@example.test", allow.Name, allow.Conditions, allow.Action);

        await SendAsync("ceo@spoofer.test", "Wichtig");

        Assert.Single(_server.Inbox("alice"));
        Assert.Empty(Messages("alice", "Junk"));
    }

    private List<Core.Storage.StoredMessage> Messages(string user, string folder) =>
        _server.HostMailboxes.ListMessages(_server.HostMailboxes.GetFolder(_server.User(user).Id, folder)!.Id).ToList();

    private static string Subject(string raw) => MimeMessage.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(raw))).Subject;

    private async Task SendAsync(string from, string subject, (string Name, string Value)? extraHeaders = null)
    {
        var message = new MimeMessage { Subject = subject, Body = new TextPart("plain") { Text = "Inhalt" } };
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse("alice@example.test"));
        if (extraHeaders is { } header)
        {
            message.Headers.Add(header.Name, header.Value);
        }

        using var client = new SmtpClient { LocalDomain = "mx.sender.test" };
        await client.ConnectAsync("127.0.0.1", _server.InboundPort, SecureSocketOptions.None);
        await client.SendAsync(message);
        await client.DisconnectAsync(true);
    }
}

/// <summary>Greylisting through a real SMTP session: first attempt deferred, retry accepted.</summary>
public sealed class GreylistingIntegrationTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() =>
        _server = await TestServer.StartAsync(new Dictionary<string, string?>
        {
            ["Mailserver:Spam:TrustLoopback"] = "false",
            ["Mailserver:Spam:Greylisting:Delay"] = "00:00:00",
            ["Mailserver:Spam:DnsBlocklistsEnabled"] = "false",
        }, services => services.AddSingleton<IDnsResolver>(new FakeDns()));

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task Defers_first_attempt()
    {
        var ex = await Assert.ThrowsAsync<SmtpCommandException>(SendAsync);
        Assert.Contains("Greylisted", ex.Message);
        Assert.Equal(451, (int)ex.StatusCode);

        await SendAsync();
        Assert.Single(_server.Inbox("alice"));
    }

    private async Task SendAsync()
    {
        var message = new MimeMessage { Subject = "Hallo", Body = new TextPart("plain") { Text = "x" } };
        message.From.Add(MailboxAddress.Parse("someone@unknown.test"));
        message.To.Add(MailboxAddress.Parse("alice@example.test"));
        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", _server.InboundPort, SecureSocketOptions.None);
        await client.SendAsync(message);
        await client.DisconnectAsync(true);
    }
}
