using System.Collections.Concurrent;
using Mailserver.Core;
using Mailserver.Core.External;
using Mailserver.Web.Pages.Account;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Mailserver.Tests;

public sealed class ExternalProviderTests
{
    [Theory]
    [InlineData("anna@t-online.de", "secureimap.t-online.de", "securesmtp.t-online.de")]
    [InlineData("anna@magenta.de", "secureimap.t-online.de", "securesmtp.t-online.de")]
    [InlineData("anna@GMX.de", "imap.gmx.net", "mail.gmx.net")]
    [InlineData("anna@web.de", "imap.web.de", "smtp.web.de")]
    [InlineData("anna@gmail.com", "imap.gmail.com", "smtp.gmail.com")]
    public void Known_providers_fill_in_empty_server_fields(string address, string imap, string smtp)
    {
        var settings = new ExternalForm { Address = address, Send = true }.ToSettings();
        Assert.Equal(imap, settings.Imap.Host);
        Assert.Equal(smtp, settings.Smtp!.Host);
        Assert.Equal(ExternalAuth.Password, settings.Auth);
    }

    [Fact]
    public void Entered_servers_win_and_unknown_domains_are_guessed()
    {
        var own = new ExternalForm { Address = "anna@t-online.de", ImapHost = "imap.example.test", ImapPort = 143, ImapSecurity = MailSecurity.StartTls }.ToSettings();
        Assert.Equal(new MailServerAddress("imap.example.test", 143, MailSecurity.StartTls), own.Imap);

        var guessed = new ExternalForm { Address = "anna@kleiner-anbieter.de", Send = true }.ToSettings();
        Assert.Equal("imap.kleiner-anbieter.de", guessed.Imap.Host);
        Assert.Equal("smtp.kleiner-anbieter.de", guessed.Smtp!.Host);
    }

    [Fact]
    public void Common_app_registration_mistakes_get_a_hint()
    {
        Assert.Contains("Öffentliche Clientflows zulassen", MicrosoftMailAuth.Hint("AADSTS70002: The provided client is not supported for this feature."));
        Assert.Contains("persönliche Microsoft-Konten", MicrosoftMailAuth.Hint("AADSTS700016: Application not found"));
        Assert.Equal("", MicrosoftMailAuth.Hint("AADSTS90000: anything else"));
    }

    [Theory]
    [InlineData("anna@live.com")]
    [InlineData("anna@outlook.de")]
    [InlineData("anna@hotmail.com")]
    public void Outlook_addresses_sign_in_through_microsoft(string address)
    {
        var settings = new ExternalForm { Address = address, ImapHost = "egal.example", Send = true }.ToSettings();
        Assert.Equal(ExternalAuth.Microsoft, settings.Auth);
        Assert.Equal("outlook.office365.com", settings.Imap.Host);
        Assert.Equal("smtp-mail.outlook.com", settings.Smtp!.Host);
    }
}

/// <summary>Sign-in of an Outlook.com address with a code, against a stand-in for login.microsoftonline.com.</summary>
public sealed class MicrosoftSignInTests : IAsyncLifetime
{
    private readonly FakeMicrosoftLogin _login = new();
    private readonly RecordingExternalMail _mail = new();
    private TestServer _server = null!;

    public async Task InitializeAsync()
    {
        await _login.StartAsync();
        _server = await TestServer.StartAsync(new Dictionary<string, string?>
        {
            ["Mailserver:External:MicrosoftClientId"] = "11111111-2222-3333-4444-555555555555",
            ["Mailserver:External:MicrosoftLoginUrl"] = _login.Url,
        }, services => services.AddSingleton<IExternalMail>(_mail));
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _login.DisposeAsync();
    }

    [Fact]
    public async Task Code_sign_in_stores_the_refresh_token_and_renews_access()
    {
        using var web = new WebClient(_server.WebPort);
        await web.LoginAsync("alice@example.test", TestServer.Password);
        await web.PostAsync("/Account/External", "/Account/External?handler=Save",
            ("Form.Address", "anna@live.com"), ("Form.Folder", "Outlook"), ("Form.Send", "true"));
        Assert.Contains("ABCD-1234", web.LastPage);
        Assert.Contains("https://microsoft.com/devicelogin", web.LastPage);

        var store = _server.Services.GetRequiredService<ExternalAccountStore>();
        await TestServer.WaitUntilAsync(() => store.List(_server.User("alice").Id).Count == 1, "sign-in completed");
        var account = store.List(_server.User("alice").Id).Single();
        Assert.Equal(ExternalAuth.Microsoft, account.Settings.Auth);
        Assert.Equal("outlook.office365.com", account.Settings.Imap.Host);
        Assert.Equal("refresh-1", store.Password(account.Id));
        Assert.Equal("access-1", _mail.TestedWith);
        Assert.Contains("client_id=11111111-2222-3333-4444-555555555555", _login.Requests.First());

        // The account is stored before the first fetch; the result message only follows after it.
        for (var i = 0; i < 50; i++)
        {
            await web.GetAsync("/Account/External");
            if (web.LastPage.Contains("eingerichtet"))
            {
                break;
            }

            await Task.Delay(200);
        }

        Assert.Contains("eingerichtet", web.LastPage);
        Assert.Contains("über Microsoft angemeldet", web.LastPage);

        // Later connections: the refresh token gives a new access token, and Microsoft's new refresh token is kept.
        var auth = _server.Services.GetRequiredService<MicrosoftMailAuth>();
        Assert.Equal("access-2", await auth.AccessTokenAsync(account, CancellationToken.None));
        Assert.Equal("refresh-2", store.Password(account.Id));
        Assert.Equal("access-2", await auth.AccessTokenAsync(account, CancellationToken.None));

        // Revoked at Microsoft: a clear message.
        auth.Forget(account.Id);
        store.SetSecret(account.Id, "widerrufen");
        var ex = await Assert.ThrowsAsync<ExternalMailException>(() => auth.AccessTokenAsync(account, CancellationToken.None));
        Assert.Contains("Neu anmelden", ex.Message);
    }

    [Fact]
    public async Task Only_administrators_set_the_app_registration()
    {
        using var web = new WebClient(_server.WebPort);
        await web.LoginAsync("alice@example.test", TestServer.Password);
        await web.PostAsync("/Account/External", "/Account/External?handler=MicrosoftApp", ("clientId", "99999999-2222-3333-4444-555555555555"));
        Assert.DoesNotContain("App-Registrierung gespeichert", web.LastPage);
        Assert.False(File.Exists(Path.Combine(_server.DataDirectory, "settings.json")) &&
                     File.ReadAllText(Path.Combine(_server.DataDirectory, "settings.json")).Contains("99999999"));
    }

    private sealed class RecordingExternalMail : IExternalMail
    {
        public string? TestedWith { get; private set; }

        public Task<string?> TestAsync(ExternalAccountSettings settings, string secret, CancellationToken cancellationToken)
        {
            TestedWith = secret;
            return Task.FromResult<string?>(null);
        }

        public Task SendAsync(ExternalAccount account, byte[] message, IReadOnlyList<EmailAddress> recipients, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<FetchResult> FetchAsync(ExternalAccount account, CancellationToken cancellationToken) => Task.FromResult(new FetchResult(0, null));
    }
}

/// <summary>Device code flow and refresh tokens as login.microsoftonline.com answers them.</summary>
public sealed class FakeMicrosoftLogin : FakeCloud
{
    private int _polls;

    public ConcurrentQueue<string> Requests { get; } = new();

    public new Task StartAsync() => base.StartAsync();

    protected override async Task HandleAsync(HttpContext context)
    {
        var body = System.Text.Encoding.UTF8.GetString(await BodyAsync(context));
        Requests.Enqueue(body);
        var form = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(body);
        if (context.Request.Path.Value!.EndsWith("/devicecode", StringComparison.Ordinal))
        {
            await JsonAsync(context, new { user_code = "ABCD-1234", verification_uri = "https://microsoft.com/devicelogin", device_code = "dev-1",
                interval = 1, expires_in = 600 });
            return;
        }

        switch ((string?)form["grant_type"])
        {
            case "urn:ietf:params:oauth:grant-type:device_code":
                if (Interlocked.Increment(ref _polls) == 1)
                {
                    await JsonAsync(context, new { error = "authorization_pending" }, 400);
                    return;
                }

                await JsonAsync(context, new { access_token = "access-1", refresh_token = "refresh-1", expires_in = 3600 });
                return;
            case "refresh_token" when (string?)form["refresh_token"] == "refresh-1":
                await JsonAsync(context, new { access_token = "access-2", refresh_token = "refresh-2", expires_in = 3600 });
                return;
            default:
                await JsonAsync(context, new { error = "invalid_grant", error_description = "AADSTS70000: The refresh token was revoked.\r\nTrace ID: x" }, 400);
                return;
        }
    }
}
