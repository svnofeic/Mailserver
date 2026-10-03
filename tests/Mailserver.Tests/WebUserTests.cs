using System.Net;
using Mailserver.Core;
using Mailserver.Core.Rules;
using Microsoft.Extensions.DependencyInjection;

namespace Mailserver.Tests;

public sealed class WebUserTests : IAsyncLifetime
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

    [Fact]
    public async Task Requires_login_and_sets_security_headers()
    {
        var response = await _web.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Login", response.Headers.Location!.PathAndQuery);

        var login = await _web.GetAsync("/Login");
        Assert.Equal("DENY", login.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", login.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("Strict-Transport-Security", login.Headers.Select(h => h.Key));
    }

    [Fact]
    public async Task Wrong_password_is_rejected()
    {
        await _web.PostAsync("/Login", "/Login", ("email", "alice@example.test"), ("password", "falsch"));
        Assert.Contains("E-Mail-Adresse oder Passwort ist falsch", _web.LastPage);
        Assert.Equal(HttpStatusCode.Redirect, (await _web.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task User_sees_dashboard_but_not_admin_area()
    {
        await _web.LoginAsync("alice@example.test", TestServer.Password);

        var dashboard = await _web.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        Assert.Contains("alice@example.test", _web.LastPage);
        Assert.Contains("Posteingang", _web.LastPage);
        Assert.DoesNotContain("/Admin/Settings", _web.LastPage);

        var admin = await _web.GetAsync("/Admin");
        Assert.Equal(HttpStatusCode.Redirect, admin.StatusCode);
        Assert.StartsWith("/Verboten", admin.Headers.Location!.PathAndQuery);
    }

    [Fact]
    public async Task User_creates_edits_and_deletes_own_rule()
    {
        await _web.LoginAsync("alice@example.test", TestServer.Password);

        await _web.PostAsync("/Rules/Edit", "/Rules/Edit",
            ("Form.Scope", "*"), // ignored for non-admins
            ("Form.MatchAll", "true"),
            ("Form.Conditions[0].Field", "Subject"), ("Form.Conditions[0].Operator", "Contains"), ("Form.Conditions[0].Value", "Sie haben gewonnen"),
            ("Form.Conditions[1].Field", "Subject"), ("Form.Conditions[1].Operator", "Contains"), ("Form.Conditions[1].Value", ""),
            ("Form.Action", "Delete"), ("Form.Priority", "50"), ("Form.Stop", "true"), ("Form.Enabled", "true"));

        var rules = _server.Services.GetRequiredService<RuleStore>();
        var rule = Assert.Single(rules.List());
        Assert.Equal("alice@example.test", rule.Scope);
        Assert.Equal(RuleAction.Delete, rule.Action);
        Assert.Equal("Betreff enthält \"Sie haben gewonnen\" → endgültig löschen", rule.Name);
        Assert.Contains("Regel gespeichert", _web.LastPage);
        Assert.Contains("Sie haben gewonnen", _web.LastPage);

        await _web.PostAsync("/Rules", $"/Rules/Edit?handler=Toggle&id={rule.Id}");
        Assert.False(rules.Get(rule.Id)!.Enabled);

        await _web.PostAsync("/Rules", $"/Rules/Edit?handler=Delete&id={rule.Id}");
        Assert.Empty(rules.List());
    }

    [Fact]
    public async Task User_cannot_touch_other_users_rules()
    {
        var rules = _server.Services.GetRequiredService<RuleStore>();
        var bobs = rules.Add("bob@example.test", "Bobs Regel", [new RuleCondition(RuleField.Subject, RuleOperator.Contains, "x")], RuleAction.Junk);
        await _web.LoginAsync("alice@example.test", TestServer.Password);

        Assert.Equal(HttpStatusCode.NotFound, (await _web.GetAsync($"/Rules/Edit?id={bobs.Id}")).StatusCode);
        await _web.PostAsync("/Rules", $"/Rules/Edit?handler=Delete&id={bobs.Id}");
        Assert.NotNull(rules.Get(bobs.Id));
    }

    [Fact]
    public async Task Invalid_rule_shows_error_and_keeps_input()
    {
        await _web.LoginAsync("alice@example.test", TestServer.Password);
        await _web.PostAsync("/Rules/Edit", "/Rules/Edit",
            ("Form.MatchAll", "true"),
            ("Form.Conditions[0].Field", "Subject"), ("Form.Conditions[0].Operator", "Regex"), ("Form.Conditions[0].Value", "([a-z"),
            ("Form.Action", "Junk"), ("Form.Priority", "100"), ("Form.Stop", "true"), ("Form.Enabled", "true"));

        Assert.Contains("([a-z", _web.LastPage);
        Assert.Contains("notice bad", _web.LastPage);
        Assert.Empty(_server.Services.GetRequiredService<RuleStore>().List());
    }

    [Fact]
    public async Task Posts_without_antiforgery_token_are_refused()
    {
        await _web.LoginAsync("alice@example.test", TestServer.Password);
        var response = await _web.PostRawAsync("/Account/Password", ("current", TestServer.Password), ("password", "neues-passwort-1"), ("confirm", "neues-passwort-1"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(_server.HostAccounts.Authenticate("alice@example.test", TestServer.Password));
    }

    [Fact]
    public async Task Password_change_ends_other_sessions()
    {
        using var other = new WebClient(_server.WebPort);
        await other.LoginAsync("alice@example.test", TestServer.Password);
        await _web.LoginAsync("alice@example.test", TestServer.Password);

        await _web.PostAsync("/Account/Password", "/Account/Password",
            ("current", TestServer.Password), ("password", "ganz-neues-passwort"), ("confirm", "ganz-neues-passwort"));

        Assert.Contains("Das Passwort wurde geändert", _web.LastPage);
        Assert.NotNull(_server.HostAccounts.Authenticate("alice@example.test", "ganz-neues-passwort"));
        Assert.Equal(HttpStatusCode.OK, (await _web.GetAsync("/")).StatusCode);       // this session was renewed
        Assert.Equal(HttpStatusCode.Redirect, (await other.GetAsync("/")).StatusCode); // the other one is gone
    }

    [Fact]
    public async Task Spam_history_offers_sender_rules()
    {
        await _web.LoginAsync("alice@example.test", TestServer.Password);
        await _web.PostAsync("/Spam", "/Spam?handler=Allow", ("sender", "chef@kunde.test"));

        var rule = Assert.Single(_server.Services.GetRequiredService<RuleStore>().List("alice@example.test"));
        Assert.Equal(RuleAction.Inbox, rule.Action);
        Assert.Contains("chef@kunde.test erlaubt", _web.LastPage);
    }
}
