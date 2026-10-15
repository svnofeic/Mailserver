using Mailserver.Core.Accounts;
using Mailserver.Core.Storage;
using Microsoft.Extensions.Hosting;

namespace Mailserver.Web.Webmail;

/// <summary>
/// Every half minute: sends the mail whose "Später senden" time has come, and brings back the mail whose "Wiedervorlage"
/// is due – into its folder, unread, with a push notification like new mail.
/// </summary>
public sealed class ScheduledMailService(
    FollowUps followUps,
    WebmailSender sender,
    AccountStore accounts,
    MailboxStore mailboxes,
    Mailserver.Core.Push.PushNotifier push,
    ILogger<ScheduledMailService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    // One run at a time, so a message is never sent twice (the timer, "Jetzt senden" and "Jetzt zurückholen" can overlap).
    private readonly SemaphoreSlim _running = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Scheduled mail could not be processed");
            }

            await Task.Delay(Interval, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await _running.WaitAsync(cancellationToken);
        try
        {
            await ProcessDueAsync(cancellationToken);
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>"Jetzt senden": sends one scheduled message right away. Throws <see cref="ComposeException"/>.</summary>
    public async Task SendNowAsync(Account account, ScheduledSend scheduled, CancellationToken cancellationToken)
    {
        await _running.WaitAsync(cancellationToken);
        try
        {
            await sender.SendScheduledAsync(account, scheduled, cancellationToken);
        }
        finally
        {
            _running.Release();
        }
    }

    private async Task ProcessDueAsync(CancellationToken cancellationToken)
    {
        foreach (var scheduled in followUps.DueSends())
        {
            if (accounts.FindAccount(scheduled.AccountId) is not { Enabled: true } account)
            {
                continue;
            }

            try
            {
                await sender.SendScheduledAsync(account, scheduled, cancellationToken);
                logger.LogInformation("Sent scheduled message of {Account}", account.Address);
            }
            catch (ComposeException ex)
            {
                // Not sent: the message stays in "Geplant", and the owner learns about it in the inbox.
                followUps.SendFailed(scheduled.Id, ex.Message);
                logger.LogWarning("Scheduled message of {Account} not sent: {Error}", account.Address, ex.Message);
                await NotifyAsync(account, "Geplante Mail nicht gesendet",
                    $"Eine Mail, die für {Format.Time(scheduled.SendAt)} geplant war, konnte nicht gesendet werden:\r\n\r\n{ex.Message}\r\n\r\n" +
                    "Sie liegt weiter im Ordner „Geplant“. Unter Mail → Geplant & Wiedervorlage lässt sie sich erneut senden oder bearbeiten.",
                    cancellationToken);
            }
        }

        foreach (var due in followUps.DueFollowUps())
        {
            followUps.RemoveFollowUp(due.Id);
            // Deleted (or thrown into the trash) in the meantime: nothing comes back.
            if (accounts.FindAccount(due.AccountId) is not { } account || followUps.Locate(account.Id, due.FileName) is not { } location ||
                location.Folder.Name == "Trash")
            {
                continue;
            }

            var folder = location.Folder;
            var uid = location.Message.Uid;
            if (folder.Name == FollowUps.FollowUpFolder)
            {
                // Back where it came from (if that folder is gone meanwhile: the inbox).
                var target = mailboxes.GetFolder(account.Id, due.ReturnFolder) ?? mailboxes.GetOrCreateFolder(account.Id, MailboxStore.Inbox);
                uid = mailboxes.Move(folder.Id, [uid], target.Id).Single().TargetUid;
                folder = target;
            }

            mailboxes.UpdateFlags(folder.Id, [uid], FlagOperation.Remove, [MessageFlags.Seen]);
            if (folder.Name == MailboxStore.Inbox)
            {
                push.NewMail(account, uid, await File.ReadAllBytesAsync(mailboxes.GetMessagePath(location.Message), cancellationToken));
            }

            logger.LogInformation("Follow-up of {Account}: message back in {Folder}", account.Address, folder.Name);
        }
    }

    private async Task NotifyAsync(Account account, string subject, string text, CancellationToken cancellationToken)
    {
        var message = new MimeKit.MimeMessage { Subject = subject, Body = new MimeKit.TextPart("plain") { Text = text } };
        message.From.Add(new MimeKit.MailboxAddress("Mailserver", account.Address.ToString()));
        message.To.Add(new MimeKit.MailboxAddress(null, account.Address.ToString()));
        using var raw = new MemoryStream();
        await message.WriteToAsync(raw, cancellationToken);
        await mailboxes.AppendAsync(account, raw.ToArray(), cancellationToken: cancellationToken);
    }
}
