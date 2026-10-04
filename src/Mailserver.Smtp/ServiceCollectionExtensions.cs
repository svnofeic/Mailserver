using DnsClient;
using Mailserver.AntiSpam;
using Mailserver.Core;
using Mailserver.Core.Accounts;
using Mailserver.Core.Configuration;
using Mailserver.Core.Data;
using Mailserver.Core.Dkim;
using Mailserver.Core.Queue;
using Mailserver.Core.Routing;
using Mailserver.Core.Rules;
using Mailserver.Core.Security;
using Mailserver.Core.SpamLogging;
using Mailserver.Core.Storage;
using Mailserver.Smtp.Delivery;
using Mailserver.Smtp.Receiving;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Mailserver.Smtp;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers storage, routing, SMTP servers and the delivery queue.</summary>
    public static IServiceCollection AddMailserver(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MailserverOptions>(configuration.GetSection(MailserverOptions.SectionName));
        // Settings changed in the web interface apply without restart (see LiveOptions).
        services.AddSingleton<IOptions<MailserverOptions>, LiveOptions>();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp =>
        {
            var paths = new DataPaths(sp.GetRequiredService<IOptions<MailserverOptions>>().Value.DataDirectory);
            paths.EnsureCreated();
            return paths;
        });
        services.AddSingleton(sp =>
        {
            var database = new Database(sp.GetRequiredService<DataPaths>());
            database.Migrate();
            return database;
        });
        services.AddSingleton<AccountStore>();
        services.AddSingleton<MailboxStore>();
        services.AddSingleton<SentCopies>();
        services.AddSingleton<Mailserver.Core.Antivirus.MalwareFilter>();
        services.AddSingleton<OutboundQueue>();
        services.AddSingleton<DkimKeyStore>();
        services.AddSingleton<OutgoingMessagePreparer>();
        services.AddSingleton<RuleStore>();
        services.AddSingleton<SpamLog>();
        services.AddSingleton<SpamFeedback>();
        services.AddSingleton<MailboxSettingsStore>();
        services.AddSingleton<AutoResponder>();
        services.AddSingleton<MessageRouter>();
        services.AddSingleton<AdminNotifier>();
        services.AddSingleton<SendingLimiter>();
        services.AddSingleton<AuthThrottle>();
        services.AddSingleton<Mailserver.Core.Backup.BackupManager>();
        services.AddSingleton<Mailserver.Core.Security.Acme.AcmeChallengeStore>();
        services.AddSingleton<Mailserver.Core.Security.Acme.AcmeCertificateManager>();
        services.AddSingleton<CertificateProvider>();
        services.AddSingleton<ILookupClient>(_ => new LookupClient(new LookupClientOptions { UseCache = true, Timeout = TimeSpan.FromSeconds(10) }));
        services.AddSingleton<RemoteDeliveryClient>();
        services.AddSingleton<DeliveryService>();
        services.AddAntiSpam();

        // Before SMTP/IMAP: on a fresh installation the first certificate is requested before their TLS ports start.
        services.AddHostedService<AcmeRenewalService>();
        services.AddHostedService<SmtpHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<DeliveryService>());
        services.AddHostedService<BackupService>();
        return services;
    }
}
