using Pulsar.Domain.Common;

namespace Pulsar.Domain.Nodes;

/// <summary>Errors of the nodes module. Codes are part of the API contract (ProblemDetails "code").</summary>
public static class NodeErrors
{
    public static DomainError NameInvalid { get; } =
        new("node.name_invalid", $"The node name is one line of 1 to {Node.MaxNameLength} characters, with no invisible ones.");

    public static DomainError DescriptionTooLong { get; } =
        new("node.description_too_long", $"The description must have at most {Node.MaxDescriptionLength} characters.");

    public static DomainError PublicHostInvalid { get; } =
        new("node.public_host_invalid", "The public host must be a host name or an IP address.");

    public static DomainError NameTaken { get; } = new("node.name_taken", "Another node already has this name.");

    public static DomainError NotFound { get; } = new("node.not_found", "The node does not exist.");

    public static DomainError ApprovalBatchInvalid { get; } = new("node.approval_batch_invalid", "Approve 1 to 200 nodes at a time.");

    public static DomainError NotConnected { get; } = new("node.not_connected", "The node's agent is not connected.");

    public static DomainError HasServers { get; } = new("node.has_servers", "Delete the node's servers first.");

    public static DomainError DiskLow { get; } = new("node.disk_low",
        $"The node's drive would have less than {DiskFloor.MinFreePercent}% or {DiskFloor.MinFreeBytes >> 30} GB free: make room on it first.");

    public static DomainError AllocationAddressInvalid { get; } =
        new("node.allocation_address_invalid", "The address must be an IPv4 address (0.0.0.0 for every interface).");

    public static DomainError AllocationPortInvalid { get; } = new("node.allocation_port_invalid", "Ports go from 1 to 65535.");

    public static DomainError AllocationRangeTooLarge { get; } = new("node.allocation_range_too_large", "At most 1000 ports at a time.");

    public static DomainError AllocationNotesTooLong { get; } =
        new("node.allocation_notes_too_long", $"Notes have at most {NodeAllocation.MaxNotesLength} characters.");

    public static DomainError AllocationTaken { get; } =
        new("node.allocation_taken", "The node already has an allocation on that address and port (or on every interface).");

    public static DomainError AllocationNotFound { get; } = new("node.allocation_not_found", "The allocation does not exist.");

    public static DomainError AllocationInUse { get; } = new("node.allocation_in_use", "The allocation is assigned to a server.");
}
