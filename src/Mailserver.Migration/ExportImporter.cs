using Mailserver.Core;
using Mailserver.Core.Accounts;
using Mailserver.Core.Migration;
using MailboxStore = Mailserver.Core.Storage.MailboxStore;
using StoredFlags = Mailserver.Core.Storage.MessageFlags;

namespace Mailserver.Migration;

/// <summary>
/// Restores a mailbox saved by <see cref="MailboxExporter"/> into this server. Uses the same import log as
/// <see cref="ImapImporter"/> (source folder name, UIDVALIDITY, UID), so messages already taken over directly
/// via IMAP are not imported a second time, and the restore can be repeated.
/// </summary>
public sealed class ExportImporter(AccountStore accounts, MailboxStore mailboxes, ImportLog log)
{
    /// <summary>Account directories (containing export.json) directly below <paramref name="exportDirectory"/>.</summary>
    public static IReadOnlyList<(EmailAddress Address, string Directory)> FindAccounts(string exportDirectory)
    {
        var result = new List<(EmailAddress, string)>();
        foreach (var directory in Directory.EnumerateDirectories(exportDirectory).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (ExportManifest.Load(directory) is { } manifest && EmailAddress.TryParse(manifest.Address, out var address))
            {
                result.Add((address, directory));
            }
        }

        return result;
    }

    /// <param name="password">Used only if the account has to be created.</param>
    public async Task<AccountImportResult> ImportAsync(string accountDirectory, string? password, bool dryRun,
        Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var manifest = ExportManifest.Load(accountDirectory);
        if (manifest is null || !EmailAddress.TryParse(manifest.Address, out var address))
        {
            return new AccountImportResult(EmailAddress.Parse("unknown@invalid"), false, [], $"{accountDirectory} enthält keine {ExportManifest.FileName}.");
        }

        if (!accounts.IsLocalDomain(address.Domain))
        {
            return new AccountImportResult(address, false, [], $"Domain {address.Domain} ist hier nicht angelegt.");
        }

        var account = accounts.FindAccount(address);
        var created = false;
        if (account is null && !dryRun)
        {
            if (string.IsNullOrEmpty(password))
            {
                return new AccountImportResult(address, false, [], "Postfach existiert nicht und es wurde kein Passwort angegeben.");
            }

            account = accounts.AddAccount(address, password);
            mailboxes.EnsureDefaultFolders(account.Id);
            created = true;
        }

        var results = new List<FolderImportResult>();
        foreach (var folder in manifest.Folders)
        {
            var targetName = ImapImporter.MapFolderName(folder.Name, folder.SeparatorChar, folder.SpecialUse);
            var messages = folder.Messages.Where(m => !StoredFlags.Parse(m.Flags).Contains(StoredFlags.Deleted, StringComparer.OrdinalIgnoreCase)).ToList();
            if (dryRun || account is null)
            {
                progress?.Invoke($"  {folder.Name} -> {targetName}: {messages.Count} Nachrichten (Probelauf)");
                results.Add(new FolderImportResult(folder.Name, targetName, folder.Messages.Count, 0, 0, 0));
                continue;
            }

            var target = mailboxes.GetOrCreateFolder(account.Id, targetName);
            var done = log.GetImportedUids(account.Id, folder.Name, folder.UidValidity);
            var directory = MailboxExporter.FolderPath(accountDirectory, folder);
            int imported = 0, skipped = folder.Messages.Count - messages.Count, failed = 0;
            foreach (var message in messages.OrderBy(m => m.Uid))
            {
                if (done.Contains(message.Uid))
                {
                    skipped++;
                    continue;
                }

                try
                {
                    var raw = await File.ReadAllBytesAsync(Path.Combine(directory, message.File), cancellationToken);
                    await mailboxes.AppendAsync(target, raw, message.Flags, message.Received, cancellationToken);
                    log.Record(account.Id, folder.Name, folder.UidValidity, message.Uid);
                    imported++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
                {
                    progress?.Invoke($"    {message.File} übersprungen: {ex.Message}");
                    failed++;
                }
            }

            progress?.Invoke($"  {folder.Name} -> {targetName}: {imported} übernommen, {skipped} bereits vorhanden/gelöscht" +
                             (failed > 0 ? $", {failed} fehlgeschlagen" : ""));
            results.Add(new FolderImportResult(folder.Name, targetName, folder.Messages.Count, imported, skipped, failed));
        }

        return new AccountImportResult(address, created, results, null);
    }
}
