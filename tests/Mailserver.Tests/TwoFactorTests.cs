using System.Net;
using System.Text.RegularExpressions;
using Mailserver.Core.Accounts;
using Microsoft.Extensions.DependencyInjection;

namespace Mailserver.Tests;

/// <summary>Two-factor sign-in with an authenticator app, trusted devices and "Angemeldet bleiben".</summary>
public sealed partial class TwoFactorTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private TwoFactor TwoFactor => _server.Services.GetRequiredService<TwoFactor>();

    [Fact]
    public void Codes_match_the_RFC_6238_test_vectors()
    {
        var key = "12345678901234567890"u8.ToArray();
        // RFC 6238 appendix B (SHA-1, 8 digits) – the app shows the last six.
        Assert.Equal("287082", TwoFactor.Code(key, 59 / 30));
        Assert.Equal("081804", TwoFactor.Code(key, 1111111109 / 30));
        Assert.Equal("279037", TwoFactor.Code(key, 2000000000 / 30));
        Assert.Equal(key, TwoFactor.FromBase32(TwoFactor.Base32(key)));
        Assert.Equal(key, TwoFactor.FromBase32("gezd gnbv gy3t qojq gezd gnbv gy3t qojq"));
    }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;

    private static string CodeFor(string secret, long step) => TwoFactor.Code(TwoFactor.FromBase32(secret), step);

    [Fact]
    public async Task Sign_in_asks_for_the_code_unless_the_device_is_trusted()
    {
        using var alice = new WebClient(_server.WebPort);
        await alice.LoginAsync("alice@example.test", TestServer.Password);
        await alice.GetAsync("/Account/Security");
        Assert.Contains("Einrichten", alice.LastPage);

        await alice.GetAsync("/Account/Security?handler=Setup");
        Assert.Contains("<svg", alice.LastPage);
        Assert.Contains("otpauth://totp/", alice.LastPage);
        var secret = SecretPattern().Match(alice.LastPage).Groups[1].Value.Replace(" ", "");
        var protectedSecret = WebUtility.HtmlDecode(ProtectedPattern().Match(alice.LastPage).Groups[1].Value);
        Assert.Equal(32, secret.Length);

        var step = Now;
        var wrong = CodeFor(secret, step) == "000000" || CodeFor(secret, step + 1) == "000000" ? "111111" : "000000";
        await alice.PostAsync("/Account/Security", "/Account/Security?handler=Enable", ("secret", protectedSecret), ("code", wrong));
        Assert.Contains("Der Code passt nicht", alice.LastPage);
        Assert.False(TwoFactor.IsEnabled(_server.User("alice").Id));

        await alice.PostAsync("/Account/Security", "/Account/Security?handler=Enable", ("secret", protectedSecret), ("code", CodeFor(secret, step)));
        Assert.Contains("Ihre Wiederherstellungscodes", alice.LastPage);
        var recoveryCodes = RecoveryPattern().Matches(alice.LastPage).Select(m => m.Value).Distinct().ToList();
        Assert.Equal(TwoFactor.RecoveryCodeCount, recoveryCodes.Count);
        Assert.True(TwoFactor.IsEnabled(_server.User("alice").Id));
        // The session that switched it on continues.
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync("/Mail")).StatusCode);

        // Another browser: password, then code.
        using var phone = new WebClient(_server.WebPort);
        await phone.LoginAsync("alice@example.test", TestServer.Password);
        Assert.Contains("Bestätigungscode", phone.LastPage);
        Assert.Equal(HttpStatusCode.Redirect, (await phone.GetAsync("/Mail")).StatusCode);
        await phone.PostAsync("/Login/Code", "/Login/Code", ("code", wrong));
        Assert.Contains("Der Code stimmt nicht", phone.LastPage);
        // The code used for switching it on does not work a second time; the next one does.
        await phone.PostAsync("/Login/Code", "/Login/Code", ("code", CodeFor(secret, step)));
        Assert.Contains("Der Code stimmt nicht", phone.LastPage);
        var login = await phone.PostAsync("/Login/Code", "/Login/Code", ("code", CodeFor(secret, step + 1)), ("trust", "true"));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/Mail")).StatusCode);

        // The trusted phone signs in again with the password alone.
        await phone.PostAsync("/Mail", "/Logout");
        await phone.LoginAsync("alice@example.test", TestServer.Password);
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/Mail")).StatusCode);
        await phone.GetAsync("/Account/Security");
        Assert.Contains("Unbekanntes Gerät", phone.LastPage); // the test client sends no user agent
        Assert.Equal("iPhone · Safari", Mailserver.Web.WebSignIn.DeviceName(
            "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1"));

        // A recovery code works once.
        using var laptop = new WebClient(_server.WebPort);
        await laptop.LoginAsync("alice@example.test", TestServer.Password);
        await laptop.PostAsync("/Login/Code", "/Login/Code", ("code", recoveryCodes[0].ToLowerInvariant()));
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync("/Mail")).StatusCode);
        Assert.Equal(TwoFactor.RecoveryCodeCount - 1, TwoFactor.RecoveryCodesLeft(_server.User("alice").Id));
        using var other = new WebClient(_server.WebPort);
        await other.LoginAsync("alice@example.test", TestServer.Password);
        await other.PostAsync("/Login/Code", "/Login/Code", ("code", recoveryCodes[0]));
        Assert.Contains("Der Code stimmt nicht", other.LastPage);

        // A new password ends the trust.
        _server.HostAccounts.SetPassword(Mailserver.Core.EmailAddress.Parse("alice@example.test"), "ganz-neues-passwort");
        await phone.LoginAsync("alice@example.test", "ganz-neues-passwort");
        Assert.Contains("Bestätigungscode", phone.LastPage);

        // Without the password it cannot be switched off; the administrator can reset it.
        await alice.LoginAsync("alice@example.test", "ganz-neues-passwort");
        await alice.PostAsync("/Login/Code", "/Login/Code", ("code", recoveryCodes[1]));
        await alice.PostAsync("/Account/Security", "/Account/Security?handler=Disable", ("current", "falsch"));
        Assert.Contains("aktuelle Passwort ist falsch", alice.LastPage);
        Assert.True(TwoFactor.IsEnabled(_server.User("alice").Id));

        _server.HostAccounts.SetAdmin(Mailserver.Core.EmailAddress.Parse("bob@example.test"), true);
        using var bob = new WebClient(_server.WebPort);
        await bob.LoginAsync("bob@example.test", TestServer.Password);
        await bob.GetAsync("/Admin/Mailboxes/Edit?address=alice@example.test");
        Assert.Contains("Zurücksetzen (Handy verloren)", bob.LastPage);
        await bob.PostAsync("/Admin/Mailboxes/Edit?address=alice@example.test",
            "/Admin/Mailboxes/Edit?handler=ResetTwoFactor&address=alice@example.test");
        Assert.Contains("Zwei-Faktor-Anmeldung für alice@example.test ausgeschaltet", bob.LastPage);
        Assert.False(TwoFactor.IsEnabled(_server.User("alice").Id));
        Assert.Empty(TwoFactor.Devices(_server.User("alice").Id));

        using var plain = new WebClient(_server.WebPort);
        await plain.LoginAsync("alice@example.test", "ganz-neues-passwort");
        Assert.Equal(HttpStatusCode.OK, (await plain.GetAsync("/Mail")).StatusCode);
    }

    [Fact]
    public async Task Stay_signed_in_makes_the_session_cookie_persistent()
    {
        using var web = new WebClient(_server.WebPort);
        var once = await web.PostAsync("/Login", "/Login", ("email", "alice@example.test"), ("password", TestServer.Password));
        Assert.DoesNotContain("expires=", SessionCookie(once), StringComparison.OrdinalIgnoreCase);

        using var remembered = new WebClient(_server.WebPort);
        var stay = await remembered.PostAsync("/Login", "/Login", ("email", "alice@example.test"), ("password", TestServer.Password),
            ("remember", "true"));
        var cookie = SessionCookie(stay);
        var expires = DateTimeOffset.Parse(Regex.Match(cookie, "expires=([^;]+)", RegexOptions.IgnoreCase).Groups[1].Value);
        Assert.InRange(expires - DateTimeOffset.UtcNow, TimeSpan.FromDays(29), TimeSpan.FromDays(31));
    }

    private static string SessionCookie(HttpResponseMessage response) =>
        response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("__Host-mailserver=", StringComparison.Ordinal));

    [GeneratedRegex("""Schlüssel zum Abtippen</div>\s*<div class="mono"[^>]*>([A-Z2-7 ]+)</div>""")]
    private static partial Regex SecretPattern();

    [GeneratedRegex("""name="secret" value="([^"]+)" """)]
    private static partial Regex ProtectedPattern();

    [GeneratedRegex("[A-HJ-NP-Z2-9]{5}-[A-HJ-NP-Z2-9]{5}")]
    private static partial Regex RecoveryPattern();
}
