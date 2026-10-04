using Mailserver.Core;
using Mailserver.Core.Backup;
using Mailserver.Core.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Mailserver.Web.Pages.Admin.Backup;

/// <summary>Automatic backup: settings, past runs, snapshots in the target folder.</summary>
public sealed class IndexModel(IOptions<MailserverOptions> options, BackupManager backups, SettingsStore settings,
    IHostApplicationLifetime lifetime) : MailPageModel
{
    public IReadOnlyList<BackupRun> Runs { get; private set; } = [];
    public IReadOnlyList<BackupSnapshot> Snapshots { get; private set; } = [];
    public string? SnapshotProblem { get; private set; }
    public BackupRun? LastSuccess { get; private set; }
    public bool Running => backups.Running;
    public BackupOptions Current => options.Value.Backup;

    [BindProperty]
    public BackupForm Form { get; set; } = new();

    public void OnGet()
    {
        Form = BackupForm.From(Current);
        Describe();
    }

    public IActionResult OnPostSave() => Save(runNow: false);

    public IActionResult OnPostRun() => Save(runNow: true);

    private IActionResult Save(bool runNow)
    {
        var backup = Form.ToOptions(Current);
        if ((backup.Enabled || runNow) && backups.Problem(backup) is { } problem)
        {
            ErrorMessage = problem;
            Describe();
            return Page();
        }

        settings.SaveBackup(backup);
        if (!runNow)
        {
            Message = backup.Enabled ? $"Gespeichert. Die Sicherung läuft täglich um {backup.Time:hh\\:mm} Uhr." : "Gespeichert. Die automatische Sicherung ist aus.";
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

    private void Describe()
    {
        Runs = backups.RecentRuns();
        LastSuccess = backups.LastSuccess();
        if (Current.Directory is { Length: > 0 } directory && backups.Problem() is null)
        {
            try
            {
                Snapshots = BackupManager.ListSnapshots(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SnapshotProblem = ex.Message;
            }
        }
    }
}

public sealed class BackupForm
{
    public bool Enabled { get; set; }
    public string? Directory { get; set; }
    public TimeSpan Time { get; set; }
    public int KeepDays { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }

    public static BackupForm From(BackupOptions backup) => new()
    {
        Enabled = backup.Enabled, Directory = backup.Directory, Time = backup.Time, KeepDays = backup.KeepDays, Username = backup.Username,
    };

    public BackupOptions ToOptions(BackupOptions current) => new()
    {
        Enabled = Enabled,
        Directory = string.IsNullOrWhiteSpace(Directory) ? null : Directory.Trim(),
        Time = Time,
        KeepDays = KeepDays,
        Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim(),
        // Empty password field = unchanged (it is never sent back to the browser).
        Password = string.IsNullOrWhiteSpace(Username) ? null : string.IsNullOrEmpty(Password) ? current.Password : Password,
    };
}
