using System.Net;
using Mailserver.AntiSpam.Diagnostics;
using Mailserver.AntiSpam.Dns;
using Mailserver.Core;
using Mailserver.Smtp;
using Microsoft.Extensions.DependencyInjection;

namespace Mailserver.Tests;

/// <summary>Weekly status report to the administrators and the status address for outside monitoring.</summary>
public sealed class StatusReportTests : IAsyncLifetime
{
    private readonly FakeDns _dns = new();
    private TestServer _server = null!;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync(new Dictionary<string, string?> { ["Mailserver:Reports:StatusKey"] = "geheim" },
            services => services.AddSingleton<IDnsResolver>(_dns));
        var diagnostics = _server.Services.GetRequiredService<ServerDiagnostics>();
        diagnostics.Connect = (_, _, _) => Task.FromResult<string?>(null);
        diagnostics.LatestRelease = _ => Task.FromResult<string?>(null);
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Theory]
    // Monday 08:00 is the slot; Monday 2026-10-12.
    [InlineData("2026-10-12T08:30:00+02:00", null, true)]
    [InlineData("2026-10-12T07:59:00+02:00", null, false)]
    [InlineData("2026-10-14T09:00:00+02:00", null, false)] // installed mid-week: wait for next Monday
    [InlineData("2026-10-14T09:00:00+02:00", "2026-10-05T08:10:00+02:00", true)] // missed (server was off) – sent late
    [InlineData("2026-10-14T09:00:00+02:00", "2026-10-12T08:10:00+02:00", false)]
    [InlineData("2026-10-19T08:00:00+02:00", "2026-10-12T08:10:00+02:00", true)]
    public void Weekly_report_is_due_once_per_week(string now, string? lastSent, bool due) =>
        Assert.Equal(due, WeeklyReportService.IsDue(DateTimeOffset.Parse(now), new ReportOptions(),
            lastSent is null ? null : DateTimeOffset.Parse(lastSent)));

    [Fact]
    public void Disabled_report_is_never_due() =>
        Assert.False(WeeklyReportService.IsDue(DateTimeOffset.Parse("2026-10-12T09:00:00+02:00"), new ReportOptions { WeeklyEnabled = false }, null));

    [Fact]
    public async Task Report_lists_problems_and_traffic_and_goes_to_the_admins()
    {
        _server.HostAccounts.SetAdmin(EmailAddress.Parse("alice@example.test"), true);
        // No DNS records: the diagnosis finds missing MX/SPF/DKIM/DMARC.
        var weekly = await _server.Services.GetRequiredService<StatusReport>().BuildWeeklyAsync();
        Assert.True(weekly.Problems > 0);
        Assert.Contains("Punkt(e) zu prüfen", weekly.Subject);
        Assert.Contains("Mailverkehr der letzten 7 Tage", weekly.Text);
        Assert.Contains("Warteschlange: leer", weekly.Text);
        Assert.Contains("alice@example.test", weekly.Text);

        await _server.Services.GetRequiredService<WeeklyReportService>().SendAsync(CancellationToken.None);
        await TestServer.WaitUntilAsync(() => _server.Inbox("alice").Count == 1, "weekly report");
        Assert.Contains("Wochenbericht", await _server.ReadAsync(_server.Inbox("alice").Single()));
        Assert.Empty(_server.Inbox("bob"));

        using var web = new WebClient(_server.WebPort);
        await web.LoginAsync("alice@example.test", TestServer.Password);
        await web.GetAsync("/Admin/Report");
        Assert.Contains("/status?key=geheim", web.LastPage);
        Assert.DoesNotContain("noch nie", web.LastPage);
        await web.PostAsync("/Admin/Report", "/Admin/Report?handler=Preview");
        Assert.Contains("Mailverkehr der letzten 7 Tage", web.LastPage);
    }

    [Fact]
    public async Task Status_address_answers_ok_or_failure()
    {
        using var web = new WebClient(_server.WebPort);
        var response = await web.GetAsync("/status");
        // The test server has its certificate; the queue is empty, there is space.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("OK", web.LastPage);

        // A message stuck in the queue for a day is a failure.
        var queue = _server.Services.GetRequiredService<Mailserver.Core.Queue.OutboundQueue>();
        await queue.EnqueueAsync("Subject: x\r\n\r\nx"u8.ToArray(), "alice@example.test", [EmailAddress.Parse("x@remote.invalid")]);
        var database = _server.Services.GetRequiredService<Mailserver.Core.Data.Database>();
        using (var connection = database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE queue SET created_utc = '2020-01-01T00:00:00.0000000Z'";
            command.ExecuteNonQuery();
        }

        response = await web.GetAsync("/status");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("STÖRUNG", web.LastPage);
        await web.GetAsync("/status?key=falsch");
        Assert.Equal("STÖRUNG", web.LastPage);
        await web.GetAsync("/status?key=geheim");
        Assert.StartsWith("STÖRUNG: Warteschlange hängt", web.LastPage);
    }
}
