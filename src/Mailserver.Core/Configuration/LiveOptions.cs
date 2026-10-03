using Microsoft.Extensions.Options;

namespace Mailserver.Core.Configuration;

/// <summary>
/// Makes <see cref="IOptions{TOptions}"/> return the current configuration instead of a snapshot from startup, so settings
/// saved in the web interface (data/settings.json) take effect without a restart. Values read only at startup – ports,
/// listen addresses, maximum message size – still need a restart.
/// </summary>
public sealed class LiveOptions(IOptionsMonitor<MailserverOptions> monitor) : IOptions<MailserverOptions>
{
    public MailserverOptions Value => monitor.CurrentValue;
}
