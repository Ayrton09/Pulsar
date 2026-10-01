namespace Pulsar.Contracts;

/// <summary>
/// Versioning of the panel ↔ agent protocol. The major version is also part of the agent endpoints
/// (<c>/agent/v1/...</c>); within a major version only additive changes are allowed.
/// </summary>
public static class AgentProtocol
{
    public const int CurrentVersion = 1;
}
