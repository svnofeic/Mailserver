using Mailserver.AntiSpam.Checks;
using Mailserver.AntiSpam.Dns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mailserver.AntiSpam;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAntiSpam(this IServiceCollection services)
    {
        services.TryAddSingleton<IDnsResolver, DnsClientResolver>();
        services.AddSingleton<SpfChecker>();
        services.AddSingleton<DkimChecker>();
        services.AddSingleton<DmarcChecker>();
        services.AddSingleton<DnsBlocklistChecker>();
        services.AddSingleton<Greylist>();
        services.AddSingleton<SpamFilter>();
        return services;
    }
}
