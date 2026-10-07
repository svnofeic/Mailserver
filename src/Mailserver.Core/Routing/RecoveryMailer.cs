using Mailserver.Core.Accounts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Mailserver.Core.Routing;

/// <summary>The mails of "Passwort vergessen": confirmation of the external address, the reset link, and the notice afterwards.</summary>
public sealed class RecoveryMailer(MessageRouter router, OutgoingMessagePreparer preparer, IOptions<MailserverOptions> options,
    ILogger<RecoveryMailer> logger)
{
    /// <summary>Base address of the web interface for links, from the configuration only.</summary>
    public string WebUrl =>
        options.Value.Web.PublicUrl is { Length: > 0 } url
            ? url.TrimEnd('/')
            : $"https://{options.Value.Hostname}{(options.Value.Web.HttpsPort == 443 ? "" : $":{options.Value.Web.HttpsPort}")}";

    public Task SendConfirmationAsync(Account account, string address, string token, CancellationToken cancellationToken = default) =>
        SendAsync(account, address, "Ersatz-Adresse bestätigen",
            $"""
             Hallo,

             diese Adresse wurde als Ersatz-Adresse für das Postfach {account.Address} eingetragen. Wer das Passwort des Postfachs
             vergisst, bekommt den Link zum Zurücksetzen dann an diese Adresse.

             Bitte bestätigen (gültig {PasswordRecovery.ConfirmLifetime.TotalHours:0} Stunden):
             {WebUrl}/Recovery/Confirm?token={token}

             Wenn Sie das nicht veranlasst haben, ignorieren Sie diese Mail – die Adresse wird dann nicht verwendet.
             """, cancellationToken);

    public Task SendResetAsync(Account account, string address, string token, CancellationToken cancellationToken = default) =>
        SendAsync(account, address, "Passwort zurücksetzen",
            $"""
             Hallo,

             für das Postfach {account.Address} wurde ein neues Passwort angefordert. Über diesen Link lässt es sich setzen
             (gültig {PasswordRecovery.ResetLifetime.TotalMinutes:0} Minuten, nur einmal verwendbar):
             {WebUrl}/Recovery/Reset?token={token}

             Wenn Sie das nicht waren, ignorieren Sie diese Mail – Ihr Passwort bleibt dann unverändert.
             """, cancellationToken);

    /// <summary>Delivered into the mailbox itself, so the owner notices a reset someone else made.</summary>
    public Task NotifyResetAsync(Account account, string? ip, CancellationToken cancellationToken = default) =>
        SendAsync(account, account.Address.ToString(), "Ihr Passwort wurde zurückgesetzt",
            $"""
             Hallo,

             das Passwort des Postfachs {account.Address} wurde am {DateTimeOffset.Now:dd.MM.yyyy} um {DateTimeOffset.Now:HH:mm} Uhr über
             „Passwort vergessen“ neu gesetzt{(ip is null ? "" : $" (von der Adresse {ip})")}. Alle Geräte brauchen ab jetzt das neue Passwort.

             Wenn Sie das nicht waren, wenden Sie sich bitte sofort an Ihren Administrator.
             """, cancellationToken);

    private async Task SendAsync(Account account, string to, string subject, string text, CancellationToken cancellationToken)
    {
        // From the mailbox's own domain, so the mail is signed with its DKIM key and passes DMARC at the recipient.
        var message = new MimeMessage { Subject = subject, Body = new TextPart("plain") { Text = text } };
        message.From.Add(new MailboxAddress("Mailserver", $"noreply@{account.Address.Domain}"));
        message.To.Add(MailboxAddress.Parse(to));
        message.Headers.Add("Auto-Submitted", "auto-generated");
        try
        {
            using var buffer = new MemoryStream();
            await message.WriteToAsync(buffer, cancellationToken);
            var prepared = await preparer.PrepareAsync(buffer.ToArray(), cancellationToken);
            // Empty envelope sender: nothing answers or bounces these notices.
            await router.RouteAsync(prepared, "", [EmailAddress.Parse(to)], allowRelay: true, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or FormatException or Microsoft.Data.Sqlite.SqliteException)
        {
            logger.LogError(ex, "Could not send \"{Subject}\" for {Account}", subject, account.Address);
        }
    }
}
