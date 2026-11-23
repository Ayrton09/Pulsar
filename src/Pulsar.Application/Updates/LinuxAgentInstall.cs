using Pulsar.Contracts;

namespace Pulsar.Application.Updates;

/// <summary>
/// How a Linux machine becomes a node: the release's install.sh, run as root with <c>--agent</c>, downloads the release
/// from the project's GitHub, checks it against its signed manifest and runs install-pulsar-agent.sh, which asks for the
/// node code (docs/guides/install.md, "Linux nodes").
/// </summary>
public static class LinuxAgentInstall
{
    /// <summary>
    /// The command for this panel's own release, so the agent is the panel's version; a development build (a pre-release)
    /// names the newest release instead, as there is none of its own.
    /// </summary>
    public static string Command((UpdateVersion Version, bool PreRelease) panel) => panel.PreRelease
        ? $"curl -fsSL https://github.com/{UpdateRelease.Repository}/releases/latest/download/install.sh | sudo bash -s -- --agent"
        : $"curl -fsSL https://github.com/{UpdateRelease.Repository}/releases/download/v{panel.Version}/install.sh | sudo bash -s -- --agent --version {panel.Version}";
}
