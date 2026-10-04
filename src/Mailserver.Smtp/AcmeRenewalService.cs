using Mailserver.Core;
using Mailserver.Core.Security;
using Mailserver.Core.Security.Acme;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailserver.Smtp;

/// <summary>
/// Requests the first Let's Encrypt certificate at startup (if there is none) and renews it in time. Checks twice a day;
/// after a failure it tries again every few hours, long before the certificate expires.
/// </summary>
public sealed class AcmeRenewalService(
    AcmeCertificateManager manager,
    CertificateProvider certificates,
    IOptions<MailserverOptions> options,
    ILogger<AcmeRenewalService> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromHours(3);
    private static readonly TimeSpan StartupLimit = TimeSpan.FromMinutes(3);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Without any certificate SMTP submission and IMAPS would stay closed, so the first one is fetched before they start.
        // When the web interface answers the challenge itself, it is not running yet; the loop below handles that case.
        if (options.Value.Tls.Acme.Enabled && !manager.ChallengesServedByWeb && certificates.GetCertificate() is null)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(StartupLimit);
            await RenewAsync("noch kein Zertifikat", limit.Token);
        }

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var success = true;
                if (manager.RenewalReason() is { } reason)
                {
                    success = await RenewAsync(reason, stoppingToken);
                }

                await Task.Delay(success ? CheckInterval : RetryInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> RenewAsync(string reason, CancellationToken cancellationToken)
    {
        logger.LogInformation("Requesting a Let's Encrypt certificate ({Reason})", reason);
        var status = await manager.IssueAsync(cancellationToken: cancellationToken);
        if (status.Success == true)
        {
            certificates.Invalidate();
            return true;
        }

        return false;
    }
}
