using System.Text.Json;
using System.Threading.Channels;
using Mailserver.Core.Accounts;
using Mailserver.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Mailserver.Core.Push;

/// <summary>
/// Push notifications for new mail in the inbox. Delivery only queues the notice; a background loop sends it, so a slow push
/// service never holds up receiving mail.
/// </summary>
public sealed class PushNotifier(
    PushSubscriptionStore subscriptions,
    WebPushSender sender,
    MailboxStore mailboxes,
    IOptions<MailserverOptions> options,
    ILogger<PushNotifier> logger)
{
    private sealed record Job(long AccountId, string Json);

    private readonly Channel<Job> _queue = Channel.CreateBounded<Job>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest });

    /// <summary>A message was stored in the inbox of <paramref name="account"/>.</summary>
    public void NewMail(Account account, long uid, byte[] message)
    {
        if (subscriptions.ForAccount(account.Id).Count == 0)
        {
            return;
        }

        HeaderList headers;
        using (var stream = new MemoryStream(message, writable: false))
        {
            headers = HeaderList.Load(stream);
        }

        var from = InternetAddressList.TryParse(headers[HeaderId.From] ?? "", out var list) && list.Mailboxes.FirstOrDefault() is { } mailbox
            ? (string.IsNullOrWhiteSpace(mailbox.Name) ? mailbox.Address : mailbox.Name)
            : "Neue Mail";
        var subject = headers[HeaderId.Subject] is { Length: > 0 } s ? Decode(s) : "(kein Betreff)";
        Enqueue(account.Id, from, subject.Length > 180 ? subject[..180] + "…" : subject,
            $"/Mail/Read?folder=INBOX&uid={uid}", Unread(account.Id));
    }

    /// <summary>A test message to every device of the mailbox.</summary>
    public void Test(Account account) =>
        Enqueue(account.Id, "Mailserver", "Benachrichtigungen funktionieren auf diesem Gerät.", "/Account/Notifications", Unread(account.Id));

    /// <summary>Sends queued notices until the service stops.</summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            await SendAsync(job, stoppingToken);
        }
    }

    /// <summary>Sends everything queued so far (tests).</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        while (_queue.Reader.TryRead(out var job))
        {
            await SendAsync(job, cancellationToken);
        }
    }

    private async Task SendAsync(Job job, CancellationToken cancellationToken)
    {
        foreach (var subscription in subscriptions.ForAccount(job.AccountId))
        {
            var (outcome, detail) = await sender.SendAsync(subscription, job.Json, $"https://{options.Value.Hostname}", cancellationToken);
            subscriptions.Record(subscription.Id, outcome == PushOutcome.Sent, outcome == PushOutcome.Gone);
            if (outcome != PushOutcome.Sent)
            {
                logger.LogInformation("Push to {Device} of account {Account}: {Outcome} {Detail}", subscription.Device, job.AccountId, outcome, detail);
            }
        }
    }

    private void Enqueue(long accountId, string title, string body, string url, long unread) =>
        _queue.Writer.TryWrite(new Job(accountId, JsonSerializer.Serialize(new { title, body, url, unread, tag = "inbox" })));

    private long Unread(long accountId) =>
        mailboxes.GetFolder(accountId, MailboxStore.Inbox) is { } inbox ? mailboxes.GetStatus(inbox.Id).Unseen : 0;

    private static string Decode(string value)
    {
        try
        {
            return MimeKit.Utils.Rfc2047.DecodeText(System.Text.Encoding.UTF8.GetBytes(value)).Trim();
        }
        catch (FormatException)
        {
            return value;
        }
    }
}
