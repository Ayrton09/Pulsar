namespace Pulsar.Domain.Access;

/// <summary>
/// What lets a user see a resource at all (docs/architecture/02): lists show it to them, and a request about it is
/// not answered 404. Holding any of these at the resource, or above it, is enough.
/// </summary>
public static class ResourceVisibility
{
    public static IReadOnlyList<string> Server { get; } = [Permissions.Server.View];

    /// <summary>What lets a user see a node's details (its machine, usage and credentials), not just its name and state.</summary>
    public static IReadOnlyList<string> NodeDetails { get; } = [Permissions.Node.View, Permissions.Node.Manage];

    /// <summary>
    /// A node's own permissions, or creating servers on it: the create dialog lists it, with its name and state only
    /// (<see cref="NodeDetails"/>).
    /// </summary>
    public static IReadOnlyList<string> Node { get; } = [.. NodeDetails, Permissions.Server.Create];
}
