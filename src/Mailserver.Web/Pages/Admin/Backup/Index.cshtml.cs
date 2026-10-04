using Mailserver.Core;
using Mailserver.Core.Backup;
using Mailserver.Core.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Mailserver.Web.Pages.Admin.Backup;

/// <summary>Automatic backup: target (folder, OneDrive, pCloud), past runs, snapshots in the target.</summary>
public sealed class IndexModel(IOptions<MailserverOptions> options, BackupManager backups, SettingsStore settings, CloudConnector cloud,
    IHostApplicationLifetime lifetime) : MailPageModel
{
    public IReadOnlyList<BackupRun> Runs { get; private set; } = [];
    public IReadOnlyList<BackupSnapshot> Snapshots { get; private set; } = [];
    public string? SnapshotProblem { get; private set; }
    public BackupRun? LastSuccess { get; private set; }
    public bool Running => backups.Running;
    public BackupOptions Current => options.Value.Backup;
    public string TargetDescription => backups.Stores.Describe(Current);
    public CloudConnector Cloud => cloud;

    /// <summary>Account and free space of the connected cloud storage, or why it cannot be reached.</summary>
    public string? CloudStatus { get; private set; }

    [BindProperty]
    public BackupForm Form { get; set; } = new();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Form = BackupForm.From(Current);
        await DescribeAsync(cancellationToken);
    }

    public Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken) => SaveAsync(runNow: false, cancellationToken);

    public Task<IActionResult> OnPostRunAsync(CancellationToken cancellationToken) => SaveAsync(runNow: true, cancellationToken);

    public async Task<IActionResult> OnPostConnectOneDriveAsync(CancellationToken cancellationToken)
    {
        var backup = Form.ToOptions(Current) with { Target = BackupOptions.OneDriveTarget };
        settings.SaveBackup(backup);
        try
        {
            await cloud.StartOneDriveAsync(backup, lifetime.ApplicationStopping);
        }
        catch (Exception ex) when (ex is BackupException or HttpRequestException)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostConnectPCloudAsync(string? email, string? password, string? code, CancellationToken cancellationToken)
    {
        var backup = Form.ToOptions(Current) with { Target = BackupOptions.PCloudTarget };
        settings.SaveBackup(backup);
        if (string.IsNullOrWhiteSpace(email) || (string.IsNullOrEmpty(password) && cloud.PendingPCloudCode is null))
        {
            ErrorMessage = "Bitte E-Mail-Adresse und Passwort des pCloud-Kontos eingeben.";
            return RedirectToPage();
        }

        try
        {
            if (await cloud.ConnectPCloudAsync(backup, email, password ?? "", code, cancellationToken))
            {
                Message = $"Mit pCloud verbunden ({cloud.Account(PCloudStore.Provider)}). Das Passwort wurde nicht gespeichert.";
            }
            else
            {
                ErrorMessage = "pCloud verlangt den Code der Zwei-Faktor-Anmeldung: bitte Code eingeben und noch einmal „Mit pCloud verbinden“.";
            }
        }
        catch (Exception ex) when (ex is BackupException or HttpRequestException)
        {
            ErrorMessage = ex.Message;
        }

        return RedirectToPage();
    }

    public IActionResult OnPostDisconnect(string provider)
    {
        if (provider is OneDriveStore.Provider or PCloudStore.Provider)
        {
            cloud.Disconnect(provider);
            Message = "Verbindung getrennt. Die Sicherungen in der Cloud bleiben erhalten.";
        }

        return RedirectToPage();
    }

    private async Task<IActionResult> SaveAsync(bool runNow, CancellationToken cancellationToken)
    {
        var backup = Form.ToOptions(Current);
        var problem = backups.Problem(backup);
        var notConnected = problem is not null && problem.StartsWith("Noch nicht", StringComparison.Ordinal);
        if ((backup.Enabled || runNow) && problem is not null && !(notConnected && !runNow))
        {
            ErrorMessage = problem;
            await DescribeAsync(cancellationToken);
            return Page();
        }

        settings.SaveBackup(backup);
        if (!runNow)
        {
            Message = notConnected && backup.Enabled
                ? $"Gespeichert. {problem} – bitte unten verbinden, sonst schlägt die Sicherung fehl."
                : backup.Enabled ? $"Gespeichert. Die Sicherung läuft täglich um {backup.Time:hh\\:mm} Uhr." : "Gespeichert. Die automatische Sicherung ist aus.";
            return RedirectToPage();
        }

        if (backups.Running)
        {
            ErrorMessage = "Es läuft bereits eine Sicherung.";
            return RedirectToPage();
        }

        // The first backup copies all mails and may take a while; it continues in the background.
        _ = Task.Run(() => backups.RunAsync(backup, lifetime.ApplicationStopping));
        Message = "Die Sicherung läuft. Das Ergebnis steht in wenigen Augenblicken unten (Seite neu laden); die erste kann bei vielen Mails länger dauern.";
        return RedirectToPage();
    }

    private async Task DescribeAsync(CancellationToken cancellationToken)
    {
        Runs = backups.RecentRuns();
        LastSuccess = backups.LastSuccess();
        if (backups.Problem() is not null)
        {
            return;
        }

        // A slow or unreachable cloud must not block the page for long.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var store = backups.Stores.Open(Current);
            Snapshots = await BackupManager.ListSnapshotsAsync(store, timeout.Token);
            CloudStatus = store switch
            {
                OneDriveStore oneDrive => await oneDrive.DescribeAccountAsync(timeout.Token) is var (account, free)
                    ? $"verbunden mit {account}" + (free is { } f ? $", {BackupManager.Format(f)} frei" : "")
                    : null,
                PCloudStore pCloud => $"verbunden mit {cloud.Account(PCloudStore.Provider)}" +
                                      (await pCloud.FreeSpaceAsync(timeout.Token) is { } f ? $", {BackupManager.Format(f)} frei" : ""),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BackupException or HttpRequestException
                                       or OperationCanceledException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
        {
            SnapshotProblem = ex is OperationCanceledException ? "Das Sicherungsziel antwortet nicht." : ex.Message;
        }
    }
}

public sealed class BackupForm
{
    public bool Enabled { get; set; }
    public string Target { get; set; } = BackupOptions.FolderTarget;
    public string? Directory { get; set; }
    public string? RemoteFolder { get; set; }
    public TimeSpan Time { get; set; }
    public int KeepDays { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? OneDriveClientId { get; set; }
    public string? OneDriveTenant { get; set; }
    public string? PCloudRegion { get; set; }

    public static BackupForm From(BackupOptions backup) => new()
    {
        Enabled = backup.Enabled, Target = backup.Target, Directory = backup.Directory, RemoteFolder = backup.RemoteFolder, Time = backup.Time,
        KeepDays = backup.KeepDays, Username = backup.Username, OneDriveClientId = backup.OneDriveClientId, OneDriveTenant = backup.OneDriveTenant,
        PCloudRegion = backup.PCloudRegion,
    };

    public BackupOptions ToOptions(BackupOptions current) => current with
    {
        Enabled = Enabled,
        Target = Target is BackupOptions.OneDriveTarget or BackupOptions.PCloudTarget ? Target : BackupOptions.FolderTarget,
        Directory = string.IsNullOrWhiteSpace(Directory) ? null : Directory.Trim(),
        RemoteFolder = string.IsNullOrWhiteSpace(RemoteFolder) ? current.RemoteFolder : RemoteFolder.Trim().Trim('/', '\\'),
        Time = Time,
        KeepDays = KeepDays,
        Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim(),
        // Empty password field = unchanged (it is never sent back to the browser).
        Password = string.IsNullOrWhiteSpace(Username) ? null : string.IsNullOrEmpty(Password) ? current.Password : Password,
        OneDriveClientId = string.IsNullOrWhiteSpace(OneDriveClientId) ? null : OneDriveClientId.Trim(),
        OneDriveTenant = string.IsNullOrWhiteSpace(OneDriveTenant) ? "common" : OneDriveTenant.Trim(),
        PCloudRegion = PCloudRegion == "US" ? "US" : "EU",
    };
}
