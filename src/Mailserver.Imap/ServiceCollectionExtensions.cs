using Mailserver.Imap.Session;
using Microsoft.Extensions.DependencyInjection;

namespace Mailserver.Imap;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the IMAP server. Requires the core services (storage, accounts, certificates) to be registered.</summary>
    public static IServiceCollection AddImapServer(this IServiceCollection services)
    {
        services.AddSingleton<FolderWatcher>();
        services.AddHostedService<ImapServer>();
        return services;
    }
}
