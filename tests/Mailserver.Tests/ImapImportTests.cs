using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Mailserver.Core;
using Mailserver.Core.Migration;
using Mailserver.Core.Storage;
using Mailserver.Migration;
using MimeKit;
using MessageFlags = MailKit.MessageFlags;

namespace Mailserver.Tests;

/// <summary>Imports from a running server (standing in for SmarterMail) into a separate, empty data directory.</summary>
public sealed class ImapImportTests : IAsyncLifetime
{
    private static readonly EmailAddress Alice = EmailAddress.Parse("alice@example.test");
    private static readonly DateTimeOffset OldDate = new(2021, 3, 14, 9, 30, 0, TimeSpan.FromHours(1));

    private TestServer _source = null!;
    private TestData _target = null!;
    private ImapImporter _importer = null!;
    private ImapSource _sourceAddress = null!;

    public async Task InitializeAsync()
    {
        _source = await TestServer.StartAsync();
        _target = new TestData();
        _target.Accounts.AddDomain("example.test");
        _importer = new ImapImporter(_target.Accounts, _target.Mailboxes, new ImportLog(_target.Database));
        _sourceAddress = new ImapSource("127.0.0.1", _source.ImapsPort, SecureSocketOptions.SslOnConnect, AcceptInvalidCertificate: true);

        // Folder layout as SmarterMail creates it.
        using var client = await ConnectSourceAsync();
        var root = client.GetFolder(client.PersonalNamespaces[0]);
        // Created on the server directly: over IMAP this server maps "Sent Items" to its own special folder.
        _source.HostMailboxes.CreateFolder(_source.User("alice").Id, "Sent Items");
        var sentItems = await client.GetFolderAsync("Sent Items");
        // Created on the server directly: over IMAP this server maps "Deleted Items" to its own special folder.
        _source.HostMailboxes.CreateFolder(_source.User("alice").Id, "Deleted Items");
        var deletedItems = await client.GetFolderAsync("Deleted Items");
        var projects = await client.Inbox.CreateAsync("Projekte", true);

        await client.Inbox.AppendAsync(new AppendRequest(Message("Willkommen"), MessageFlags.Seen) { InternalDate = OldDate });
        await client.Inbox.AppendAsync(new AppendRequest(Message("Wichtig"), MessageFlags.Flagged));
        await client.Inbox.AppendAsync(new AppendRequest(Message("Schon gelöscht"), MessageFlags.Deleted));
        await sentItems.AppendAsync(new AppendRequest(Message("Antwort"), MessageFlags.Seen | MessageFlags.Answered));
        await deletedItems.AppendAsync(new AppendRequest(Message("Alt"), MessageFlags.Seen));
        await projects.AppendAsync(new AppendRequest(Message("Projekt X"), MessageFlags.None) { Keywords = new HashSet<string> { "$Projekt" } });
    }

    public async Task DisposeAsync()
    {
        await _source.DisposeAsync();
        _target.Dispose();
    }

    [Fact]
    public async Task Imports_folders_flags_dates_and_exact_bytes()
    {
        var result = await _importer.ImportAsync(_sourceAddress, Alice, TestServer.Password, dryRun: false);

        Assert.Null(result.Error);
        Assert.True(result.AccountCreated);
        Assert.Equal(5, result.Imported);
        Assert.Equal(0, result.Failed);

        // The password from the source works here too.
        Assert.NotNull(_target.Accounts.Authenticate("alice@example.test", TestServer.Password));

        var account = _target.Accounts.FindAccount(Alice)!;
        var inbox = Messages(account.Id, "INBOX");
        Assert.Equal(2, inbox.Count);
        Assert.True(inbox[0].HasFlag(Core.Storage.MessageFlags.Seen));
        Assert.Equal(OldDate.UtcDateTime, inbox[0].InternalDate.UtcDateTime);
        Assert.True(inbox[1].HasFlag(Core.Storage.MessageFlags.Flagged));

        var sent = Assert.Single(Messages(account.Id, "Sent"));
        Assert.True(sent.HasFlag(Core.Storage.MessageFlags.Answered));
        Assert.Single(Messages(account.Id, "Trash"));
        var project = Assert.Single(Messages(account.Id, "INBOX/Projekte"));
        Assert.True(project.HasFlag("$Projekt"));

        // Byte-for-byte identical to the source (so DKIM signatures of received mail stay valid).
        var sourceMessage = _source.Inbox("alice")[0];
        var expected = await File.ReadAllBytesAsync(_source.HostMailboxes.GetMessagePath(sourceMessage));
        Assert.Equal(expected, await File.ReadAllBytesAsync(_target.Mailboxes.GetMessagePath(inbox[0])));
    }

    [Fact]
    public async Task Repeated_import_only_copies_new_messages()
    {
        await _importer.ImportAsync(_sourceAddress, Alice, TestServer.Password, dryRun: false);

        var second = await _importer.ImportAsync(_sourceAddress, Alice, TestServer.Password, dryRun: false);
        Assert.Equal(0, second.Imported);
        Assert.False(second.AccountCreated);

        using (var client = await ConnectSourceAsync())
        {
            await client.Inbox.AppendAsync(new AppendRequest(Message("Kurz vor der Umstellung"), MessageFlags.None));
        }

        var third = await _importer.ImportAsync(_sourceAddress, Alice, TestServer.Password, dryRun: false);
        Assert.Equal(1, third.Imported);
        Assert.Equal(3, Messages(_target.Accounts.FindAccount(Alice)!.Id, "INBOX").Count);
    }

    [Fact]
    public async Task Dry_run_writes_nothing()
    {
        var result = await _importer.ImportAsync(_sourceAddress, Alice, TestServer.Password, dryRun: true);

        Assert.Null(result.Error);
        // The source here is this server, which merges "Sent Items" into Sent at login; SmarterMail would report "Sent Items".
        Assert.Contains(result.Folders, f => f.TargetFolder == "Sent" && f.Total == 1);
        Assert.Null(_target.Accounts.FindAccount(Alice));
    }

    [Fact]
    public async Task Wrong_password_creates_nothing()
    {
        var result = await _importer.ImportAsync(_sourceAddress, Alice, "falsch", dryRun: false);

        Assert.NotNull(result.Error);
        Assert.Null(_target.Accounts.FindAccount(Alice));
    }

    [Theory]
    [InlineData("Sent Items", '/', "Sent")]
    [InlineData("Deleted Items", '/', "Trash")]
    [InlineData("Junk E-Mail", '/', "Junk")]
    [InlineData("Gesendete Elemente", '.', "Sent")]
    [InlineData("Inbox/Kunden/Müller", '/', "INBOX/Kunden/Müller")]
    [InlineData("INBOX.Kunden.A/B", '.', "INBOX/Kunden/A-B")]
    [InlineData("Belege", '/', "Belege")]
    public void Maps_folder_names(string source, char separator, string expected) =>
        Assert.Equal(expected, ImapImporter.MapFolderPath(source, separator));

    private List<StoredMessage> Messages(long accountId, string folder) =>
        _target.Mailboxes.ListMessages(_target.Mailboxes.GetFolder(accountId, folder)!.Id).ToList();

    private async Task<ImapClient> ConnectSourceAsync()
    {
        var client = new ImapClient { ServerCertificateValidationCallback = (_, _, _, _) => true };
        await client.ConnectAsync("127.0.0.1", _source.ImapsPort, SecureSocketOptions.SslOnConnect);
        await client.AuthenticateAsync("alice@example.test", TestServer.Password);
        return client;
    }

    private static MimeMessage Message(string subject)
    {
        var message = new MimeMessage { Subject = subject, Body = new TextPart("plain") { Text = $"Inhalt: {subject}" } };
        message.From.Add(MailboxAddress.Parse("someone@remote.test"));
        message.To.Add(MailboxAddress.Parse("alice@example.test"));
        return message;
    }
}
