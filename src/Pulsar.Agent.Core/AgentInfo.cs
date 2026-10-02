using System.Reflection;

namespace Pulsar.Agent.Core;

/// <summary>What this agent build reports about itself.</summary>
public static class AgentInfo
{
    /// <summary>The product version without build metadata ("0.1.0", not "0.1.0+3f2a…").</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var informational = typeof(AgentInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
        {
            return "0.0.0";
        }

        var metadata = informational.IndexOf('+', StringComparison.Ordinal);
        return metadata < 0 ? informational : informational[..metadata];
    }
}
