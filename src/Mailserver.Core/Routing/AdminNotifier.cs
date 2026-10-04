using Mailserver.Core.Accounts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Mailserver.Core.Routing;

/// <summary>Short mails from the server to all administrators (delivered locally), e.g. when a mailbox was locked.</summary>
public sealed class AdminNotifier(AccountStore accounts, MessageRouter router, IOptions<MailserverOptions> options, ILogger<AdminNotifier> logger)
{
    public async Task NotifyAsync(string subject, string text, CancellationToken cancellationToken = default)
    {
        var admins = accounts.ListAccounts().Where(a => a.IsAdmin && a.Enabled).Select(a => a.Address).ToList();
        if (admins.Count == 0)
        {
            logger.LogWarning("No administrator to notify: {Subject}", subject);
            return;
        }

        var message = new MimeMessage { Subject = $"[Mailserver] {subject}", Body = new TextPart("plain") { Text = text } };
        message.From.Add(new MailboxAddress("Mailserver", $"mailserver@{options.Value.Hostname}"));
        message.To.AddRange(admins.Select(a => new MailboxAddress(null, a.ToString())));
        message.Headers.Add("Auto-Submitted", "auto-generated");
        try
        {
            using var buffer = new MemoryStream();
            await message.WriteToAsync(buffer, cancellationToken);
            // Empty envelope sender: nothing answers or bounces this notice.
            await router.RouteAsync(buffer.ToArray(), "", admins, allowRelay: true, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            logger.LogError(ex, "Could not notify the administrators: {Subject}", subject);
        }
    }
}
