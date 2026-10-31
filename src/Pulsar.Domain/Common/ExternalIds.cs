namespace Pulsar.Domain.Common;

/// <summary>
/// What another system (a billing system, Pulsar 1.0.24) calls a user or a server, to find it again: 1 to 128 ASCII
/// letters, digits and <c>. _ - : @</c>, unique among users and among servers. Pulsar never reads anything into it.
/// </summary>
public static class ExternalIds
{
    public const int MaxLength = 128;

    public static DomainError Invalid { get; } =
        new("external_id.invalid", $"An external id is 1 to {MaxLength} letters, digits and . _ - : @ (no spaces).");

    public static bool IsValid(string? value) =>
        value is { Length: > 0 and <= MaxLength } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':' or '@');
}
