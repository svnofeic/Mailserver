using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Mailserver.Core;
using Mailserver.Core.Accounts;
using Mailserver.Core.Routing;
using Mailserver.Core.Security;
using Mailserver.Core.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmtpServer;
using SmtpServer.Storage;
using SmtpServerProvider = SmtpServer.ComponentModel.ServiceProvider;

namespace Mailserver.Smtp.Receiving;

/// <summary>
/// Runs two independent SMTP servers: the MX on port 25 (no AUTH, no relaying) and submission on 587/465 (AUTH required).
/// </summary>
public sealed class SmtpHostedService(
    IOptions<MailserverOptions> options,
    AccountStore accounts,
    MailboxStore mailboxes,
    MessageRouter router,
    OutgoingMessagePreparer preparer,
    AuthThrottle throttle,
    CertificateProvider certificates,
    ILoggerFactory loggerFactory) : BackgroundService
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<SmtpHostedService>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var certificate = certificates.GetCertificate();
        var certificateFactory = certificate is null ? null : new ReloadingCertificateFactory(certificates);
        var servers = new List<Task>();

        if (settings.Smtp.InboundPort > 0)
        {
            var server = CreateServer(settings, isSubmission: false, builder => AddEndpoints(builder, settings, settings.Smtp.InboundPort,
                implicitTls: false, requireAuth: false, certificateFactory));
            servers.Add(server.StartAsync(stoppingToken));
            _logger.LogInformation("Inbound SMTP listening on port {Port} (STARTTLS {Tls})", settings.Smtp.InboundPort,
                certificateFactory is null ? "disabled" : "enabled");
        }

        if (certificateFactory is null && !settings.Smtp.AllowInsecureAuthentication)
        {
            _logger.LogError("Submission ports are disabled: no TLS certificate configured. Passwords are never accepted without TLS.");
        }
        else
        {
            var submissionPorts = new List<(int Port, bool ImplicitTls)>();
            if (settings.Smtp.SubmissionPort > 0)
            {
                submissionPorts.Add((settings.Smtp.SubmissionPort, false));
            }

            if (settings.Smtp.SubmissionTlsPort > 0 && certificateFactory is not null)
            {
                submissionPorts.Add((settings.Smtp.SubmissionTlsPort, true));
            }

            if (submissionPorts.Count > 0)
            {
                var server = CreateServer(settings, isSubmission: true, builder =>
                {
                    foreach (var (port, implicitTls) in submissionPorts)
                    {
                        AddEndpoints(builder, settings, port, implicitTls, requireAuth: true, certificateFactory);
                    }
                });
                servers.Add(server.StartAsync(stoppingToken));
                _logger.LogInformation("Submission SMTP listening on port(s) {Ports}", string.Join(", ", submissionPorts.Select(p => p.Port)));
            }
        }

        try
        {
            await Task.WhenAll(servers);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private SmtpServer.SmtpServer CreateServer(MailserverOptions settings, bool isSubmission, Action<SmtpServerOptionsBuilder> configureEndpoints)
    {
        var builder = new SmtpServerOptionsBuilder()
            .ServerName(settings.Hostname)
            .MaxMessageSize(settings.MaxMessageSizeBytes, MaxMessageSizeHandling.Strict)
            .MaxAuthenticationAttempts(3)
            .CommandWaitTimeout(TimeSpan.FromMinutes(5));
        configureEndpoints(builder);

        var provider = new SmtpServerProvider();
        provider.Add(isSubmission
            ? new SubmissionAuthenticator(accounts, throttle, loggerFactory.CreateLogger<SubmissionAuthenticator>())
            : new RejectingAuthenticator());
        provider.Add(isSubmission
            ? new SubmissionMailboxFilter(accounts)
            : new InboundMailboxFilter(accounts, mailboxes, options));
        provider.Add((IMessageStore)new RoutingMessageStore(router, preparer, options, loggerFactory.CreateLogger<RoutingMessageStore>(), isSubmission));

        var server = new SmtpServer.SmtpServer(builder.Build(), provider);
        server.SessionCreated += (_, e) => SessionInfo.Track(e.Context);
        server.SessionFaulted += (_, e) => _logger.LogDebug(e.Exception, "SMTP session {Session} faulted", e.Context.SessionId);
        return server;
    }

    private static void AddEndpoints(SmtpServerOptionsBuilder builder, MailserverOptions settings, int port, bool implicitTls, bool requireAuth,
        ICertificateFactory? certificateFactory)
    {
        foreach (var address in settings.Smtp.ListenAddresses)
        {
            builder.Endpoint(endpoint =>
            {
                endpoint
                    .Endpoint(new IPEndPoint(IPAddress.Parse(address), port))
                    .IsSecure(implicitTls)
                    .AuthenticationRequired(requireAuth)
                    .AllowUnsecureAuthentication(requireAuth && settings.Smtp.AllowInsecureAuthentication)
                    .SessionTimeout(settings.Smtp.SessionTimeout)
                    // None = operating system defaults. Windows Server 2016 has no TLS 1.3, and requesting it there fails the
                    // handshake; old protocol versions are disabled system-wide instead (see docs).
                    .SupportedSslProtocols(SslProtocols.None);
                if (certificateFactory is not null)
                {
                    endpoint.Certificate(certificateFactory);
                }
            });
        }
    }

    private sealed class ReloadingCertificateFactory(CertificateProvider provider) : ICertificateFactory
    {
        public X509Certificate GetServerCertificate(ISessionContext sessionContext) =>
            provider.GetCertificate() ?? throw new InvalidOperationException("TLS certificate is no longer available.");
    }
}
