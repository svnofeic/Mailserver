using Mailserver.Core.Accounts;
using Mailserver.Core.Security;
using Microsoft.Extensions.Logging;
using SmtpServer;
using SmtpServer.Authentication;
using SmtpServer.Protocol;

namespace Mailserver.Smtp.Receiving;

internal sealed class SubmissionAuthenticator(AccountStore accounts, AuthThrottle throttle, ILogger<SubmissionAuthenticator> logger)
    : IUserAuthenticator
{
    public Task<bool> AuthenticateAsync(ISessionContext context, string user, string password, CancellationToken cancellationToken)
    {
        var ip = SessionInfo.GetRemoteAddress(context);
        if (throttle.IsLockedOut(ip))
        {
            logger.LogWarning("Rejected login for {User} from locked-out address {Ip}", user, ip);
            throw new SmtpResponseException(new SmtpResponse(SmtpReplyCode.ServiceUnavailable, "4.7.0 Too many failed logins, try again later"), true);
        }

        if (accounts.Authenticate(user, password) is not null)
        {
            throttle.RecordSuccess(ip);
            logger.LogInformation("User {User} authenticated from {Ip}", user, ip);
            return Task.FromResult(true);
        }

        throttle.RecordFailure(ip);
        logger.LogWarning("Failed login for {User} from {Ip}", user, ip);
        return Task.FromResult(false);
    }
}

/// <summary>Port 25 never accepts logins; this replaces the library default.</summary>
internal sealed class RejectingAuthenticator : IUserAuthenticator
{
    public Task<bool> AuthenticateAsync(ISessionContext context, string user, string password, CancellationToken cancellationToken) =>
        Task.FromResult(false);
}
