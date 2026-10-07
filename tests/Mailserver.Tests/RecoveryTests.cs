using System.Text.RegularExpressions;
using Mailserver.Core;
using Mailserver.Core.Accounts;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace Mailserver.Tests;

/// <summary>"Passwort vergessen" with a confirmed external address.</summary>
public sealed partial class RecoveryTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private PasswordRecovery Recovery => _server.Services.GetRequiredService<PasswordRecovery>();

    /// <summary>The link of the next mail sent to the outside, decoded from the message.</summary>
    private async Task<(string Link, string Text)> NextMailAsync(int count)
    {
        await TestServer.WaitUntilAsync(() => _server.Remote.Messages.Count >= count, "mail to the external address");
        var mail = _server.Remote.Messages.ElementAt(count - 1);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(mail.Content));
        var text = (await MimeMessage.LoadAsync(stream)).TextBody;
        return (LinkPattern().Match(text).Value, text);
    }

    [Fact]
    public async Task User_confirms_an_external_address_and_resets_a_forgotten_password()
    {
        using (var alice = new WebClient(_server.WebPort))
        {
            await alice.LoginAsync("alice@example.test", TestServer.Password);
            await alice.PostAsync("/Account/Password", "/Account/Password?handler=Recovery", ("address", "alice@remote.test"), ("current", "falsch"));
            Assert.Contains("aktuelle Passwort ist falsch", alice.LastPage);

            await alice.PostAsync("/Account/Password", "/Account/Password?handler=Recovery", ("address", "alice@remote.test"), ("current", TestServer.Password));
            Assert.Contains("Bestätigungslink an alice@remote.test", alice.LastPage);
            Assert.Contains("wartet auf Bestätigung", alice.LastPage);
        }

        var (confirm, _) = await NextMailAsync(1);
        Assert.Contains("/Recovery/Confirm?token=", confirm);
        Assert.False(Recovery.Get(_server.User("alice").Id)!.Verified);

        // Not signed in: the link is opened from the other mailbox. Opening alone does not confirm (mail scanners open links).
        using var web = new WebClient(_server.WebPort);
        var path = new Uri(confirm).PathAndQuery;
        await web.GetAsync(path);
        Assert.False(Recovery.Get(_server.User("alice").Id)!.Verified);
        await web.PostAsync(path, "/Recovery/Confirm", ("token", path.Split("token=")[1]));
        Assert.Contains("Bestätigt", web.LastPage);
        Assert.True(Recovery.Get(_server.User("alice").Id)!.Verified);

        // Forgotten password.
        await web.GetAsync("/Login");
        Assert.Contains("Passwort vergessen?", web.LastPage);
        await web.PostAsync("/Recovery", "/Recovery", ("email", "alice@example.test"));
        Assert.Contains("Falls für dieses Postfach eine bestätigte Ersatz-Adresse hinterlegt ist", web.LastPage);
        var (reset, text) = await NextMailAsync(2);
        Assert.Contains("30 Minuten", text);
        var resetPath = new Uri(reset).PathAndQuery;
        var token = resetPath.Split("token=")[1];

        await web.GetAsync(resetPath);
        Assert.Contains("alice@example.test", web.LastPage);
        await web.PostAsync(resetPath, "/Recovery/Reset", ("token", token), ("password", "kurz"), ("confirm", "kurz"));
        Assert.Contains("mindestens", web.LastPage);
        await web.PostAsync(resetPath, "/Recovery/Reset", ("token", token), ("password", "ganz-neues-passwort"), ("confirm", "ganz-neues-passwort"));
        Assert.Contains("ist gesetzt", web.LastPage);

        Assert.NotNull(_server.HostAccounts.Authenticate("alice@example.test", "ganz-neues-passwort"));
        Assert.Null(_server.HostAccounts.Authenticate("alice@example.test", TestServer.Password));
        await TestServer.WaitUntilAsync(() => _server.Inbox("alice").Count == 1, "notice in the mailbox");

        // A link works once.
        await web.GetAsync(resetPath);
        Assert.Contains("ungültig oder abgelaufen", web.LastPage);
    }

    [Fact]
    public async Task Same_answer_without_a_mail_for_unknown_mailboxes_unconfirmed_addresses_and_admins()
    {
        var alice = _server.User("alice");
        _ = Recovery.SetAddress(alice, "alice@remote.test"); // not confirmed
        var admin = _server.HostAccounts.AddAccount(EmailAddress.Parse("chef@example.test"), TestServer.Password);
        _server.HostAccounts.SetAdmin(admin.Address, true);
        Recovery.SetConfirmedAddress(_server.HostAccounts.FindAccount(admin.Address)!, "chef@remote.test");

        using var web = new WebClient(_server.WebPort);
        foreach (var address in new[] { "niemand@example.test", "alice@example.test", "chef@example.test" })
        {
            await web.PostAsync("/Recovery", "/Recovery", ("email", address));
            Assert.Contains("Falls für dieses Postfach eine bestätigte Ersatz-Adresse hinterlegt ist", web.LastPage);
        }

        await Task.Delay(1500);
        Assert.Empty(_server.Remote.Messages);
    }

    [Fact]
    public async Task Requests_are_limited_and_invalid_links_do_nothing()
    {
        Recovery.SetConfirmedAddress(_server.User("bob"), "bob@remote.test");
        using var web = new WebClient(_server.WebPort);
        for (var i = 0; i < 3; i++)
        {
            await web.PostAsync("/Recovery", "/Recovery", ("email", "bob@example.test"));
            Assert.Contains("unterwegs", web.LastPage);
        }

        await web.PostAsync("/Recovery", "/Recovery", ("email", "bob@example.test"));
        Assert.Contains("Zu viele Anfragen", web.LastPage);
        await TestServer.WaitUntilAsync(() => _server.Remote.Messages.Count == 3, "three reset mails");

        // Only the newest link is valid.
        var first = new Uri((await NextMailAsync(1)).Link).PathAndQuery;
        await web.GetAsync(first);
        Assert.Contains("ungültig oder abgelaufen", web.LastPage);
        var newest = new Uri((await NextMailAsync(3)).Link).PathAndQuery;
        await web.PostAsync(newest, "/Recovery/Reset", ("token", "erfunden"), ("password", "ganz-neues-passwort"),
            ("confirm", "ganz-neues-passwort"));
        Assert.Contains("ungültig oder abgelaufen", web.LastPage);
        Assert.NotNull(_server.HostAccounts.Authenticate("bob@example.test", TestServer.Password));
    }

    [Fact]
    public void Own_address_cannot_be_the_recovery_address() =>
        Assert.Throws<ArgumentException>(() => Recovery.SetAddress(_server.User("alice"), "alice@example.test"));

    [GeneratedRegex(@"https://\S+/Recovery/\w+\?token=[\w-]+")]
    private static partial Regex LinkPattern();
}
