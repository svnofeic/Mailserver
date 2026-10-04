using System.Text;
using Mailserver.Core;
using Mailserver.Core.Accounts;
using Mailserver.Core.Backup;
using Mailserver.Core.Storage;
using Mailserver.Smtp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Mailserver.Tests;

public sealed class BackupTests : IDisposable
{
    private readonly TestData _data = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 5, 1, 0, 0, TimeSpan.Zero));
    private readonly string _target = Path.Combine(Path.GetTempPath(), "mailserver-tests", "backup-" + Guid.NewGuid().ToString("N"));
    private readonly MailserverOptions _options = new();
    private readonly BackupManager _backups;
    private readonly Account _alice;

    public BackupTests()
    {
        _options.Backup = new BackupOptions { Enabled = true, Directory = _target, KeepDays = 7 };
        _backups = new BackupManager(_data.Database, _data.Paths, Options.Create(_options), _time, NullLogger<BackupManager>.Instance);
        _data.Accounts.AddDomain("example.test");
        _alice = _data.Accounts.AddAccount(EmailAddress.Parse("alice@example.test"), "irrelevant-passwort");
        _data.Mailboxes.EnsureDefaultFolders(_alice.Id);
        Directory.CreateDirectory(Path.Combine(_data.Directory, "dkim"));
        File.WriteAllText(Path.Combine(_data.Directory, "dkim", "example.test.mail.pem"), "KEY");
        Directory.CreateDirectory(Path.Combine(_data.Directory, "keys"));
        File.WriteAllText(Path.Combine(_data.Directory, "keys", "key-1.xml"), "machine bound");
    }

    public void Dispose()
    {
        _data.Dispose();
        try
        {
            Directory.Delete(_target, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private Task<StoredMessage> AppendAsync(string subject) =>
        _data.Mailboxes.AppendAsync(_alice, Encoding.ASCII.GetBytes($"Subject: {subject}\r\n\r\n{subject}\r\n"));

    [Fact]
    public async Task Backs_up_database_configuration_and_mails_but_not_the_machine_keys()
    {
        var message = await AppendAsync("Erste Mail");

        var run = await _backups.RunAsync();

        Assert.True(run.Success, run.Message);
        var snapshot = Assert.Single(BackupManager.ListSnapshots(_target));
        Assert.True(File.Exists(Path.Combine(snapshot.Path, "mailserver.db")));
        Assert.Equal("KEY", File.ReadAllText(Path.Combine(snapshot.Path, "dkim", "example.test.mail.pem")));
        Assert.False(Directory.Exists(Path.Combine(snapshot.Path, "keys")));
        var manifest = BackupManager.ReadManifest(snapshot.Path)!;
        Assert.Equal((1, 1L), (manifest.Accounts, manifest.Messages));
        Assert.True(File.Exists(Path.Combine(_target, "mail", message.FileName)));
        Assert.Equal(run.Snapshot, snapshot.Name);
    }

    [Fact]
    public async Task Copies_only_new_mails_the_next_time()
    {
        await AppendAsync("Eins");
        await AppendAsync("Zwei");
        var first = await _backups.RunAsync();

        _time.Now = _time.Now.AddDays(1);
        await AppendAsync("Drei");
        var second = await _backups.RunAsync();

        // Snapshot files as before plus the new mail – the two old mails are not copied again.
        Assert.Equal(first.FilesCopied - 1, second.FilesCopied);
        Assert.Equal(2, BackupManager.ListSnapshots(_target).Count);
    }

    [Fact]
    public async Task Keeps_deleted_mails_and_old_snapshots_for_the_retention_period()
    {
        var message = await AppendAsync("Wird gelöscht");
        await _backups.RunAsync();
        var inbox = _data.Mailboxes.GetFolder(_alice.Id, MailboxStore.Inbox)!;
        _data.Mailboxes.UpdateFlags(inbox.Id, [message.Uid], FlagOperation.Add, [MessageFlags.Deleted]);
        _data.Mailboxes.Expunge(inbox.Id);
        var mirrored = Path.Combine(_target, "mail", message.FileName);

        for (var day = 1; day <= 7; day++)
        {
            _time.Now = _time.Now.AddDays(1);
            Assert.True((await _backups.RunAsync()).Success);
            Assert.True(File.Exists(mirrored), $"day {day}");
        }

        Assert.Equal(8, BackupManager.ListSnapshots(_target).Count);
        _time.Now = _time.Now.AddDays(2);
        await _backups.RunAsync();

        Assert.False(File.Exists(mirrored));
        var snapshots = BackupManager.ListSnapshots(_target);
        Assert.All(snapshots, s => Assert.True(_time.Now - s.Created <= TimeSpan.FromDays(7)));
    }

    [Fact]
    public async Task Always_keeps_the_newest_snapshot()
    {
        await _backups.RunAsync();
        _time.Now = _time.Now.AddDays(30);

        Assert.True((await _backups.RunAsync()).Success);
        Assert.Single(BackupManager.ListSnapshots(_target));
    }

    [Fact]
    public async Task Restores_into_an_empty_data_directory()
    {
        var message = await AppendAsync("Wichtige Mail");
        _data.Accounts.AddAccount(EmailAddress.Parse("bob@example.test"), "irrelevant-passwort");
        await _backups.RunAsync();

        using var fresh = new TestData();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var result = BackupManager.Restore(BackupManager.ListSnapshots(_target)[0].Path, fresh.Paths);

        Assert.True(result.MirrorFound);
        var database = new Mailserver.Core.Data.Database(fresh.Paths);
        database.Migrate();
        var accounts = new AccountStore(database);
        Assert.Equal(["alice@example.test", "bob@example.test"], accounts.ListAccounts().Select(a => a.Address.ToString()).Order());
        var mailboxes = new MailboxStore(database, fresh.Paths);
        var restored = Assert.Single(mailboxes.ListMessages(mailboxes.GetFolder(accounts.FindAccount(_alice.Address)!.Id, MailboxStore.Inbox)!.Id));
        Assert.Contains("Wichtige Mail", File.ReadAllText(mailboxes.GetMessagePath(restored)));
        Assert.Equal(message.FileName, restored.FileName);
        Assert.Equal("KEY", File.ReadAllText(Path.Combine(fresh.Directory, "dkim", "example.test.mail.pem")));
    }

    [Fact]
    public async Task Reports_unusable_targets()
    {
        _options.Backup.Directory = Path.Combine(_data.Directory, "sicherung");
        var run = await _backups.RunAsync();

        Assert.False(run.Success);
        Assert.Contains("nicht im Datenordner", run.Message);
        Assert.Equal(run.Message, _backups.RecentRuns().First().Message);
        Assert.Null(_backups.LastSuccess());
        Assert.Contains("vollständiger Pfad", new BackupManager(_data.Database, _data.Paths,
            Options.Create(new MailserverOptions { Backup = new BackupOptions { Directory = "relativ" } }), _time,
            NullLogger<BackupManager>.Instance).Problem());
    }

    [Theory]
    [InlineData("2026-10-05T02:00", null, null, true)]            // never backed up
    [InlineData("2026-10-05T02:00", "2026-10-04T03:00", null, false)] // yesterday's done, today's not due yet
    [InlineData("2026-10-05T03:30", "2026-10-04T03:00", null, true)]  // today's due
    [InlineData("2026-10-05T09:00", "2026-10-04T03:00", null, true)]  // server was off at 3:00 – catch up
    [InlineData("2026-10-05T04:00", "2026-10-04T03:00", "2026-10-05T03:00", false)] // failed an hour ago: wait
    [InlineData("2026-10-05T06:30", "2026-10-04T03:00", "2026-10-05T03:00", true)]  // retry after three hours
    public void Schedule(string now, string? lastSuccess, string? lastFailure, bool due)
    {
        static DateTimeOffset Local(string value) => new(DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture), TimeSpan.Zero);
        var success = lastSuccess is null ? null : new BackupRun(1, Local(lastSuccess), Local(lastSuccess), true, null, 0, 0, null);
        var failure = lastFailure is null ? null : new BackupRun(2, Local(lastFailure), Local(lastFailure), false, null, 0, 0, null);

        Assert.Equal(due, BackupSchedule.IsDue(Local(now), new TimeSpan(3, 0, 0), success, failure ?? success));
    }
}

public sealed class BackupWebTests : IAsyncLifetime
{
    private const string Admin = "chef@example.test";
    private readonly string _target = Path.Combine(Path.GetTempPath(), "mailserver-tests", "backup-" + Guid.NewGuid().ToString("N"));
    private TestServer _server = null!;
    private WebClient _web = null!;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync();
        var admin = _server.HostAccounts.AddAccount(EmailAddress.Parse(Admin), TestServer.Password);
        _server.HostAccounts.SetAdmin(admin.Address, true);
        _web = new WebClient(_server.WebPort);
        await _web.LoginAsync(Admin, TestServer.Password);
    }

    public async Task DisposeAsync()
    {
        _web.Dispose();
        await _server.DisposeAsync();
        try
        {
            Directory.Delete(_target, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Admin_configures_the_backup_and_runs_it_now()
    {
        await _web.GetAsync("/Admin/Backup");
        Assert.Contains("noch keine Sicherung", _web.LastPage);

        await _web.PostAsync("/Admin/Backup", "/Admin/Backup?handler=Run", ("Form.Enabled", "true"), ("Form.Enabled", "false"),
            ("Form.Directory", _target), ("Form.Time", "02:30"), ("Form.KeepDays", "10"), ("Form.Username", ""), ("Form.Password", ""));
        Assert.Contains("Die Sicherung läuft", _web.LastPage);

        var backups = _server.Services.GetRequiredService<BackupManager>();
        await TestServer.WaitUntilAsync(() => backups.LastSuccess() is not null, "backup finished");
        var live = _server.Services.GetRequiredService<IOptions<MailserverOptions>>();
        await TestServer.WaitUntilAsync(() => live.Value.Backup.Enabled, "reloaded settings");
        var options = live.Value.Backup;
        Assert.Equal((true, new TimeSpan(2, 30, 0), 10), (options.Enabled, options.Time, options.KeepDays));

        await _web.GetAsync("/Admin/Backup");
        Assert.Contains("letzte Sicherung", _web.LastPage);
        Assert.Contains("erfolgreich", _web.LastPage);
        Assert.Contains(BackupManager.ListSnapshots(_target).Single().Name, _web.LastPage);
    }

    [Fact]
    public async Task Refuses_a_target_inside_the_data_directory()
    {
        await _web.PostAsync("/Admin/Backup", "/Admin/Backup?handler=Save", ("Form.Enabled", "true"), ("Form.Enabled", "false"),
            ("Form.Directory", Path.Combine(_server.DataDirectory, "backup")), ("Form.Time", "03:00"), ("Form.KeepDays", "14"));

        Assert.Contains("nicht im Datenordner", _web.LastPage);
        Assert.False(_server.Services.GetRequiredService<IOptions<MailserverOptions>>().Value.Backup.Enabled);
    }
}
