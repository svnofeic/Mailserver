using Mailserver.AntiSpam;
using Mailserver.Core;
using Mailserver.Core.Accounts;
using Mailserver.Core.Storage;
using Microsoft.Extensions.Options;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Protocol;
using SmtpServer.Storage;

namespace Mailserver.Smtp.Receiving;

/// <summary>
/// Port 25: accepts mail for local recipients only. Clients in <see cref="SmtpOptions.RelayNetworks"/> (local applications)
/// may relay without authentication and skip the spam checks.
/// </summary>
internal sealed class InboundMailboxFilter(AccountStore accounts, MailboxStore mailboxes, SpamFilter spamFilter, IOptions<MailserverOptions> options)
    : IMailboxFilter
{
    public async Task<bool> CanAcceptFromAsync(ISessionContext context, IMailbox from, int size, CancellationToken cancellationToken)
    {
        if (IsRelayClient(context))
        {
            return true;
        }

        var session = SessionInfo.GetInbound(context);
        if (await spamFilter.CheckConnectionAsync(session, cancellationToken) is { } blocked)
        {
            throw Reject(SmtpReplyCode.TransactionFailed, blocked);
        }

        if (options.Value.Security.RejectUnauthenticatedLocalSender &&
            !string.IsNullOrEmpty(from.Host) && accounts.IsLocalDomain(from.Host))
        {
            throw Reject(SmtpReplyCode.MailboxUnavailable, "5.7.1 Use the submission port with authentication to send as a local domain");
        }

        var sender = string.IsNullOrEmpty(from.User) ? "" : from.AsAddress();
        if (await spamFilter.CheckSenderAsync(session, sender, cancellationToken) is { } spfRejection)
        {
            throw Reject(SmtpReplyCode.MailboxUnavailable, spfRejection);
        }

        return true;
    }

    public Task<bool> CanDeliverToAsync(ISessionContext context, IMailbox to, IMailbox from, CancellationToken cancellationToken)
    {
        if (!EmailAddress.TryParse(to.AsAddress(), out var recipient))
        {
            throw Reject(SmtpReplyCode.MailboxNameNotAllowed, "5.1.3 Invalid recipient address");
        }

        if (IsRelayClient(context))
        {
            return Task.FromResult(true);
        }

        if (!accounts.IsLocalDomain(recipient.Domain))
        {
            throw Reject(SmtpReplyCode.MailboxUnavailable, "5.7.1 Relaying denied");
        }

        var resolution = accounts.Resolve(recipient);
        if (resolution.IsEmpty)
        {
            throw Reject(SmtpReplyCode.MailboxUnavailable, "5.1.1 User unknown");
        }

        if (resolution.ExternalAddresses.Count == 0 && resolution.LocalAccounts.All(a => mailboxes.IsOverQuota(a)))
        {
            throw Reject(SmtpReplyCode.InsufficientStorage, "4.2.2 Mailbox full");
        }

        if (!spamFilter.AcceptRecipient(SessionInfo.GetInbound(context), recipient.ToString()))
        {
            var delay = options.Value.Spam.Greylisting.Delay;
            throw Reject(SmtpReplyCode.Aborted, $"4.7.1 Greylisted, please try again in {Math.Max(1, (int)Math.Ceiling(delay.TotalMinutes))} minutes");
        }

        return Task.FromResult(true);
    }

    private bool IsRelayClient(ISessionContext context) => options.Value.Smtp.IsRelayClient(SessionInfo.GetRemoteAddress(context));

    private static SmtpResponseException Reject(SmtpReplyCode code, string message) => new(new SmtpResponse(code, message));
}
