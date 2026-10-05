using System.Net;
using Mailserver.Core;
using Mailserver.Core.SpamLogging;
using Mailserver.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Mailserver.Tests;

/// <summary>Design of the web interface: stylesheet, light/dark switch, dashboard figures and charts.</summary>
public sealed class DesignTests : IAsyncLifetime
{
    private TestServer _server = null!;
    private WebClient _web = null!;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync();
        var admin = _server.HostAccounts.AddAccount(EmailAddress.Parse("chef@example.test"), TestServer.Password);
        _server.HostAccounts.SetAdmin(admin.Address, true);
        _web = new WebClient(_server.WebPort);
        await _web.LoginAsync("chef@example.test", TestServer.Password);
    }

    public async Task DisposeAsync()
    {
        _web.Dispose();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task Stylesheet_is_served_and_allowed_by_the_content_security_policy()
    {
        var page = await _web.GetAsync("/Admin");
        Assert.Contains("style-src 'self'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("/assets/site.css?v=", _web.LastPage);

        var css = await _web.GetAsync("/assets/site.css");
        Assert.Equal("text/css", css.Content.Headers.ContentType!.MediaType);
        Assert.Contains("--accent", _web.LastPage);
    }

    [Fact]
    public async Task Dark_is_the_default_and_light_can_be_chosen()
    {
        await _web.GetAsync("/");
        Assert.Contains("data-theme=\"dark\"", _web.LastPage);

        var switched = await _web.GetAsync("/theme?mode=light&returnUrl=%2FMail%3Ffolder%3DINBOX");
        Assert.Equal(HttpStatusCode.Redirect, switched.StatusCode);
        Assert.Equal("/Mail?folder=INBOX", switched.Headers.Location!.OriginalString);
        await _web.GetAsync("/");
        Assert.Contains("data-theme=\"light\"", _web.LastPage);

        // Only addresses on this server: no redirect to another site.
        foreach (var target in new[] { "//evil.example", "https://evil.example", "/\\evil.example" })
        {
            var response = await _web.GetAsync("/theme?mode=dark&returnUrl=" + Uri.EscapeDataString(target));
            Assert.Equal("/", response.Headers.Location!.OriginalString);
        }
    }

    [Fact]
    public async Task Dashboard_shows_charts_of_the_last_14_days()
    {
        var log = _server.Services.GetRequiredService<SpamLog>();
        var now = DateTimeOffset.UtcNow;
        log.Write(new SpamLogEntry { Time = now.AddMinutes(-5), Session = "a", Stage = SpamLogStage.Data, Action = SpamLogAction.Spam, Recipient = "alice@example.test",
            HeaderFrom = "win@lotto.test" });
        log.Write(new SpamLogEntry { Time = now.AddMinutes(-4), Session = "b", Stage = SpamLogStage.Data, Action = SpamLogAction.Accepted, Recipient = "alice@example.test" });
        log.Write(new SpamLogEntry { Time = now.AddMinutes(-3), Stage = SpamLogStage.Outbound, Action = SpamLogAction.Sent, Recipient = "x@remote.test" });

        await _web.GetAsync("/Admin");

        Assert.Contains("Mailverkehr", _web.LastPage);
        Assert.Contains("<svg class=\"chart\"", _web.LastPage);
        Assert.Contains("class=\"chart donut\"", _web.LastPage);
        Assert.Contains("lotto.test", _web.LastPage); // top spam senders
        Assert.Contains("Gesendet an x@remote.test", _web.LastPage); // activity
    }
}

public sealed class DailyTrafficTests : IDisposable
{
    private readonly TestData _data = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public void Counts_per_day_for_the_charts()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var log = new SpamLog(_data.Database, Microsoft.Extensions.Options.Options.Create(new MailserverOptions()), time);
        var now = time.GetUtcNow();
        void Write(DateTimeOffset at, string stage, string action) => log.Write(new SpamLogEntry { Time = at, Stage = stage, Action = action });
        Write(now, SpamLogStage.Data, SpamLogAction.Accepted);
        Write(now, SpamLogStage.Data, SpamLogAction.Spam);
        Write(now, SpamLogStage.Connect, SpamLogAction.Rejected);
        Write(now, SpamLogStage.Outbound, SpamLogAction.Sent);
        Write(now, SpamLogStage.Outbound, SpamLogAction.Failed);
        Write(now, SpamLogStage.Auth, SpamLogAction.LoginFailed);
        Write(now, SpamLogStage.Delivery, SpamLogAction.Delivered); // not counted twice
        Write(now.AddDays(-3), SpamLogStage.Data, SpamLogAction.Accepted);
        Write(now.AddDays(-30), SpamLogStage.Data, SpamLogAction.Accepted); // too old

        var days = log.Daily(14);

        Assert.Equal(14, days.Count);
        Assert.Equal(DateOnly.FromDateTime(time.GetLocalNow().DateTime), days[^1].Day);
        Assert.Equal(new DailyTraffic(days[^1].Day, 2, 1, 1, 1, 1, 1), days[^1]);
        Assert.Equal(1, days[^4].Received);
        Assert.Equal(3, days.Sum(d => d.Received));
    }

    [Theory]
    [InlineData("Anna Weber", "AW")]
    [InlineData("news@verein.de", "NE")]
    [InlineData("max.mustermann@example.test", "MM")]
    [InlineData("\"Shop Müller\" ", "SM")]
    [InlineData("", "?")]
    public void Avatar_initials(string name, string initials) => Assert.Equal(initials, Avatars.Initials(name));

    [Fact]
    public void Charts_render_without_values_and_with_large_numbers()
    {
        Assert.Equal("", Charts.Sparkline([5]).ToString());
        Assert.Contains("<path", Charts.Sparkline([0, 0, 0]).ToString());
        Assert.Contains("<svg", Charts.Area(["1", "2"], new Charts.Series("x", [0, 0], 1)).ToString());
        Assert.Contains("gesamt", Charts.Donut([("a", 0, 1)], "0", "gesamt").ToString());
        Assert.Equal(["950", "1,2K", "15K", "1,1 Mio."], new long[] { 950, 1200, 15_400, 1_100_000 }.Select(Charts.Compact));
    }
}
