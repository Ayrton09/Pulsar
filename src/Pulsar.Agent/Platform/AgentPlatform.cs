namespace Pulsar.Agent.Platform;

/// <summary>The platform this build of the agent is for: Windows' for <c>net10.0-windows</c>, Linux' for <c>net10.0</c>.</summary>
internal static class AgentPlatform
{
#if WINDOWS
    public static IAgentPlatform Current { get; } = new WindowsAgentPlatform();
#else
    public static IAgentPlatform Current { get; } = new LinuxAgentPlatform();
#endif
}
