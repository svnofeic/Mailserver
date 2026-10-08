using System.Net;
using Mailserver.Core;
using Mailserver.Core.Accounts;
using Mailserver.Core.Data;
using Mailserver.Core.External;
using Mailserver.Core.Security;
using Mailserver.Core.Storage;
using Mailserver.Smtp.External;
using Mailserver.Web.Webmail;
using Microsoft.Extensions.DependencyInjection;

namespace Mailserver.Tests;

/// <summary>
/// Addresses at other providers: a second test server plays the provider (carol@provider.test), the first one fetches
/// from it and sends through it.
/// </summary>
public sealed class ExternalAccountTests : IAsyncLifetime
{
    private const string Carol = "carol@provider.test";
    private TestServer _home = null!;
    private TestServer _provider = null!;
    private Account _carol = null!;

    public async Task InitializeAsync()
    {
        _provider = await TestServer.StartAsync();
        _provider.HostAccounts.AddDomain("provider.test");
        _carol = _provider.HostAccounts.AddAccount(EmailAddress.Parse(Carol), TestServer.Password);
        _provider.HostMailboxes.EnsureDefaultFolders(_carol.Id);
        _home = await TestServer.StartAsync();
        // The provider's test certificate is self-signed and made out to mail.example.test.
        _home.Services.GetRequiredService<ExternalMailClient>().CertificateValidation = (_, _, _, _) => true;
    }

    public async Task DisposeAsync()
    {
        await _home.DisposeAsync();
        await _provider.DisposeAsync();
    }

    private ExternalAccountStore Store => _home.Services.GetRequiredService<ExternalAccountStore>();
    private ExternalMailClient Client => _home.Services.GetRequiredService<ExternalMailClient>();

    private ExternalAccountSettings Settings(string folder = "Provider") => new(Carol, folder,
        new MailServerAddress("127.0.0.1", _provider.ImapsPort, MailSecurity.Ssl),
        new MailServerAddress("127.0.0.1", _provider.SubmissionPort, MailSecurity.StartTls), Carol);

    private Task DeliverToCarolAsync(string subject, string flags = "") => _provider.HostMailboxes.AppendAsync(_carol,
        System.Text.Encoding.ASCII.GetBytes($"From: dave@elsewhere.test\r\nTo: {Carol}\r\nSubject: {subject}\r\nMessage-ID: <{Guid.NewGuid():N}@elsewhere.test>\r\n\r\nHallo\r\n"),
        MailboxStore.Inbox, flags);

    private IReadOnlyList<StoredMessage> Folder(string name)
    {
        var folder = _home.HostMailboxes.GetFolder(_home.User("alice").Id, name);
        return folder is null ? [] : _home.HostMailboxes.ListMessages(folder.Id);
    }

    [Fact]
    public async Task Fetches_only_new_mail_into_its_folder_and_leaves_it_at_the_provider()
    {
        await DeliverToCarolAsync("Schon da");
        Assert.Null(await Client.TestAsync(Settings(), TestServer.Password, CancellationToken.None));
        var account = Store.Add(_home.User("alice"), Settings(), TestServer.Password, fetchExisting: false);

        // The first run only notes where the provider's inbox stands.
        Assert.Equal(new FetchResult(0, null), await Client.FetchAsync(account, CancellationToken.None));
        Assert.Empty(Folder("Provider"));

        await DeliverToCarolAsync("Neu 1");
        await DeliverToCarolAsync("Neu 2", MessageFlags.Seen);
        Assert.Equal(new FetchResult(2, null), await Client.FetchAsync(account, CancellationToken.None));
        Assert.Equal(new FetchResult(0, null), await Client.FetchAsync(account, CancellationToken.None));

        var fetched = Folder("Provider");
        Assert.Equal(2, fetched.Count);
        Assert.False(fetched[0].HasFlag(MessageFlags.Seen));
        Assert.True(fetched[1].HasFlag(MessageFlags.Seen));
        var text = await _home.ReadAsync(fetched[0]);
        Assert.StartsWith("Received: from 127.0.0.1 by mail.example.test with IMAP", text);
        Assert.Contains("Subject: Neu 1", text);

        // The provider keeps its copies.
        Assert.Equal(3, ProviderInbox().Count);
        var saved = Store.Find(account.Id)!;
        Assert.Equal(2, saved.Fetched);
        Assert.NotNull(saved.LastFetch);
        Assert.Null(saved.LastError);
    }

    private IReadOnlyList<StoredMessage> ProviderInbox() =>
        _provider.HostMailboxes.ListMessages(_provider.HostMailboxes.GetFolder(_carol.Id, MailboxStore.Inbox)!.Id);

    [Fact]
    public async Task Fetches_existing_mail_when_asked()
    {
        await DeliverToCarolAsync("Alt 1");
        await DeliverToCarolAsync("Alt 2");
        var account = Store.Add(_home.User("alice"), Settings(MailboxStore.Inbox), TestServer.Password, fetchExisting: true);

        Assert.Equal(new FetchResult(2, null), await Client.FetchAsync(account, CancellationToken.None));
        Assert.Equal(2, _home.Inbox("alice").Count);
    }

    [Fact]
    public async Task Wrong_password_is_reported_and_stored()
    {
        var error = await Client.TestAsync(Settings(), "falsch", CancellationToken.None);
        Assert.StartsWith("IMAP (127.0.0.1:", error);
        Assert.Contains("Anmeldung abgelehnt", error);

        var account = Store.Add(_home.User("alice"), Settings(), "falsch", fetchExisting: false);
        var result = await Client.FetchAsync(account, CancellationToken.None);
        Assert.Contains("Anmeldung abgelehnt", result.Error);
        Assert.Contains("Anmeldung abgelehnt", Store.Find(account.Id)!.LastError);
    }

    [Fact]
    public async Task Webmail_sends_through_the_provider()
    {
        Store.Add(_home.User("alice"), Settings(), TestServer.Password, fetchExisting: false);
        var sender = _home.Services.GetRequiredService<WebmailSender>();
        Assert.Contains(Carol, sender.SenderAddresses(_home.User("alice")));

        await sender.SendAsync(_home.User("alice"), new Draft(Carol, "bob@example.test", null, "versteckt@remote.test", "Über den Anbieter", "Hallo Bob", []),
            IPAddress.Loopback, CancellationToken.None);

        // Delivered by the provider (bob@example.test is local there), not by the home server.
        await TestServer.WaitUntilAsync(() => _provider.Inbox("bob").Count == 1, "delivery at the provider");
        var delivered = await _provider.ReadAsync(_provider.Inbox("bob").Single());
        Assert.Contains($"From: {Carol}", delivered);
        Assert.DoesNotContain("versteckt", delivered);
        await TestServer.WaitUntilAsync(() => _provider.Remote.Messages.Any(m => m.To.Contains("versteckt@remote.test")), "Bcc at the provider");
        Assert.Empty(_home.Inbox("bob"));
        Assert.Contains("Hallo Bob", await _home.ReadAsync(Folder("Sent").Single()));

        // Not in the list: the home server refuses to send as that address.
        await Assert.ThrowsAsync<ComposeException>(() => sender.SendAsync(_home.User("bob"),
            new Draft(Carol, "alice@example.test", null, null, "Fremd", "x", []), IPAddress.Loopback, CancellationToken.None));
    }

    [Fact]
    public async Task Web_page_checks_saves_and_fetches()
    {
        using var web = new WebClient(_home.WebPort);
        await web.LoginAsync("alice@example.test", TestServer.Password);
        (string, string)[] Fields(string password) =>
        [
            ("Form.Address", Carol), ("Form.Folder", "Provider"), ("Form.ImapHost", "127.0.0.1"), ("Form.ImapPort", _provider.ImapsPort.ToString()),
            ("Form.ImapSecurity", "Ssl"), ("Form.Password", password), ("Form.Send", "true"), ("Form.SmtpHost", "127.0.0.1"),
            ("Form.SmtpPort", _provider.SubmissionPort.ToString()), ("Form.SmtpSecurity", "StartTls"),
        ];

        await web.PostAsync("/Account/External", "/Account/External?handler=Save", Fields("falsch"));
        Assert.Contains("Anmeldung abgelehnt", web.LastPage);
        Assert.Empty(Store.List(_home.User("alice").Id));

        await web.PostAsync("/Account/External", "/Account/External?handler=Save", Fields(TestServer.Password));
        Assert.Contains("eingerichtet", web.LastPage);
        var account = Assert.Single(Store.List(_home.User("alice").Id));
        Assert.Equal(TestServer.Password, Store.Password(account.Id));

        await DeliverToCarolAsync("Per Knopf");
        await web.PostAsync("/Account/External", "/Account/External?handler=Fetch", ("id", account.Id.ToString()));
        Assert.Contains("1 neue Mail abgerufen", web.LastPage);
        Assert.Single(Folder("Provider"));

        // Offered as sender, and preselected when answering mail in its folder.
        await web.GetAsync($"/Mail/Compose?mode=reply&folder=Provider&uid={Folder("Provider").Single().Uid}");
        Assert.Contains($"<option value=\"{Carol}\" selected=\"selected\">", web.LastPage);

        // Another user cannot touch it.
        using var bob = new WebClient(_home.WebPort);
        await bob.LoginAsync("bob@example.test", TestServer.Password);
        var response = await bob.PostAsync("/Account/External", "/Account/External?handler=Fetch", ("id", account.Id.ToString()));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await web.PostAsync("/Account/External", "/Account/External?handler=Remove", ("id", account.Id.ToString()));
        Assert.Empty(Store.List(_home.User("alice").Id));
        Assert.Single(Folder("Provider"));
    }
}

public sealed class ExternalAccountStoreTests : IDisposable
{
    private readonly TestData _data = new();
    private readonly ExternalAccountStore _store;
    private readonly Account _alice;

    public ExternalAccountStoreTests()
    {
        _data.Accounts.AddDomain("example.test");
        _alice = _data.Accounts.AddAccount(EmailAddress.Parse("alice@example.test"), "irrelevant password");
        _store = new ExternalAccountStore(_data.Database, _data.Accounts, new SecretProtector(_data.Paths), TimeProvider.System);
    }

    public void Dispose() => _data.Dispose();

    private static ExternalAccountSettings Settings(string address, string folder = "") =>
        new(address, folder, new MailServerAddress("imap.gmx.net", 993, MailSecurity.Ssl), null, "");

    [Fact]
    public void Password_is_stored_encrypted()
    {
        var account = _store.Add(_alice, Settings("anna@gmx.de"), "Sehr-Geheim-123", fetchExisting: false);

        using var connection = _data.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT password FROM external_accounts";
        var stored = (string)command.ExecuteScalar()!;
        Assert.DoesNotContain("Geheim", stored);
        Assert.Equal("Sehr-Geheim-123", _store.Password(account.Id));
        // A new instance (e.g. after a restart or restore) reads the key from data\secrets.
        Assert.Equal("Sehr-Geheim-123", new SecretProtector(_data.Paths).Unprotect(stored));
        Assert.Null(new SecretProtector(new DataPaths(Path.Combine(_data.Directory, "other"))).Unprotect(stored));
    }

    [Fact]
    public void Defaults_and_checks()
    {
        var account = _store.Add(_alice, Settings(" Anna@GMX.de "), "pw", fetchExisting: false);
        Assert.Equal("Anna@gmx.de", account.Address, ignoreCase: true);
        Assert.Equal(account.Address, account.Settings.Folder);
        Assert.Equal(account.Address, account.Settings.UserName);
        Assert.Null(account.LastUid);
        Assert.False(account.CanSend);

        Assert.Contains("verwaltet", Assert.Throws<ArgumentException>(() => _store.Add(_alice, Settings("bob@example.test"), "pw", false)).Message);
        Assert.Contains("bereits", Assert.Throws<ArgumentException>(() => _store.Add(_alice, Settings("anna@gmx.de"), "pw", false)).Message);
        Assert.Contains("Ordner", Assert.Throws<ArgumentException>(() => _store.Add(_alice, Settings("x@gmx.de", "Trash"), "pw", false)).Message);
        Assert.Contains("Passwort", Assert.Throws<ArgumentException>(() => _store.Add(_alice, Settings("y@gmx.de"), "", false)).Message);

        // Another inbox at the provider starts over; the password stays when left empty.
        _store.SavePosition(account.Id, 7, 42, 3);
        var moved = _store.Update(_alice, account.Id, Settings("anna@gmx.de") with { Imap = new MailServerAddress("imap.web.de", 993, MailSecurity.Ssl) }, null);
        Assert.Null(moved.LastUid);
        Assert.Equal("pw", _store.Password(account.Id));

        _data.Accounts.RemoveAccount(_alice.Address);
        Assert.Null(_store.Find(account.Id));
    }
}
