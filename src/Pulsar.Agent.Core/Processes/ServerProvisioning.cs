using System.Text.RegularExpressions;

namespace Pulsar.Agent.Core.Processes;

/// <summary>
/// Prepares what a server needs on this machine before it can run (docs/architecture/05, "Full creation flow"): its
/// directory under the servers root and, outside development, its dedicated Windows account with access to that
/// directory only.
/// </summary>
public interface IServerProvisioner
{
    /// <summary>Creates or repairs the server's directory and account. Idempotent.</summary>
    /// <exception cref="GameProcessException">The machine cannot host servers (e.g. a domain controller).</exception>
    ProvisionedServer Provision(string serversRoot, string shortId);

    /// <summary>Where an already provisioned server lives and who it runs as, without changing anything.</summary>
    ProvisionedServer Describe(string serversRoot, string shortId);

    /// <summary>Deletes the directory (never following junctions or links out of it) and the account. Idempotent.</summary>
    void Deprovision(string serversRoot, string shortId);
}

/// <param name="Directory">Absolute path of the server directory.</param>
/// <param name="Identity">Who the server's processes run as.</param>
public sealed record ProvisionedServer(string Directory, ProcessIdentity Identity)
{
    /// <summary>
    /// Files and folders of the server no file operation reaches, nor anything in them, whatever name or link leads there;
    /// walks leave them out, and the folders that hold them cannot be moved (paths as <c>ServerFilePath</c>): the console
    /// log and the logs of a game that writes its RCON password there (GoldSrc), which only its console shows, masked.
    /// </summary>
    public IReadOnlyList<string> PrivateFiles { get; init; } = [];
}

public static partial class ServerShortId
{
    /// <summary>Short ids name folders and Windows accounts (<c>pulsar_&lt;shortId&gt;</c>, at most 20 characters).</summary>
    public static bool IsValid(string? shortId) => shortId is not null && Pattern().IsMatch(shortId);

    public static void Validate(string shortId)
    {
        if (!IsValid(shortId))
        {
            throw new ArgumentException($"'{shortId}' is not a valid server short id.", nameof(shortId));
        }
    }

    // \z, not $: $ also matches before a final line feed.
    [GeneratedRegex(@"^[a-z0-9]{4,12}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
