namespace Pulsar.Domain.Access;

/// <summary>Where a role applies. Inheritance: <see cref="Global"/> ⊃ <see cref="Node"/> ⊃ <see cref="Server"/>.</summary>
public enum PermissionScope
{
    Global,
    Node,
    Server,
}
