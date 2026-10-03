using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Mailserver.Core;
using Mailserver.Core.Accounts;
using Mailserver.Core.Migration;
using MailboxStore = Mailserver.Core.Storage.MailboxStore;
using MessageFlags = MailKit.MessageFlags;
using StoredFlags = Mailserver.Core.Storage.MessageFlags;

namespace Mailserver.Migration;

public sealed record ImapSource(string Host, int Port = 993, SecureSocketOptions Security = SecureSocketOptions.SslOnConnect,
    bool AcceptInvalidCertificate = false);

public sealed record FolderImportResult(string SourceFolder, string TargetFolder, int Total, int Imported, int Skipped, int Failed);

public sealed record AccountImportResult(EmailAddress Address, bool AccountCreated, IReadOnlyList<FolderImportResult> Folders, string? Error)
{
    public int Imported => Folders.Sum(f => f.Imported);
    public int Failed => Folders.Sum(f => f.Failed);
}

/// <summary>
/// Copies a mailbox from another IMAP server (e.g. SmarterMail) into this server: all folders, flags and received dates.
/// Messages already imported are skipped, so the import can run early and again right before the switch-over.
/// </summary>
public sealed class ImapImporter(AccountStore accounts, MailboxStore mailboxes, ImportLog log)
{
    // Folder names used by SmarterMail, Outlook, Exchange and German clients, mapped to this server's special folders.
    private static readonly Dictionary<string, string> WellKnownFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sent Items"] = "Sent", ["Sent Messages"] = "Sent", ["Sent Mail"] = "Sent", ["Gesendet"] = "Sent",
        ["Gesendete Elemente"] = "Sent", ["Gesendete Objekte"] = "Sent", ["Sent"] = "Sent",
        ["Deleted Items"] = "Trash", ["Deleted Messages"] = "Trash", ["Gelöschte Elemente"] = "Trash",
        ["Gelöschte Objekte"] = "Trash", ["Papierkorb"] = "Trash", ["Trash"] = "Trash",
        ["Junk E-Mail"] = "Junk", ["Junk-E-Mail"] = "Junk", ["Junk Email"] = "Junk", ["Spam"] = "Junk", ["Junk"] = "Junk",
        ["Drafts"] = "Drafts", ["Entwürfe"] = "Drafts",
        ["Archive"] = "Archive", ["Archiv"] = "Archive",
    };

    /// <param name="password">Password at the source server; also used for the new account if it has to be created.</param>
    /// <param name="dryRun">Only lists what would be imported.</param>
    public async Task<AccountImportResult> ImportAsync(ImapSource source, EmailAddress address, string password, bool dryRun,
        Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
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
            return new AccountImportResult(address, false, [], $"Anmeldung am Quellserver fehlgeschlagen: {ex.Message}");
        }

        if (!accounts.IsLocalDomain(address.Domain))
        {
            return new AccountImportResult(address, false, [], $"Domain {address.Domain} ist hier nicht angelegt.");
        }

        var account = accounts.FindAccount(address);
        var created = false;
        if (account is null && !dryRun)
        {
            account = accounts.AddAccount(address, password);
            mailboxes.EnsureDefaultFolders(account.Id);
            created = true;
        }

        var results = new List<FolderImportResult>();
        foreach (var folder in await ListSourceFoldersAsync(client, cancellationToken))
        {
            var target = MapFolderName(folder);
            results.Add(await ImportFolderAsync(folder, target, account, dryRun, progress, cancellationToken));
        }

        await client.DisconnectAsync(quit: true, cancellationToken);
        return new AccountImportResult(address, created, results, null);
    }

    private async Task<FolderImportResult> ImportFolderAsync(IMailFolder folder, string targetName, Account? account, bool dryRun,
        Action<string>? progress, CancellationToken cancellationToken)
    {
        await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
        var total = folder.Count;
        if (total == 0 || dryRun || account is null)
        {
            progress?.Invoke($"  {folder.FullName} -> {targetName}: {total} Nachrichten{(dryRun ? " (Probelauf)" : "")}");
            await folder.CloseAsync(expunge: false, cancellationToken);
            return new FolderImportResult(folder.FullName, targetName, total, 0, 0, 0);
        }

        var target = mailboxes.GetOrCreateFolder(account.Id, targetName);
        var done = log.GetImportedUids(account.Id, folder.FullName, folder.UidValidity);
        var summaries = await folder.FetchAsync(0, -1,
            MessageSummaryItems.UniqueId | MessageSummaryItems.Flags | MessageSummaryItems.InternalDate, cancellationToken);

        int imported = 0, skipped = 0, failed = 0;
        foreach (var summary in summaries.OrderBy(s => s.UniqueId.Id))
        {
            var flags = summary.Flags ?? MessageFlags.None;
            if (done.Contains(summary.UniqueId.Id) || flags.HasFlag(MessageFlags.Deleted))
            {
                skipped++;
                continue;
            }

            try
            {
                byte[] raw;
                await using (var stream = await folder.GetStreamAsync(summary.UniqueId, "", cancellationToken))
                using (var buffer = new MemoryStream())
                {
                    await stream.CopyToAsync(buffer, cancellationToken);
                    raw = buffer.ToArray();
                }

                await mailboxes.AppendAsync(target, raw, ConvertFlags(flags, summary.Keywords), summary.InternalDate, cancellationToken);
                log.Record(account.Id, folder.FullName, folder.UidValidity, summary.UniqueId.Id);
                imported++;
            }
            catch (Exception ex) when (ex is ImapCommandException or MessageNotFoundException or FormatException)
            {
                // A single broken or vanished message must not stop the rest of the mailbox.
                progress?.Invoke($"    Nachricht UID {summary.UniqueId.Id} übersprungen: {ex.Message}");
                failed++;
            }
        }

        progress?.Invoke($"  {folder.FullName} -> {targetName}: {imported} übernommen, {skipped} bereits vorhanden/gelöscht" +
                         (failed > 0 ? $", {failed} fehlgeschlagen" : ""));
        await folder.CloseAsync(expunge: false, cancellationToken);
        return new FolderImportResult(folder.FullName, targetName, total, imported, skipped, failed);
    }

    private static async Task<List<IMailFolder>> ListSourceFoldersAsync(ImapClient client, CancellationToken cancellationToken)
    {
        var folders = new List<IMailFolder> { client.Inbox };
        foreach (var ns in client.PersonalNamespaces)
        {
            var all = await client.GetFoldersAsync(ns, StatusItems.None, subscribedOnly: false, cancellationToken);
            folders.AddRange(all.Where(f => !f.Attributes.HasFlag(FolderAttributes.NoSelect) &&
                                            !f.Attributes.HasFlag(FolderAttributes.NonExistent) &&
                                            !string.Equals(f.FullName, client.Inbox.FullName, StringComparison.OrdinalIgnoreCase)));
        }

        return folders;
    }

    /// <summary>Maps a source folder to its name here: special folders by attribute or well-known name, others keep their path.</summary>
    public static string MapFolderName(IMailFolder folder)
    {
        if (folder.Attributes.HasFlag(FolderAttributes.Inbox) || folder.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase))
        {
            return MailboxStore.Inbox;
        }

        var special = folder.Attributes switch
        {
            var a when a.HasFlag(FolderAttributes.Sent) => "Sent",
            var a when a.HasFlag(FolderAttributes.Drafts) => "Drafts",
            var a when a.HasFlag(FolderAttributes.Trash) => "Trash",
            var a when a.HasFlag(FolderAttributes.Junk) => "Junk",
            var a when a.HasFlag(FolderAttributes.Archive) => "Archive",
            _ => null,
        };
        return special ?? MapFolderPath(folder.FullName, folder.DirectorySeparator);
    }

    public static string MapFolderPath(string fullName, char separator)
    {
        var segments = separator == '\0' ? [fullName] : fullName.Split(separator);
        if (segments.Length == 1 && WellKnownFolders.TryGetValue(segments[0], out var wellKnown))
        {
            return wellKnown;
        }

        if (segments[0].Equals("INBOX", StringComparison.OrdinalIgnoreCase))
        {
            segments[0] = MailboxStore.Inbox;
        }

        // A '/' inside a name (possible with '.' as separator) would create an unintended level here.
        return string.Join(MailboxStore.HierarchyDelimiter, segments.Select(s => s.Replace(MailboxStore.HierarchyDelimiter, '-')));
    }

    private static string ConvertFlags(MessageFlags flags, IEnumerable<string>? keywords)
    {
        var result = new List<string>();
        if (flags.HasFlag(MessageFlags.Seen)) result.Add(StoredFlags.Seen);
        if (flags.HasFlag(MessageFlags.Answered)) result.Add(StoredFlags.Answered);
        if (flags.HasFlag(MessageFlags.Flagged)) result.Add(StoredFlags.Flagged);
        if (flags.HasFlag(MessageFlags.Draft)) result.Add(StoredFlags.Draft);
        result.AddRange((keywords ?? []).Where(k => k.Length > 0 && !k.StartsWith('\\') && !k.Any(char.IsWhiteSpace)));
        return StoredFlags.Format(result);
    }
}
