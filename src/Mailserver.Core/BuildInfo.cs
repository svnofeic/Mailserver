using System.Reflection;

namespace Mailserver.Core;

/// <summary>Version of the running build, e.g. "1.0.15 (ed4089e)". Set by CI via -p:Version and the commit hash.</summary>
public static class BuildInfo
{
    public static string Version { get; } = Read();

    private static string Read()
    {
        var version = (Assembly.GetEntryAssembly() ?? typeof(BuildInfo).Assembly)
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unbekannt";
        var plus = version.IndexOf('+');
        return plus < 0 ? version : $"{version[..plus]} ({version[(plus + 1)..][..Math.Min(7, version.Length - plus - 1)]})";
    }
}
