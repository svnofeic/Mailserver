using System.Net;
using System.Net.Sockets;
using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using Mailserver.Core;
using Mailserver.Core.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Mailserver.Tests;

public sealed class IpRuleTests : IDisposable
{
    private readonly TestData _data = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero));
    private readonly IpRules _rules;

    public IpRuleTests() => _rules = new IpRules(_data.Database, _time);

    public void Dispose() => _data.Dispose();

    private static IPAddress Ip(string value) => IPAddress.Parse(value);

    [Fact]
    public void Blocks_addresses_and_networks_until_they_expire()
    {
        _rules.Add("198.51.100.0/24", IpRuleKind.Block, "Bots", TimeSpan.FromDays(7));
        _rules.Add("2001:db8::1", IpRuleKind.Block, null);

        Assert.True(_rules.IsBlocked(Ip("198.51.100.77")));
        Assert.True(_rules.IsBlocked(Ip("::ffff:198.51.100.77")));
        Assert.True(_rules.IsBlocked(Ip("2001:db8::1")));
        Assert.False(_rules.IsBlocked(Ip("198.51.101.1")));

        _time.Now = _time.Now.AddDays(8);
        Assert.False(_rules.IsBlocked(Ip("198.51.100.77")));
        Assert.Equal(["2001:db8::1"], _rules.List().Select(r => r.NetworkText));
    }

    [Fact]
    public void Allow_wins_over_block()
    {
        _rules.Add("203.0.113.0/24", IpRuleKind.Block, null);
        _rules.Add("203.0.113.5", IpRuleKind.Allow, "Büro");

        Assert.False(_rules.IsBlocked(Ip("203.0.113.5")));
        Assert.True(_rules.IsAllowed(Ip("203.0.113.5")));
        Assert.True(_rules.IsBlocked(Ip("203.0.113.6")));

        Assert.Equal(2, _rules.Remove("203.0.113.0/24") + _rules.Remove("203.0.113.5"));
        Assert.Empty(_rules.List());
    }

    [Fact]
    public void Rejects_invalid_input_with_a_readable_message()
    {
        var ex = Assert.Throws<FormatException>(() => _rules.Add("kaputt", IpRuleKind.Block, null));
        Assert.Contains("keine IP-Adresse", ex.Message);
    }

    [Fact]
    public void Repeated_lockouts_block_the_address_for_days()
    {
        var options = new MailserverOptions();
        options.Security.MaxAuthFailuresPerIp = 2;
        options.Security.AutoBlockAfterLockouts = 3;
        var throttle = new AuthThrottle(Options.Create(options), _time, _rules);
        var bot = Ip("192.0.2.66");

        for (var lockout = 1; lockout <= 3; lockout++)
        {
            Assert.False(throttle.RecordFailure(bot));
            Assert.True(throttle.RecordFailure(bot));
            Assert.True(throttle.IsLockedOut(bot));
            Assert.Equal(lockout == 3, throttle.IsBlocked(bot));
            _time.Now = _time.Now.AddMinutes(31);
        }

        var rule = Assert.Single(_rules.List());
        Assert.Equal(_time.Now.AddMinutes(-31) + TimeSpan.FromDays(7), rule.Expires);
        Assert.Contains("automatisch", rule.Comment);
    }

    [Fact]
    public void Allowed_addresses_are_never_locked_out_and_lockouts_can_be_released()
    {
        var options = new MailserverOptions();
        options.Security.MaxAuthFailuresPerIp = 2;
        var throttle = new AuthThrottle(Options.Create(options), _time, _rules);
        _rules.Add("203.0.113.5", IpRuleKind.Allow, null);

        for (var i = 0; i < 10; i++)
        {
            Assert.False(throttle.RecordFailure(Ip("203.0.113.5")));
        }

        throttle.RecordFailure(Ip("192.0.2.1"));
        throttle.RecordFailure(Ip("192.0.2.1"));
        Assert.Equal("192.0.2.1", Assert.Single(throttle.ListLockouts()).Address.ToString());
        Assert.True(throttle.Release(Ip("192.0.2.1")));
        Assert.False(throttle.IsLockedOut(Ip("192.0.2.1")));
    }
}

/// <summary>A blocked address gets no SMTP, IMAP or web access.</summary>
public sealed class IpBlockIntegrationTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private IpRules Rules => _server.Services.GetRequiredService<IpRules>();

    [Fact]
    public async Task Blocked_address_is_refused_everywhere()
    {
        Rules.Add("127.0.0.1", IpRuleKind.Block, "Test");

        var message = new MimeMessage { Subject = "Hallo", Body = new TextPart("plain") { Text = "x" } };
        message.From.Add(MailboxAddress.Parse("sender@remote.test"));
        message.To.Add(MailboxAddress.Parse("alice@example.test"));
        using (var client = new SmtpClient { ServerCertificateValidationCallback = (_, _, _, _) => true })
        {
            await client.ConnectAsync("127.0.0.1", _server.InboundPort, SecureSocketOptions.None);
            var ex = await Assert.ThrowsAsync<SmtpCommandException>(() => client.SendAsync(message));
            Assert.Equal(554, (int)ex.StatusCode);
        }

        using (var client = new SmtpClient { ServerCertificateValidationCallback = (_, _, _, _) => true })
        {
            await client.ConnectAsync("127.0.0.1", _server.SubmissionPort, SecureSocketOptions.StartTls);
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.AuthenticateAsync("alice@example.test", TestServer.Password));
            Assert.Contains("Access denied", ex.Message);
        }

        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync("127.0.0.1", _server.ImapPort);
            var buffer = new byte[100];
            var read = await tcp.GetStream().ReadAsync(buffer);
            Assert.StartsWith("* BYE Access denied", Encoding.ASCII.GetString(buffer, 0, read));
        }

        using var web = new WebClient(_server.WebPort);
        var response = await web.GetAsync("/Login");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_server.Inbox("alice"));
    }

    [Fact]
    public async Task Admin_manages_rules_but_cannot_lock_themselves_out()
    {
        var admin = _server.HostAccounts.AddAccount(EmailAddress.Parse("chef@example.test"), TestServer.Password);
        _server.HostAccounts.SetAdmin(admin.Address, true);
        using var web = new WebClient(_server.WebPort);
        await web.LoginAsync("chef@example.test", TestServer.Password);

        await web.PostAsync("/Admin/IpBlocks", "/Admin/IpBlocks?handler=Add", ("network", "127.0.0.0/8"), ("kind", "Block"), ("comment", ""), ("days", "0"));
        Assert.Contains("enthält die eigene Adresse", web.LastPage);
        Assert.Empty(Rules.List());

        await web.PostAsync("/Admin/IpBlocks", "/Admin/IpBlocks?handler=Add", ("network", "198.51.100.0/24"), ("kind", "Block"), ("comment", "Bots"), ("days", "7"));
        Assert.Contains("198.51.100.0/24 ist gesperrt bis", web.LastPage);
        Assert.Contains("Bots", web.LastPage);

        await web.PostAsync("/Admin/IpBlocks", "/Admin/IpBlocks?handler=Add", ("network", "kaputt"), ("kind", "Allow"), ("comment", ""), ("days", "0"));
        Assert.Contains("keine IP-Adresse", web.LastPage);

        var rule = Assert.Single(Rules.List());
        await web.PostAsync("/Admin/IpBlocks", "/Admin/IpBlocks?handler=Remove", ("id", rule.Id.ToString()));
        Assert.Contains("Regel entfernt.", web.LastPage);
        Assert.Empty(Rules.List());
    }

    [Fact]
    public async Task Admin_sees_and_releases_lockouts()
    {
        var throttle = _server.Services.GetRequiredService<AuthThrottle>();
        var bot = IPAddress.Parse("192.0.2.99");
        for (var i = 0; i < 10; i++)
        {
            throttle.RecordFailure(bot);
        }

        var admin = _server.HostAccounts.AddAccount(EmailAddress.Parse("chef@example.test"), TestServer.Password);
        _server.HostAccounts.SetAdmin(admin.Address, true);
        using var web = new WebClient(_server.WebPort);
        await web.LoginAsync("chef@example.test", TestServer.Password);

        await web.GetAsync("/Admin/IpBlocks");
        Assert.Contains("192.0.2.99", web.LastPage);

        await web.PostAsync("/Admin/IpBlocks", "/Admin/IpBlocks?handler=Release", ("ip", "192.0.2.99"));
        Assert.Contains("Sperre für 192.0.2.99 aufgehoben.", web.LastPage);
        Assert.False(throttle.IsLockedOut(bot));
    }
}
