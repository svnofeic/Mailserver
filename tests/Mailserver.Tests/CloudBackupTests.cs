using System.Text;
using Mailserver.Core;
using Mailserver.Core.Accounts;
using Mailserver.Core.Backup;
using Mailserver.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Mailserver.Tests;

/// <summary>Backup into OneDrive and pCloud, against local imitations of both services.</summary>
public sealed class CloudBackupTests : IAsyncLifetime
{
    private readonly TestData _data = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 5, 1, 0, 0, TimeSpan.Zero));
    private readonly MailserverOptions _options = new();
    private FakeOneDrive _oneDrive = null!;
    private FakePCloud _pCloud = null!;
    private BackupManager _backups = null!;
    private CloudTokens _tokens = null!;
    private Account _alice = null!;

    public async Task InitializeAsync()
    {
        _oneDrive = await FakeOneDrive.CreateAsync();
        _pCloud = await FakePCloud.CreateAsync();
        _tokens = new CloudTokens(_data.Paths);
        _options.Backup = new BackupOptions
        {
            Enabled = true, KeepDays = 7, RemoteFolder = "Mailserver-Sicherung", OneDriveClientId = "client-1",
            OneDriveLoginUrl = _oneDrive.Url, OneDriveGraphUrl = $"{_oneDrive.Url}/v1.0", PCloudApiUrl = _pCloud.Url,
        };
        _backups = new BackupManager(_data.Database, _data.Paths, Options.Create(_options), _time, NullLogger<BackupManager>.Instance,
            new BackupStores(_data.Paths, _tokens));
        _data.Accounts.AddDomain("example.test");
        _alice = _data.Accounts.AddAccount(EmailAddress.Parse("alice@example.test"), "irrelevant-passwort");
        _data.Mailboxes.EnsureDefaultFolders(_alice.Id);
    }

    public async Task DisposeAsync()
    {
        await _oneDrive.DisposeAsync();
        await _pCloud.DisposeAsync();
        _data.Dispose();
    }

    private Task<StoredMessage> AppendAsync(string subject, int size = 0) =>
        _data.Mailboxes.AppendAsync(_alice, Encoding.ASCII.GetBytes($"Subject: {subject}\r\n\r\n{subject}\r\n{new string('x', size)}"));

    private async Task ConnectOneDriveAsync()
    {
        _options.Backup = _options.Backup with { Target = BackupOptions.OneDriveTarget };
        var login = await OneDriveStore.StartLoginAsync(_options.Backup, CancellationToken.None);
        Assert.Equal("ABCD-1234", login.UserCode);
        Assert.Equal("sven@outlook.test", await OneDriveStore.CompleteLoginAsync(_options.Backup, login, _tokens, CancellationToken.None));
    }

    private async Task ConnectPCloudAsync()
    {
        _options.Backup = _options.Backup with { Target = BackupOptions.PCloudTarget };
        var result = await PCloudStore.LoginAsync(_options.Backup, "sven@pcloud.test", FakePCloud.Password, null, null, CancellationToken.None);
        _tokens.Save(PCloudStore.Provider, result.Token!);
    }

    [Fact]
    public async Task OneDrive_backup_uploads_only_new_mails_and_restores()
    {
        Assert.Equal("Noch nicht mit OneDrive verbunden.", _backups.Problem(_options.Backup with { Target = BackupOptions.OneDriveTarget }));
        await ConnectOneDriveAsync();
        var big = await AppendAsync("Großer Anhang", 6 * 1024 * 1024); // goes through an upload session
        await AppendAsync("Klein");

        var first = await _backups.RunAsync();

        Assert.True(first.Success, first.Message);
        Assert.Contains("OneDrive (sven@outlook.test): /Mailserver-Sicherung", first.Message);
        Assert.Equal(2, _oneDrive.FilesBelow("Mailserver-Sicherung/mail").Count());
        Assert.Equal(new FileInfo(_data.Mailboxes.GetMessagePath(big)).Length, _oneDrive.Files[$"Mailserver-Sicherung/mail/{big.FileName.Replace('\\', '/')}"].Length);
        Assert.Contains($"Mailserver-Sicherung/snapshots/{first.Snapshot}/manifest.json", _oneDrive.Files.Keys);

        _time.Now = _time.Now.AddDays(1);
        await AppendAsync("Neu");
        var uploads = _oneDrive.Uploads;
        var second = await _backups.RunAsync();
        Assert.True(second.Success, second.Message);
        Assert.Equal(1, second.FilesCopied - (first.FilesCopied - 2)); // snapshot files plus the one new mail

        using var store = _backups.Stores.Open(_options.Backup);
        Assert.Equal(2, (await BackupManager.ListSnapshotsAsync(store)).Count);
        Assert.True(_oneDrive.Refreshes >= 1); // access via refresh token, new refresh token stored
        Assert.Equal(_oneDrive.RefreshToken, _tokens.Load<OneDriveToken>(OneDriveStore.Provider)!.RefreshToken);
        Assert.Equal(uploads - 1, _oneDrive.Uploads - uploads); // the same files as the first time, but only one mail

        using var fresh = new TestData();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var result = await BackupManager.RestoreAsync(store, null, fresh.Paths);
        Assert.Equal(3, result.MailFiles);
        var restored = new MailboxStore(new Mailserver.Core.Data.Database(fresh.Paths), fresh.Paths);
        Assert.Equal(3, restored.ListMessages(restored.GetFolder(_alice.Id, MailboxStore.Inbox)!.Id).Count);
        Assert.Equal(File.ReadAllBytes(_data.Mailboxes.GetMessagePath(big)), File.ReadAllBytes(Path.Combine(fresh.Paths.MailRoot, big.FileName)));
    }

    [Fact]
    public async Task OneDrive_reports_a_revoked_connection()
    {
        await ConnectOneDriveAsync();
        _tokens.Save(OneDriveStore.Provider, _tokens.Load<OneDriveToken>(OneDriveStore.Provider)! with { RefreshToken = "widerrufen" });

        var run = await _backups.RunAsync();

        Assert.False(run.Success);
        Assert.Contains("neu verbinden", run.Message);
    }

    [Fact]
    public async Task PCloud_backup_cleans_up_and_restores()
    {
        await ConnectPCloudAsync();
        var gone = await AppendAsync("Wird gelöscht");
        await AppendAsync("Bleibt");
        Assert.True((await _backups.RunAsync()).Success);
        Assert.Equal(2, _pCloud.FilesBelow("Mailserver-Sicherung/mail").Count());

        var inbox = _data.Mailboxes.GetFolder(_alice.Id, MailboxStore.Inbox)!;
        _data.Mailboxes.UpdateFlags(inbox.Id, [gone.Uid], FlagOperation.Add, [MessageFlags.Deleted]);
        _data.Mailboxes.Expunge(inbox.Id);
        for (var day = 1; day <= 9; day++)
        {
            _time.Now = _time.Now.AddDays(1);
            var run = await _backups.RunAsync();
            Assert.True(run.Success, run.Message);
        }

        // The deleted mail and the snapshots older than 7 days are gone from the cloud.
        Assert.DoesNotContain($"Mailserver-Sicherung/mail/{gone.FileName.Replace('\\', '/')}", _pCloud.Files.Keys);
        using var store = _backups.Stores.Open(_options.Backup);
        var snapshots = await BackupManager.ListSnapshotsAsync(store);
        Assert.Equal(8, snapshots.Count);
        Assert.All(snapshots, s => Assert.True(_time.Now - s.Created <= TimeSpan.FromDays(7)));

        using var fresh = new TestData();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var result = await BackupManager.RestoreAsync(store, snapshots[^1].Name, fresh.Paths);
        Assert.Equal(snapshots[^1].Name, result.Snapshot);
        Assert.Equal(1, result.MailFiles);
    }

    [Fact]
    public async Task PCloud_login_with_two_factor_code()
    {
        var first = await PCloudStore.LoginAsync(_options.Backup, "2fa@pcloud.test", FakePCloud.Password, null, null, CancellationToken.None);
        Assert.True(first.NeedsCode);

        var second = await PCloudStore.LoginAsync(_options.Backup, "2fa@pcloud.test", FakePCloud.Password, "123456", first.TwoFactorToken,
            CancellationToken.None);
        Assert.Equal(FakePCloud.Auth, second.Token!.Auth);

        var ex = await Assert.ThrowsAsync<BackupException>(() =>
            PCloudStore.LoginAsync(_options.Backup, "sven@pcloud.test", "falsch", null, null, CancellationToken.None));
        Assert.Contains("Anmeldung fehlgeschlagen", ex.Message);
    }

    [Fact]
    public async Task A_new_or_emptied_target_gets_all_mails_again()
    {
        await ConnectPCloudAsync();
        await AppendAsync("Eins");
        await _backups.RunAsync();

        // Someone deleted the folder in the cloud: the index must not pretend the mails are still there.
        foreach (var key in _pCloud.Files.Keys)
        {
            _pCloud.Files.TryRemove(key, out _);
        }

        foreach (var key in _pCloud.Folders.Keys)
        {
            _pCloud.Folders.TryRemove(key, out _);
        }

        _time.Now = _time.Now.AddDays(1);
        Assert.True((await _backups.RunAsync()).Success);
        Assert.Single(_pCloud.FilesBelow("Mailserver-Sicherung/mail"));
    }

    [Fact]
    public void Tokens_are_not_part_of_the_snapshot_folders_and_can_be_removed()
    {
        _tokens.Save(PCloudStore.Provider, new PCloudToken("geheim", _pCloud.Url, "sven@pcloud.test"));
        Assert.StartsWith(_data.Paths.Root, _tokens.Folder);
        Assert.Equal("geheim", _tokens.Load<PCloudToken>(PCloudStore.Provider)!.Auth);

        _tokens.Delete(PCloudStore.Provider);
        Assert.Null(_tokens.Load<PCloudToken>(PCloudStore.Provider));
    }
}

/// <summary>Connecting a cloud storage in the web interface.</summary>
public sealed class CloudBackupWebTests : IAsyncLifetime
{
    private TestServer _server = null!;
    private WebClient _web = null!;
    private FakePCloud _pCloud = null!;
    private FakeOneDrive _oneDrive = null!;

    public async Task InitializeAsync()
    {
        _pCloud = await FakePCloud.CreateAsync();
        _oneDrive = await FakeOneDrive.CreateAsync();
        _server = await TestServer.StartAsync(new Dictionary<string, string?>
        {
            ["Mailserver:Backup:PCloudApiUrl"] = _pCloud.Url,
            ["Mailserver:Backup:OneDriveLoginUrl"] = _oneDrive.Url,
            ["Mailserver:Backup:OneDriveGraphUrl"] = $"{_oneDrive.Url}/v1.0",
        });
        var admin = _server.HostAccounts.AddAccount(EmailAddress.Parse("chef@example.test"), TestServer.Password);
        _server.HostAccounts.SetAdmin(admin.Address, true);
        _web = new WebClient(_server.WebPort);
        await _web.LoginAsync("chef@example.test", TestServer.Password);
    }

    public async Task DisposeAsync()
    {
        _web.Dispose();
        await _server.DisposeAsync();
        await _pCloud.DisposeAsync();
        await _oneDrive.DisposeAsync();
    }

    private static (string, string)[] Form(string target, params (string, string)[] extra) =>
    [
        ("Form.Enabled", "true"), ("Form.Enabled", "false"), ("Form.Target", target), ("Form.RemoteFolder", "Sicherung"),
        ("Form.Time", "03:00"), ("Form.KeepDays", "14"), ("Form.OneDriveClientId", "client-1"), ("Form.OneDriveTenant", "common"),
        ("Form.PCloudRegion", "EU"), .. extra,
    ];

    [Fact]
    public async Task Admin_connects_pCloud_with_two_factor_code_and_backs_up()
    {
        await _web.PostAsync("/Admin/Backup", "/Admin/Backup?handler=ConnectPCloud",
            Form(BackupOptions.PCloudTarget, ("email", "2fa@pcloud.test"), ("password", FakePCloud.Password)));
        Assert.Contains("Zwei-Faktor", _web.LastPage);
        Assert.Contains("Code (Zwei-Faktor)", _web.LastPage);

        await _web.PostAsync("/Admin/Backup", "/Admin/Backup?handler=ConnectPCloud",
            Form(BackupOptions.PCloudTarget, ("email", "2fa@pcloud.test"), ("password", ""), ("code", "123456")));
        Assert.Contains("Mit pCloud verbunden (2fa@pcloud.test)", _web.LastPage);

        await _web.PostAsync("/Admin/Backup", "/Admin/Backup?handler=Run", Form(BackupOptions.PCloudTarget));
        var backups = _server.Services.GetRequiredService<BackupManager>();
        await TestServer.WaitUntilAsync(() => backups.LastSuccess() is not null, "backup to pCloud");
        Assert.Contains("pCloud (2fa@pcloud.test): /Sicherung", backups.LastSuccess()!.Message);

        var options = _server.Services.GetRequiredService<IOptions<MailserverOptions>>();
        await TestServer.WaitUntilAsync(() => options.Value.Backup.Target == BackupOptions.PCloudTarget, "reloaded settings");
        await _web.GetAsync("/Admin/Backup");
        Assert.Contains("verbunden mit 2fa@pcloud.test, 9 GB frei", _web.LastPage);
        Assert.Contains(backups.LastSuccess()!.Snapshot!, _web.LastPage);
    }

    [Fact]
    public async Task Admin_connects_OneDrive_with_a_device_code()
    {
        _oneDrive.Interval = 1; // the sign-in completes only after the page was shown
        await _web.PostAsync("/Admin/Backup", "/Admin/Backup?handler=ConnectOneDrive", Form(BackupOptions.OneDriveTarget));
        Assert.Contains("ABCD-1234", _web.LastPage);
        Assert.Contains("http-equiv=\"refresh\"", _web.LastPage);

        var cloud = _server.Services.GetRequiredService<CloudConnector>();
        await TestServer.WaitUntilAsync(() => cloud.IsConnected(OneDriveStore.Provider), "device code confirmed");
        await TestServer.WaitUntilAsync(() => cloud.PendingOneDrive is null, "sign-in finished");
        await _web.GetAsync("/Admin/Backup");
        Assert.Contains("verbunden mit sven@outlook.test", _web.LastPage);
        Assert.DoesNotContain("ABCD-1234", _web.LastPage);

        await _web.PostAsync("/Admin/Backup", "/Admin/Backup?handler=Disconnect", [.. Form(BackupOptions.OneDriveTarget), ("provider", "onedrive")]);
        Assert.False(cloud.IsConnected(OneDriveStore.Provider));
    }
}
