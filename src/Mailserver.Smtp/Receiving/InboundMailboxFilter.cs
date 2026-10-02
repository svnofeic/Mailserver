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
/// Port 25: accepts mail for local recipients only. Never relays.
/// </summary>
internal sealed class InboundMailboxFilter(AccountStore accounts, MailboxStore mailboxes, IOptions<MailserverOptions> options) : IMailboxFilter
{
    public Task<bool> CanAcceptFromAsync(ISessionContext context, IMailbox from, int size, CancellationToken cancellationToken)
    {
        if (options.Value.Security.RejectUnauthenticatedLocalSender &&
            !string.IsNullOrEmpty(from.Host) && accounts.IsLocalDomain(from.Host))
        {
            throw Reject(SmtpReplyCode.MailboxUnavailable, "5.7.1 Use the submission port with authentication to send as a local domain");
        }

        return Task.FromResult(true);
    }

    public Task<bool> CanDeliverToAsync(ISessionContext context, IMailbox to, IMailbox from, CancellationToken cancellationToken)
    {
        if (!EmailAddress.TryParse(to.AsAddress(), out var recipient))
        {
            throw Reject(SmtpReplyCode.MailboxNameNotAllowed, "5.1.3 Invalid recipient address");
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

        return Task.FromResult(true);
    }

    private static SmtpResponseException Reject(SmtpReplyCode code, string message) => new(new SmtpResponse(code, message));
}
