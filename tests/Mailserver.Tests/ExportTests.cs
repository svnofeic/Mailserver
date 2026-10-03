using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Mailserver.Core;
using Mailserver.Core.Migration;
using Mailserver.Core.Storage;
using Mailserver.Migration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using MimeKit;
using MessageFlags = MailKit.MessageFlags;

namespace Mailserver.Tests;

/// <summary>Export from a running server (standing in for SmarterMail) to files, and restore into an empty data directory.</summary>
public sealed class MailboxExportTests : IAsyncLifetime
{
    private static readonly EmailAddress Alice = EmailAddress.Parse("alice@example.test");
    private static readonly DateTimeOffset OldDate = new(2021, 3, 14, 9, 30, 0, TimeSpan.FromHours(1));

    private TestServer _source = null!;
    private TestData _target = null!;
    private ImapSource _sourceAddress = null!;
    private string _exportDirectory = null!;

    public async Task InitializeAsync()
    {
        _source = await TestServer.StartAsync();
        _target = new TestData();
        _target.Accounts.AddDomain("example.test");
        _sourceAddress = new ImapSource("127.0.0.1", _source.ImapsPort, SecureSocketOptions.SslOnConnect, AcceptInvalidCertificate: true);
        _exportDirectory = Path.Combine(_target.Directory, "export");

        using var client = await ConnectSourceAsync();
        var root = client.GetFolder(client.PersonalNamespaces[0]);
        // Created on the server directly: over IMAP this server maps "Sent Items" to its own special folder.
        _source.HostMailboxes.CreateFolder(_source.User("alice").Id, "Sent Items");
        var sentItems = await client.GetFolderAsync("Sent Items");
        var projects = await client.Inbox.CreateAsync("Projekte: 2024?", true);

        await client.Inbox.AppendAsync(new AppendRequest(Message("Willkommen"), MessageFlags.Seen) { InternalDate = OldDate });
        await client.Inbox.AppendAsync(new AppendRequest(Message("Rechnung <Mai>/2024"), MessageFlags.Flagged));
        await client.Inbox.AppendAsync(new AppendRequest(Message("Schon gelöscht"), MessageFlags.Deleted));
        await sentItems.AppendAsync(new AppendRequest(Message("Antwort"), MessageFlags.Seen | MessageFlags.Answered));
        await projects.AppendAsync(new AppendRequest(Message("Projekt X"), MessageFlags.None) { Keywords = new HashSet<string> { "$Projekt" } });
    }

    public async Task DisposeAsync()
    {
        await _source.DisposeAsync();
        _target.Dispose();
    }

    [Fact]
    public async Task Exports_folders_as_eml_files_with_manifest()
    {
        var result = await new MailboxExporter().ExportAsync(_sourceAddress, Alice, TestServer.Password, _exportDirectory);

        Assert.Null(result.Error);
        Assert.Equal(5, result.Exported);
        var accountDirectory = Path.Combine(_exportDirectory, "alice@example.test");
        var manifest = ExportManifest.Load(accountDirectory)!;
        Assert.Equal("alice@example.test", manifest.Address);

        var inbox = manifest.Folders.Single(f => f.Name == "INBOX");
        Assert.Equal(3, inbox.Messages.Count);
        var welcome = inbox.Messages[0];
        Assert.Equal("000001 Willkommen.eml", welcome.File);
        Assert.Contains(@"\Seen", welcome.Flags);
        var path = Path.Combine(accountDirectory, "Mail", "INBOX", welcome.File);
        Assert.Equal(OldDate.UtcDateTime, File.GetLastWriteTimeUtc(path));

        // Byte-for-byte identical to the source.
        var expected = await File.ReadAllBytesAsync(_source.HostMailboxes.GetMessagePath(_source.Inbox("alice")[0]));
        Assert.Equal(expected, await File.ReadAllBytesAsync(path));

        // Characters that are not allowed in Windows file names are replaced.
        Assert.Equal("000002 Rechnung _Mai__2024.eml", inbox.Messages[1].File);
        Assert.Contains(@"\Deleted", inbox.Messages[2].Flags);
        var project = manifest.Folders.Single(f => f.Name.EndsWith("2024?"));
        Assert.Equal("INBOX/Projekte_ 2024_", project.Directory);
        Assert.True(File.Exists(Path.Combine(accountDirectory, "Mail", "INBOX", "Projekte_ 2024_", project.Messages[0].File)));
        Assert.Contains("$Projekt", project.Messages[0].Flags);
    }

    [Fact]
    public async Task Repeated_export_fetches_only_new_messages_and_updates_flags()
    {
        var exporter = new MailboxExporter();
        await exporter.ExportAsync(_sourceAddress, Alice, TestServer.Password, _exportDirectory);

        using (var client = await ConnectSourceAsync())
        {
            await client.Inbox.OpenAsync(FolderAccess.ReadWrite);
            await client.Inbox.AddFlagsAsync(new UniqueId(2), MessageFlags.Seen, silent: true);
            await client.Inbox.AppendAsync(new AppendRequest(Message("Neu"), MessageFlags.None));
        }

        var second = await exporter.ExportAsync(_sourceAddress, Alice, TestServer.Password, _exportDirectory);

        Assert.Equal(1, second.Exported);
        var inbox = ExportManifest.Load(second.Directory)!.Folders.Single(f => f.Name == "INBOX");
        Assert.Equal(4, inbox.Messages.Count);
        Assert.Contains(@"\Seen", inbox.Messages.Single(m => m.Uid == 2).Flags);
    }

    [Fact]
    public async Task Wrong_password_writes_nothing()
    {
        var result = await new MailboxExporter().ExportAsync(_sourceAddress, Alice, "falsch", _exportDirectory);

        Assert.NotNull(result.Error);
        Assert.False(Directory.Exists(result.Directory));
    }

    [Fact]
    public async Task Restore_recreates_folders_flags_and_dates()
    {
        var export = await new MailboxExporter().ExportAsync(_sourceAddress, Alice, TestServer.Password, _exportDirectory);
        var importer = new ExportImporter(_target.Accounts, _target.Mailboxes, new ImportLog(_target.Database));

        var found = Assert.Single(ExportImporter.FindAccounts(_exportDirectory));
        Assert.Equal(Alice, found.Address);
        var result = await importer.ImportAsync(found.Directory, "neues Passwort 123", dryRun: false);

        Assert.Null(result.Error);
        Assert.True(result.AccountCreated);
        Assert.Equal(4, result.Imported); // the message flagged \Deleted is left out
        var account = _target.Accounts.FindAccount(Alice)!;
        var inbox = Messages(account.Id, "INBOX");
        Assert.Equal(2, inbox.Count);
        Assert.True(inbox[0].HasFlag(Core.Storage.MessageFlags.Seen));
        Assert.Equal(OldDate.UtcDateTime, inbox[0].InternalDate.UtcDateTime);
        Assert.True(Assert.Single(Messages(account.Id, "Sent")).HasFlag(Core.Storage.MessageFlags.Answered));
        Assert.True(Assert.Single(Messages(account.Id, "INBOX/Projekte: 2024?")).HasFlag("$Projekt"));

        var again = await importer.ImportAsync(export.Directory, null, dryRun: false);
        Assert.Equal(0, again.Imported);
    }

    [Fact]
    public async Task Restore_skips_messages_already_taken_over_via_imap()
    {
        var export = await new MailboxExporter().ExportAsync(_sourceAddress, Alice, TestServer.Password, _exportDirectory);
        var log = new ImportLog(_target.Database);
        await new ImapImporter(_target.Accounts, _target.Mailboxes, log).ImportAsync(_sourceAddress, Alice, TestServer.Password, dryRun: false);

        var result = await new ExportImporter(_target.Accounts, _target.Mailboxes, log).ImportAsync(export.Directory, null, dryRun: false);

        Assert.Equal(0, result.Imported);
        Assert.Equal(2, Messages(_target.Accounts.FindAccount(Alice)!.Id, "INBOX").Count);
    }

    [Fact]
    public async Task Restore_without_password_does_not_create_account()
    {
        var export = await new MailboxExporter().ExportAsync(_sourceAddress, Alice, TestServer.Password, _exportDirectory);

        var result = await new ExportImporter(_target.Accounts, _target.Mailboxes, new ImportLog(_target.Database))
            .ImportAsync(export.Directory, null, dryRun: false);

        Assert.NotNull(result.Error);
        Assert.Null(_target.Accounts.FindAccount(Alice));
    }

    [Theory]
    [InlineData("Rechnung: 04/2024", "Rechnung_ 04_2024")]
    [InlineData("CON", "_CON")]
    [InlineData("  Punkt am Ende...  ", "Punkt am Ende")]
    [InlineData("", "_")]
    [InlineData("Grüße ✓", "Grüße ✓")]
    public void Sanitizes_file_names(string name, string expected) => Assert.Equal(expected, ExportNames.Sanitize(name));

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

/// <summary>CardDAV/CalDAV export against a minimal fake server shaped like common implementations.</summary>
public sealed class DavExportTests : IAsyncLifetime
{
    private const string Auth = "Basic YWxpY2VAZXhhbXBsZS50ZXN0OmdlaGVpbQ=="; // alice@example.test:geheim

    private const string TimeZone = "BEGIN:VTIMEZONE\r\nTZID:Europe/Berlin\r\nBEGIN:STANDARD\r\nDTSTART:19701025T030000\r\nTZOFFSETFROM:+0200\r\nTZOFFSETTO:+0100\r\nEND:STANDARD\r\nEND:VTIMEZONE\r\n";

    private WebApplication _app = null!;
    private Uri _server = null!;
    private string _directory = null!;
    private readonly List<string> _requests = [];

    public async Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), "mailserver-dav-" + Guid.NewGuid().ToString("N"));
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.Run(HandleAsync);
        await _app.StartAsync();
        _server = new Uri(_app.Urls.First() + "/");
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Discovers_and_exports_address_books_and_calendars()
    {
        var progress = new List<string>();
        var result = await new DavExporter().ExportAsync(_server, Alice, "geheim", _directory, progress: progress.Add);

        Assert.True(result.ServerFound);
        Assert.Empty(result.Warnings);
        Assert.Equal(2, result.Contacts);
        Assert.Equal(3, result.CalendarItems);

        var account = Path.Combine(_directory, "alice@example.test");
        var vcf = await File.ReadAllTextAsync(Path.Combine(account, "Kontakte", "Persönliche Kontakte.vcf"));
        Assert.Equal(2, CountOccurrences(vcf, "BEGIN:VCARD"));
        Assert.Contains("FN:Max Muster", vcf);

        // Calendar without REPORT support: read entry by entry and merged into one file.
        var ics = await File.ReadAllTextAsync(Path.Combine(account, "Kalender", "Arbeit.ics"));
        Assert.StartsWith("BEGIN:VCALENDAR\r\n", ics);
        Assert.EndsWith("END:VCALENDAR\r\n", ics);
        Assert.Equal(1, CountOccurrences(ics, "BEGIN:VTIMEZONE"));
        Assert.Equal(2, CountOccurrences(ics, "BEGIN:VEVENT"));
        Assert.Equal(1, CountOccurrences(ics, "BEGIN:VTODO"));
        Assert.Equal(1, CountOccurrences(ics, "BEGIN:VALARM"));
        Assert.Contains("SUMMARY:Besprechung", ics);

        // The credentials survived the redirect from /.well-known.
        Assert.Contains("PROPFIND /.well-known/caldav", _requests);
        Assert.Contains("PROPFIND /dav/", _requests);
    }

    [Fact]
    public async Task Wrong_password_reports_no_server()
    {
        var result = await new DavExporter().ExportAsync(_server, Alice, "falsch", _directory);

        Assert.False(result.ServerFound);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void Merges_calendars_with_each_time_zone_once()
    {
        var merged = DavExporter.MergeCalendars([Calendar("1", "A"), Calendar("2", "B").Replace("\r\n", "\n")]);

        Assert.Equal(1, CountOccurrences(merged, "BEGIN:VTIMEZONE"));
        Assert.Equal(2, CountOccurrences(merged, "BEGIN:VEVENT"));
        Assert.Equal(1, CountOccurrences(merged, "BEGIN:VCALENDAR"));
        Assert.DoesNotContain("PRODID:-//Fake", merged);
        Assert.DoesNotContain("\r\r", merged);
    }

    private static readonly EmailAddress Alice = EmailAddress.Parse("alice@example.test");

    private static int CountOccurrences(string text, string value) => (text.Length - text.Replace(value, "").Length) / value.Length;

    private static string Calendar(string uid, string summary, string component = "VEVENT") =>
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Fake//DE\r\n{TimeZone}BEGIN:{component}\r\nUID:{uid}\r\nDTSTART;TZID=Europe/Berlin:20240101T100000\r\nSUMMARY:{summary}\r\n" +
        (component == "VEVENT" && uid == "1" ? "BEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER:-PT15M\r\nEND:VALARM\r\n" : "") +
        $"END:{component}\r\nEND:VCALENDAR\r\n";

    private async Task HandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";
        _requests.Add($"{context.Request.Method} {path}");
        if (context.Request.Headers.Authorization != Auth)
        {
            context.Response.StatusCode = 401;
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"dav\"";
            return;
        }

        switch (context.Request.Method, path)
        {
            case ("PROPFIND", "/.well-known/caldav"):
                context.Response.StatusCode = 301;
                context.Response.Headers.Location = "/dav/";
                return;
            case ("PROPFIND", "/dav/"):
                await MultiStatus(context, Response("/dav/", "<d:current-user-principal><d:href>/dav/principals/alice/</d:href></d:current-user-principal>"));
                return;
            case ("PROPFIND", "/dav/principals/alice/"):
                await MultiStatus(context, Response("/dav/principals/alice/",
                    "<card:addressbook-home-set><d:href>/dav/cards/alice/</d:href></card:addressbook-home-set>" +
                    "<cal:calendar-home-set><d:href>/dav/cal/alice/</d:href></cal:calendar-home-set>"));
                return;
            case ("PROPFIND", "/dav/cards/alice/"):
                await MultiStatus(context,
                    Response("/dav/cards/alice/", "<d:resourcetype><d:collection/></d:resourcetype>"),
                    Response("/dav/cards/alice/default/", "<d:resourcetype><d:collection/><card:addressbook/></d:resourcetype><d:displayname>Persönliche Kontakte</d:displayname>"));
                return;
            case ("REPORT", "/dav/cards/alice/default/"):
                await MultiStatus(context,
                    Response("/dav/cards/alice/default/1.vcf", "<card:address-data>BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Max Muster\r\nEND:VCARD\r\n</card:address-data>"),
                    Response("/dav/cards/alice/default/2.vcf", "<card:address-data>BEGIN:VCARD\nVERSION:3.0\nFN:Erika Muster\nEND:VCARD</card:address-data>"));
                return;
            case ("PROPFIND", "/dav/cal/alice/"):
                await MultiStatus(context,
                    Response("/dav/cal/alice/", "<d:resourcetype><d:collection/></d:resourcetype>"),
                    Response("/dav/cal/alice/work/", "<d:resourcetype><d:collection/><cal:calendar/></d:resourcetype><d:displayname>Arbeit</d:displayname>"),
                    Response("/dav/cal/alice/inbox/", "<d:resourcetype><d:collection/><cal:schedule-inbox/></d:resourcetype>"));
                return;
            case ("REPORT", "/dav/cal/alice/work/"):
                context.Response.StatusCode = 501;
                return;
            case ("PROPFIND", "/dav/cal/alice/work/"):
                await MultiStatus(context,
                    Response("/dav/cal/alice/work/", "<d:resourcetype><d:collection/></d:resourcetype>"),
                    Response("/dav/cal/alice/work/1.ics", "<d:resourcetype/><d:getcontenttype>text/calendar; charset=utf-8</d:getcontenttype>"),
                    Response("/dav/cal/alice/work/2.ics", "<d:resourcetype/><d:getcontenttype>text/calendar</d:getcontenttype>"),
                    Response("/dav/cal/alice/work/3.ics", "<d:resourcetype/>"));
                return;
            case ("GET", "/dav/cal/alice/work/1.ics"):
                await context.Response.WriteAsync(Calendar("1", "Besprechung"));
                return;
            case ("GET", "/dav/cal/alice/work/2.ics"):
                await context.Response.WriteAsync(Calendar("2", "Urlaub"));
                return;
            case ("GET", "/dav/cal/alice/work/3.ics"):
                await context.Response.WriteAsync(Calendar("3", "Steuer", "VTODO"));
                return;
            default:
                context.Response.StatusCode = 404;
                return;
        }
    }

    private static string Response(string href, string props) =>
        $"<d:response><d:href>{href}</d:href><d:propstat><d:prop>{props}</d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>" +
        "<d:propstat><d:prop><d:getetag/></d:prop><d:status>HTTP/1.1 404 Not Found</d:status></d:propstat></d:response>";

    private static async Task MultiStatus(HttpContext context, params string[] responses)
    {
        context.Response.StatusCode = 207;
        context.Response.ContentType = "application/xml; charset=utf-8";
        await context.Response.WriteAsync(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\" xmlns:card=\"urn:ietf:params:xml:ns:carddav\" " +
            $"xmlns:cal=\"urn:ietf:params:xml:ns:caldav\">{string.Concat(responses)}</d:multistatus>", Encoding.UTF8);
    }
}
