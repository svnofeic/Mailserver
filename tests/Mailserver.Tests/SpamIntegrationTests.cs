using System.Net;
using MailKit.Net.Smtp;
using MailKit.Security;
using MailKit;
using MailKit.Net.Imap;
using Mailserver.AntiSpam.Dns;
using Mailserver.Core.Rules;
using Mailserver.Core.SpamLogging;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using MimeKit.Cryptography;

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

    [Fact]
    public async Task Logs_checks_delivery_and_user_feedback()
    {
        _dns.Ptr.Clear();
        await SendAsync("ceo@spoofer.test", "Rechnung offen");
        var log = _server.Services.GetRequiredService<SpamLog>();

        var data = Assert.Single(log.Query(new SpamLogQuery(Stage: SpamLogStage.Data)));
        Assert.Equal(SpamLogAction.Spam, data.Action);
        Assert.Equal("127.0.0.1", data.ClientIp);
        Assert.Equal("ceo@spoofer.test", data.MailFrom);
        Assert.Equal("Rechnung offen", data.Subject);
        Assert.Contains("SPF_FAIL=3.5", data.Tests);
        Assert.StartsWith("fail:spoofer.test", data.Spf);

        var delivery = Assert.Single(log.Query(new SpamLogQuery(Session: data.Session, Stage: SpamLogStage.Delivery)));
        Assert.Equal("Junk", delivery.Folder);
        Assert.Equal("alice@example.test", delivery.Recipient);

        // The user disagrees and moves the message back to the inbox.
        using var imap = new ImapClient { ServerCertificateValidationCallback = (_, _, _, _) => true };
        await imap.ConnectAsync("127.0.0.1", _server.ImapsPort, SecureSocketOptions.SslOnConnect);
        await imap.AuthenticateAsync("alice@example.test", TestServer.Password);
        var junk = imap.GetFolder(SpecialFolder.Junk);
        await junk.OpenAsync(FolderAccess.ReadWrite);
        await junk.MoveToAsync(new UniqueId(1), imap.Inbox);

        var feedback = Assert.Single(log.Query(new SpamLogQuery(Stage: SpamLogStage.Feedback)));
        Assert.Equal(SpamLogAction.MarkedHam, feedback.Action);
        Assert.Equal(data.Score, feedback.Score);
        Assert.Equal(data.MessageId, feedback.MessageId);
        Assert.Contains("SPF_FAIL", feedback.Tests);
        Assert.Single(SpamLogReport.Build(log.Query(new SpamLogQuery()), DateTimeOffset.MinValue, 5).FalsePositives);
    }

    [Fact]
    public async Task Logs_rejections_and_rule_discards()
    {
        await Assert.ThrowsAsync<SmtpCommandException>(() => SendAsync("billing@strict.test", "Rechnung"));
        var rules = _server.Services.GetRequiredService<RuleStore>();
        var delete = RuleParser.Parse(["--if", "betreff", "enthält", "gewonnen", "--then", "löschen", "--name", "Gewinnspiel"]);
        rules.Add("*", delete.Name, delete.Conditions, delete.Action);
        await SendAsync("news@sender.test", "Sie haben gewonnen");

        var log = _server.Services.GetRequiredService<SpamLog>();
        var rejected = Assert.Single(log.Query(new SpamLogQuery(Action: SpamLogAction.Rejected)));
        Assert.Equal("DMARC p=reject", rejected.Detail);
        var discarded = Assert.Single(log.Query(new SpamLogQuery(Action: SpamLogAction.Discarded)));
        Assert.Equal("Gewinnspiel", discarded.Rules);
    }

    [Fact]
    public async Task Own_mail_coming_back_through_a_forwarding_is_accepted_only_with_our_signature()
    {
        // alice writes to an address at another provider, which forwards it to bob here: MAIL FROM is still alice@example.test.
        var keys = _server.Services.GetRequiredService<Core.Dkim.DkimKeyStore>();
        _dns.AddTxt("test._domainkey.example.test", keys.GetDnsRecord("example.test", "test"));
        var message = new MimeMessage { Subject = "Weitergeleitet", Body = new TextPart("plain") { Text = "Inhalt" } };
        message.From.Add(MailboxAddress.Parse("alice@example.test"));
        message.To.Add(MailboxAddress.Parse("alice@gmx.test"));
        using var raw = new MemoryStream();
        await message.WriteToAsync(raw);
        var signed = await _server.Services.GetRequiredService<Core.Routing.OutgoingMessagePreparer>().PrepareAsync(raw.ToArray(), CancellationToken.None);

        await SendRawAsync("alice@example.test", "bob@example.test", MimeMessage.Load(new MemoryStream(signed)));
        Assert.Contains("Weitergeleitet", await _server.ReadAsync(Assert.Single(_server.Inbox("bob"))));

        // Without our signature it is a forgery and still refused.
        var ex = await Assert.ThrowsAsync<SmtpCommandException>(() => SendRawAsync("alice@example.test", "bob@example.test", message));
        Assert.Contains("submission port", ex.Message);
        Assert.Single(_server.Inbox("bob"));
    }

    [Fact]
    public async Task Forwarded_mail_with_failing_spf_but_valid_signature_is_not_spam()
    {
        // The forwarder (127.0.0.1) is not in the SPF of the original sender, but its DKIM signature survived.
        _dns.AddTxt("forwarded.test", "v=spf1 ip4:198.51.100.1 -all");
        _dns.AddTxt("_dmarc.forwarded.test", "v=DMARC1; p=reject");
        var keys = _server.Services.GetRequiredService<Core.Dkim.DkimKeyStore>();
        keys.GenerateKey("forwarded.test", "s1");
        _dns.AddTxt("s1._domainkey.forwarded.test", keys.GetDnsRecord("forwarded.test", "s1"));
        var message = new MimeMessage { Subject = "Rechnung", Body = new TextPart("plain") { Text = "Inhalt" } };
        message.From.Add(MailboxAddress.Parse("billing@forwarded.test"));
        message.To.Add(MailboxAddress.Parse("alice@gmx.test"));
        message.Prepare(EncodingConstraint.SevenBit);
        new DkimSigner(keys.GetKeyPath("forwarded.test", "s1"), "forwarded.test", "s1")
        {
            HeaderCanonicalizationAlgorithm = DkimCanonicalizationAlgorithm.Relaxed,
            BodyCanonicalizationAlgorithm = DkimCanonicalizationAlgorithm.Relaxed,
        }.Sign(message, [HeaderId.From, HeaderId.Subject, HeaderId.To]);

        await SendRawAsync("billing@forwarded.test", "alice@example.test", message);

        var content = await _server.ReadAsync(Assert.Single(_server.Inbox("alice")));
        Assert.Contains("SPF_FAIL_DKIM_PASS", content);
        Assert.Contains("dmarc=pass", content);
    }

    private async Task SendRawAsync(string mailFrom, string recipient, MimeMessage message)
    {
        using var client = new SmtpClient { LocalDomain = "mx.sender.test" };
        await client.ConnectAsync("127.0.0.1", _server.InboundPort, SecureSocketOptions.None);
        await client.SendAsync(message, MailboxAddress.Parse(mailFrom), [MailboxAddress.Parse(recipient)]);
        await client.DisconnectAsync(true);
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

        var log = (SpamLog)_server.Services.GetService(typeof(SpamLog))!;
        var deferred = Assert.Single(log.Query(new SpamLogQuery(Action: SpamLogAction.Deferred)));
        Assert.Equal("alice@example.test", deferred.Recipient);
        Assert.Equal("greylisted", deferred.Detail);
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
