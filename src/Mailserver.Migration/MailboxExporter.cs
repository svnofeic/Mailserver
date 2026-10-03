using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Mailserver.Core;
using MessageFlags = MailKit.MessageFlags;

namespace Mailserver.Migration;

public sealed record FolderExportResult(string Folder, string Directory, int Total, int Exported, int Skipped, int Failed);

public sealed record AccountExportResult(EmailAddress Address, string Directory, IReadOnlyList<FolderExportResult> Folders, string? Error)
{
    public int Exported => Folders.Sum(f => f.Exported);
    public int Total => Folders.Sum(f => f.Total);
    public int Failed => Folders.Sum(f => f.Failed);
}

/// <summary>
/// Saves a mailbox from any IMAP server (e.g. SmarterMail) as .eml files: one directory per folder below
/// &lt;target&gt;/&lt;address&gt;/Mail, plus export.json with flags and received dates. Files keep the received date as
/// their timestamp. Running it again only fetches new messages and updates flags.
/// </summary>
public sealed class MailboxExporter
{
    public const string MailDirectory = "Mail";

    private const int SaveEvery = 200;

    public async Task<AccountExportResult> ExportAsync(ImapSource source, EmailAddress address, string password, string targetDirectory,
        Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var accountDirectory = AccountDirectory(targetDirectory, address);
        using var client = new ImapClient();
        if (source.AcceptInvalidCertificate)
        {
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;
        }

        try
        {
            await client.ConnectAsync(source.Host, source.Port, source.Security, cancellationToken);
            await client.AuthenticateAsync(address.ToString(), password, cancellationToken);
        }
        catch (Exception ex) when (ex is AuthenticationException or ImapProtocolException or ImapCommandException or IOException
                                       or System.Net.Sockets.SocketException or SslHandshakeException)
        {
            return new AccountExportResult(address, accountDirectory, [], $"Anmeldung fehlgeschlagen: {ex.Message}");
        }

        Directory.CreateDirectory(accountDirectory);
        var manifest = ExportManifest.Load(accountDirectory) ?? new ExportManifest();
        manifest.Address = address.ToString();
        manifest.Source = $"{source.Host}:{source.Port}";

        var results = new List<FolderExportResult>();
        foreach (var folder in await ImapImporter.ListSourceFoldersAsync(client, cancellationToken))
        {
            results.Add(await ExportFolderAsync(folder, manifest, accountDirectory, progress, cancellationToken));
        }

        manifest.Updated = DateTimeOffset.Now;
        manifest.Save(accountDirectory);
        await client.DisconnectAsync(quit: true, cancellationToken);
        return new AccountExportResult(address, accountDirectory, results, null);
    }

    public static string AccountDirectory(string targetDirectory, EmailAddress address) =>
        Path.Combine(targetDirectory, ExportNames.Sanitize(address.ToString(), 254));

    private static async Task<FolderExportResult> ExportFolderAsync(IMailFolder folder, ExportManifest manifest, string accountDirectory,
        Action<string>? progress, CancellationToken cancellationToken)
    {
        await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
        var entry = manifest.Folders.FirstOrDefault(f => f.Name == folder.FullName);
        if (entry is not null && entry.UidValidity != folder.UidValidity)
        {
            // The server renumbered the folder: keep the old files aside and export it again.
            var oldPath = FolderPath(accountDirectory, entry);
            if (Directory.Exists(oldPath))
            {
                Directory.Move(oldPath, ExportNames.Unique($"{oldPath} (alt {entry.UidValidity})", Directory.Exists));
            }

            manifest.Folders.Remove(entry);
            entry = null;
        }

        if (entry is null)
        {
            entry = new ExportedFolder
            {
                Name = folder.FullName,
                Separator = folder.DirectorySeparator == '\0' ? "" : folder.DirectorySeparator.ToString(),
                SpecialUse = ImapImporter.SpecialUse(folder),
                Directory = FolderDirectory(folder, manifest),
                UidValidity = folder.UidValidity,
            };
            manifest.Folders.Add(entry);
        }

        var directory = FolderPath(accountDirectory, entry);
        Directory.CreateDirectory(directory);

        var summaries = folder.Count == 0
            ? []
            : await folder.FetchAsync(0, -1,
                MessageSummaryItems.UniqueId | MessageSummaryItems.Flags | MessageSummaryItems.InternalDate |
                MessageSummaryItems.Size | MessageSummaryItems.Envelope, cancellationToken);
        var known = entry.Messages.ToDictionary(m => m.Uid);

        int exported = 0, skipped = 0, failed = 0;
        foreach (var summary in summaries.OrderBy(s => s.UniqueId.Id))
        {
            var flags = ImapImporter.ConvertFlags(summary.Flags ?? MessageFlags.None, summary.Keywords);
            if ((summary.Flags ?? MessageFlags.None).HasFlag(MessageFlags.Deleted))
            {
                flags = Mailserver.Core.Storage.MessageFlags.Format([.. Mailserver.Core.Storage.MessageFlags.Parse(flags), Mailserver.Core.Storage.MessageFlags.Deleted]);
            }

            if (known.TryGetValue(summary.UniqueId.Id, out var existing) && File.Exists(Path.Combine(directory, existing.File)))
            {
                existing.Flags = flags;
                skipped++;
                continue;
            }

            try
            {
                var name = $"{summary.UniqueId.Id:D6} {ExportNames.Sanitize(summary.Envelope?.Subject, 60, "(ohne Betreff)")}.eml";
                var path = Path.Combine(directory, name);
                var temp = path + ".tmp";
                await using (var stream = await folder.GetStreamAsync(summary.UniqueId, "", cancellationToken))
                await using (var file = File.Create(temp))
                {
                    await stream.CopyToAsync(file, cancellationToken);
                }

                File.Move(temp, path, overwrite: true);
                if (summary.InternalDate is { } received)
                {
                    File.SetCreationTimeUtc(path, received.UtcDateTime);
                    File.SetLastWriteTimeUtc(path, received.UtcDateTime);
                }

                if (existing is not null)
                {
                    entry.Messages.Remove(existing);
                }

                entry.Messages.Add(new ExportedMessage
                {
                    Uid = summary.UniqueId.Id,
                    File = name,
                    Received = summary.InternalDate,
                    Flags = flags,
                    Size = new FileInfo(path).Length,
                });
                exported++;
                if (exported % SaveEvery == 0)
                {
                    manifest.Save(accountDirectory);
                }
            }
            catch (Exception ex) when (ex is ImapCommandException or MessageNotFoundException or FormatException)
            {
                progress?.Invoke($"    Nachricht UID {summary.UniqueId.Id} übersprungen: {ex.Message}");
                failed++;
            }
        }

        manifest.Save(accountDirectory);
        await folder.CloseAsync(expunge: false, cancellationToken);
        progress?.Invoke($"  {folder.FullName} -> {MailDirectory}/{entry.Directory}: {exported} neu gesichert, {skipped} bereits vorhanden" +
                         (failed > 0 ? $", {failed} fehlgeschlagen" : ""));
        return new FolderExportResult(folder.FullName, entry.Directory, summaries.Count, exported, skipped, failed);
    }

    public static string FolderPath(string accountDirectory, ExportedFolder folder) =>
        Path.Combine([accountDirectory, MailDirectory, .. folder.Directory.Split('/')]);

    /// <summary>The source path with names that are valid on Windows, unique among the folders already exported.</summary>
    private static string FolderDirectory(IMailFolder folder, ExportManifest manifest)
    {
        var segments = folder.DirectorySeparator == '\0' ? [folder.FullName] : folder.FullName.Split(folder.DirectorySeparator);
        var directory = string.Join('/', segments.Select(s => ExportNames.Sanitize(s, 80)));
        return ExportNames.Unique(directory, d => manifest.Folders.Any(f => f.Directory.Equals(d, StringComparison.OrdinalIgnoreCase)));
    }
}
