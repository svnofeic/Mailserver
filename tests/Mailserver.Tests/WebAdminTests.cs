using System.Net;
using Mailserver.Core;
using Mailserver.Core.Dkim;
using Mailserver.Core.SpamLogging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Mailserver.Tests;

public sealed class WebAdminTests : IAsyncLifetime
{
    private const string Admin = "chef@example.test";
    private TestServer _server = null!;
    private WebClient _web = null!;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync();
        var account = _server.HostAccounts.AddAccount(EmailAddress.Parse(Admin), TestServer.Password);
        _server.HostMailboxes.EnsureDefaultFolders(account.Id);
        _server.HostAccounts.SetAdmin(account.Address, true);
        _web = new WebClient(_server.WebPort);
        await _web.LoginAsync(Admin, TestServer.Password);
    }

    public async Task DisposeAsync()
    {
        _web.Dispose();
        await _server.DisposeAsync();
    }

    [Theory]
    [InlineData("/Admin", "Admin-Übersicht")]
    [InlineData("/Admin/Domains", "example.test")]
    [InlineData("/Admin/Domains/Details?name=example.test", "_domainkey.example.test")]
    [InlineData("/Admin/Mailboxes", "alice@example.test")]
    [InlineData("/Admin/Mailboxes/Edit?address=alice%40example.test", "Postfach alice@example.test")]
    [InlineData("/Admin/Aliases", "info@example.test")]
    [InlineData("/Admin/Rules", "Alle Regeln")]
    [InlineData("/Admin/Queue", "Warteschlange ist leer")]
    [InlineData("/Admin/Log", "Verlauf")]
    [InlineData("/Admin/Log/Stats", "Verteilung der Spam-Scores")]
    [InlineData("/Admin/Settings", "Spamfilter")]
    [InlineData("/Rules/Edit?scope=example.test", "Gilt für")]
    public async Task Admin_pages_render(string path, string expected)
    {
        var response = await _web.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(expected, _web.LastPage);
    }

    [Fact]
    public async Task Creates_domain_with_dkim_and_mailbox()
    {
        await _web.PostAsync("/Admin/Domains", "/Admin/Domains", ("name", "Neu-Firma.TEST"));
        Assert.True(_server.HostAccounts.IsLocalDomain("neu-firma.test"));
        Assert.Contains("v=DKIM1; k=rsa; p=", _web.LastPage);

        await _web.PostAsync("/Admin/Mailboxes", "/Admin/Mailboxes", ("localPart", "anna"), ("domain", "neu-firma.test"),
            ("password", "anna-passwort-1"), ("quotaMb", "500"));
        var anna = _server.HostAccounts.FindAccount(EmailAddress.Parse("anna@neu-firma.test"));
        Assert.NotNull(anna);
        Assert.Equal(500L * 1024 * 1024, anna.QuotaBytes);
        Assert.NotNull(_server.HostMailboxes.GetFolder(anna.Id, "Junk"));
        Assert.NotNull(_server.HostAccounts.Authenticate("anna@neu-firma.test", "anna-passwort-1"));
    }

    [Fact]
    public async Task Rejects_short_passwords_and_duplicates()
    {
        await _web.PostAsync("/Admin/Mailboxes", "/Admin/Mailboxes", ("localPart", "kurz"), ("domain", "example.test"), ("password", "123"), ("quotaMb", "0"));
        Assert.Contains("mindestens 10 Zeichen", _web.LastPage);
        Assert.Null(_server.HostAccounts.FindAccount(EmailAddress.Parse("kurz@example.test")));

        await _web.PostAsync("/Admin/Mailboxes", "/Admin/Mailboxes", ("localPart", "alice"), ("domain", "example.test"), ("password", "lang-genug-123"), ("quotaMb", "0"));
        Assert.Contains("existiert bereits", _web.LastPage);
    }

    [Fact]
    public async Task Edits_and_deletes_mailbox_but_protects_own()
    {
        const string bob = "/Admin/Mailboxes/Edit?address=bob%40example.test";
        await _web.PostAsync(bob, $"{bob}&handler=Save", ("quotaMb", "100"), ("isAdmin", "false"));
        var account = _server.HostAccounts.FindAccount(EmailAddress.Parse("bob@example.test"))!;
        Assert.False(account.Enabled);
        Assert.Equal(100L * 1024 * 1024, account.QuotaBytes);

        await _web.PostAsync(bob, $"{bob}&handler=Password", ("password", "bobs-neues-passwort"));
        _server.HostAccounts.SetEnabled(EmailAddress.Parse("bob@example.test"), true);
        Assert.NotNull(_server.HostAccounts.Authenticate("bob@example.test", "bobs-neues-passwort"));
        Assert.Null(_server.HostAccounts.Authenticate("bob@example.test", TestServer.Password));

        await _web.PostAsync(bob, $"{bob}&handler=Delete");
        Assert.Null(_server.HostAccounts.FindAccount(EmailAddress.Parse("bob@example.test")));

        const string self = "/Admin/Mailboxes/Edit?address=chef%40example.test";
        await _web.PostAsync(self, $"{self}&handler=Delete");
        Assert.NotNull(_server.HostAccounts.FindAccount(EmailAddress.Parse(Admin)));
        Assert.Contains("eigene Postfach kann nicht gelöscht werden", _web.LastPage);
    }

    [Fact]
    public async Task Manages_aliases()
    {
        await _web.PostAsync("/Admin/Aliases", "/Admin/Aliases", ("address", "kontakt@example.test"), ("targets", "alice@example.test, extern@remote.test"));
        var alias = _server.HostAccounts.FindAlias(EmailAddress.Parse("kontakt@example.test"));
        Assert.Equal(2, alias!.Targets.Count);

        await _web.PostAsync("/Admin/Aliases", "/Admin/Aliases?handler=Delete", ("address", "kontakt@example.test"));
        Assert.Null(_server.HostAccounts.FindAlias(EmailAddress.Parse("kontakt@example.test")));
    }

    [Fact]
    public async Task Admin_creates_domain_rule()
    {
        await _web.PostAsync("/Rules/Edit?scope=example.test", "/Rules/Edit",
            ("Form.Scope", "example.test"), ("Form.MatchAll", "true"),
            ("Form.Conditions[0].Field", "SpamScore"), ("Form.Conditions[0].Operator", "Greater"), ("Form.Conditions[0].Value", "12"),
            ("Form.Action", "Delete"), ("Form.Priority", "10"), ("Form.Stop", "true"), ("Form.Enabled", "true"));

        var rule = Assert.Single(_server.Services.GetRequiredService<Core.Rules.RuleStore>().List("example.test"));
        Assert.Equal("Spam-Score über \"12\" → endgültig löschen", rule.Name);
        Assert.Contains("Alle Regeln", _web.LastPage);
    }

    [Fact]
    public async Task Settings_apply_without_restart()
    {
        var options = _server.Services.GetRequiredService<IOptions<MailserverOptions>>();
        var form = SettingsFields(("Form.JunkThreshold", "6.5"), ("Form.TrustedNetworks", "192.0.2.0/24\n2001:db8::/32"),
            ("Form.DnsBlocklists", "zen.spamhaus.org;Reject\nbl.example.test;Score;2,5"));
        await _web.PostAsync("/Admin/Settings", "/Admin/Settings", form);

        Assert.Contains("Einstellungen gespeichert", _web.LastPage);
        await TestServer.WaitUntilAsync(() => options.Value.Spam.JunkThreshold == 6.5, "reloaded settings");
        Assert.Equal(["192.0.2.0/24", "2001:db8::/32"], options.Value.Spam.TrustedNetworks);
        Assert.Equal(2.5, options.Value.Spam.EffectiveDnsBlocklists.Single(b => b.Zone == "bl.example.test").Score);
        Assert.False(options.Value.Spam.Greylisting.Enabled);  // unchecked box
        Assert.True(File.Exists(Path.Combine(_server.DataDirectory, "settings.json")));
    }

    [Fact]
    public async Task Antivirus_and_sending_limits_are_saved()
    {
        var options = _server.Services.GetRequiredService<IOptions<MailserverOptions>>();
        await _web.PostAsync("/Admin/Settings", "/Admin/Settings", SettingsFields(
            ("Form.AntivirusEnabled", "true"), ("Form.Scanner", "ClamAV"), ("Form.ClamAvHost", "10.0.0.5"), ("Form.ClamAvPort", "3311"),
            ("Form.ScanOutgoing", "false"), ("Form.SuspiciousAttachments", "Reject"), ("Form.OnScanError", "Defer"),
            ("Form.BlockedExtensions", "exe, .JS; iso"), ("Form.SendMaxPerMessage", "20"), ("Form.SendMaxPerHour", "50"),
            ("Form.SendMaxPerDay", "200"), ("Form.SendBlockOnLimit", "true")));

        Assert.Contains("Einstellungen gespeichert", _web.LastPage);
        await TestServer.WaitUntilAsync(() => options.Value.Security.Sending.MaxRecipientsPerHour == 50, "reloaded settings");
        var antivirus = options.Value.Antivirus;
        Assert.Equal(("ClamAV", "10.0.0.5", 3311, "Reject", "Defer"), (antivirus.Scanner, antivirus.ClamAvHost, antivirus.ClamAvPort,
            antivirus.SuspiciousAttachments, antivirus.OnScanError));
        Assert.False(antivirus.ScanOutgoing);
        Assert.Equal(["exe", "iso", "js"], antivirus.EffectiveBlockedExtensions.Order());
        Assert.Equal((20, 200), (options.Value.Security.Sending.MaxRecipientsPerMessage, options.Value.Security.Sending.MaxRecipientsPerDay));

        await _web.PostAsync("/Admin/Settings", "/Admin/Settings", SettingsFields(("Form.SendMaxPerHour", "-1")));
        Assert.Contains("Versandlimits dürfen nicht negativ sein", _web.LastPage);
    }

    [Fact]
    public async Task Scanner_test_reports_a_missing_scanner()
    {
        await _web.PostAsync("/Admin/Settings", "/Admin/Settings?handler=TestScanner");
        Assert.Contains("nur Anhangfilter", _web.LastPage);
    }

    [Fact]
    public async Task Invalid_settings_are_rejected()
    {
        await _web.PostAsync("/Admin/Settings", "/Admin/Settings", SettingsFields(("Form.TrustedNetworks", "kein-netz")));
        Assert.Contains("Ungültiges Netz", _web.LastPage);
        Assert.False(File.Exists(Path.Combine(_server.DataDirectory, "settings.json")));
    }

    [Fact]
    public async Task History_shows_entries_and_exports_csv()
    {
        var log = _server.Services.GetRequiredService<SpamLog>();
        log.Write(new SpamLogEntry { Session = "s1", Stage = SpamLogStage.Data, Action = SpamLogAction.Spam, Score = 8, ClientIp = "203.0.113.4",
            HeaderFrom = "x@spam.test", Subject = "Gewinnspiel", MessageId = "m1@spam.test", Tests = "SPF_FAIL=3.5" });
        log.Write(new SpamLogEntry { Session = "s1", Stage = SpamLogStage.Delivery, Action = SpamLogAction.Delivered, Recipient = "alice@example.test", Folder = "Junk", Score = 8 });
        log.WriteAuthFailure("IMAP", "alice@example.test", IPAddress.Parse("198.51.100.9"), lockedOut: false);

        await _web.GetAsync("/Admin/Log?since=24h");
        Assert.Contains("Gewinnspiel", _web.LastPage);
        Assert.Contains("Anmeldung fehlgeschlagen", _web.LastPage);

        await _web.GetAsync("/Admin/Log?view=auth");
        Assert.DoesNotContain("Gewinnspiel", _web.LastPage);

        await _web.GetAsync("/Admin/Log/Session?id=s1");
        Assert.Contains("SPF_FAIL", _web.LastPage);
        Assert.Contains("Spam", _web.LastPage);

        var csv = await _web.GetAsync("/Admin/Log?handler=Export&since=24h");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        Assert.Contains("Gewinnspiel", _web.LastPage);

        // The user sees the delivery in their own history.
        using var alice = new WebClient(_server.WebPort);
        await alice.LoginAsync("alice@example.test", TestServer.Password);
        await alice.GetAsync("/Spam");
        Assert.Contains("Gewinnspiel", alice.LastPage);
        Assert.Contains("Absender erlauben", alice.LastPage);
    }

    [Fact]
    public async Task Removing_admin_rights_ends_admin_session()
    {
        _server.HostAccounts.SetAdmin(EmailAddress.Parse(Admin), false);
        var response = await _web.GetAsync("/Admin");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Login", response.Headers.Location!.PathAndQuery);
    }

    private static (string, string)[] SettingsFields(params (string Name, string Value)[] overrides)
    {
        var fields = new Dictionary<string, string>
        {
            ["Form.SpamEnabled"] = "true", ["Form.JunkThreshold"] = "5", ["Form.DeleteThreshold"] = "0", ["Form.EnforceDmarcReject"] = "true",
            ["Form.TrustLoopback"] = "true", ["Form.TrustedNetworks"] = "", ["Form.DnsBlocklistsEnabled"] = "true", ["Form.DnsBlocklists"] = "",
            ["Form.GreylistingDelayMinutes"] = "5", ["Form.GreylistingExpiryDays"] = "36", ["Form.LogEnabled"] = "true", ["Form.LogRetentionDays"] = "90",
            ["Form.MaxAuthFailuresPerIp"] = "10", ["Form.AuthFailureWindowMinutes"] = "15", ["Form.AuthLockoutMinutes"] = "30",
            ["Form.RejectUnauthenticatedLocalSender"] = "true", ["Form.MaxHopCount"] = "30", ["Form.MaxQueueLifetimeDays"] = "5",
            ["Form.MaxParallelDeliveries"] = "4", ["Form.SmartHostPort"] = "587", ["Form.SmartHostSecurity"] = "Auto",
        };
        foreach (var (name, value) in overrides)
        {
            fields[name] = value;
        }

        return fields.Select(f => (f.Key, f.Value)).ToArray();
    }
}
