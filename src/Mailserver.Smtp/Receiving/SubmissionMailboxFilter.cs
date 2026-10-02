using Mailserver.Core;
using Mailserver.Core.Accounts;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Protocol;
using SmtpServer.Storage;

namespace Mailserver.Smtp.Receiving;

/// <summary>
/// Ports 587/465: authenticated users may send to anyone, but only with an envelope sender they own.
/// </summary>
internal sealed class SubmissionMailboxFilter(AccountStore accounts) : IMailboxFilter
{
    public Task<bool> CanAcceptFromAsync(ISessionContext context, IMailbox from, int size, CancellationToken cancellationToken)
    {
        var account = GetAccount(context);
        if (!EmailAddress.TryParse(from.AsAddress(), out var sender) || !accounts.MaySendAs(account, sender))
        {
            throw new SmtpResponseException(new SmtpResponse(SmtpReplyCode.MailboxNameNotAllowed,
                $"5.7.1 {account.Address} is not allowed to send as <{from.AsAddress()}>"));
        }

        return Task.FromResult(true);
    }

    public Task<bool> CanDeliverToAsync(ISessionContext context, IMailbox to, IMailbox from, CancellationToken cancellationToken)
    {
        if (!EmailAddress.TryParse(to.AsAddress(), out var recipient))
        {
            throw new SmtpResponseException(new SmtpResponse(SmtpReplyCode.MailboxNameNotAllowed, "5.1.3 Invalid recipient address"));
        }

        if (accounts.IsLocalDomain(recipient.Domain) && accounts.Resolve(recipient).IsEmpty)
        {
            throw new SmtpResponseException(new SmtpResponse(SmtpReplyCode.MailboxUnavailable, "5.1.1 User unknown"));
        }

        return Task.FromResult(true);
    }

    internal Account GetAccount(ISessionContext context)
    {
        // AuthenticationRequired on the endpoint guarantees this; the check guards against misconfiguration.
        if (!context.Authentication.IsAuthenticated || !EmailAddress.TryParse(context.Authentication.User, out var address) ||
            accounts.FindAccount(address) is not { Enabled: true } account)
        {
            throw new SmtpResponseException(SmtpResponse.AuthenticationRequired);
        }

        return account;
    }
}
