using Pulsar.Domain.Common;

namespace Pulsar.Domain.Nodes;

/// <summary>Errors of the nodes module. Codes are part of the API contract (ProblemDetails "code").</summary>
public static class NodeErrors
{
    public static DomainError NameInvalid { get; } =
        new("node.name_invalid", $"The node name must have between 1 and {Node.MaxNameLength} characters.");

    public static DomainError DescriptionTooLong { get; } =
        new("node.description_too_long", $"The description must have at most {Node.MaxDescriptionLength} characters.");

    public static DomainError PublicHostInvalid { get; } =
        new("node.public_host_invalid", "The public host must be a host name or an IP address.");

    public static DomainError NameTaken { get; } = new("node.name_taken", "Another node already has this name.");

    public static DomainError NotFound { get; } = new("node.not_found", "The node does not exist.");

    public static DomainError NotConnected { get; } = new("node.not_connected", "The node's agent is not connected.");
}
