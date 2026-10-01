using System.ComponentModel.DataAnnotations;

namespace Pulsar.Api.Auth;

/// <summary>What the SPA needs at startup: whether the panel still needs its first administrator, and who is signed in.</summary>
public sealed record SessionResponse(bool SetupRequired, CurrentUserResponse? User);

/// <summary>The signed-in user and their global permissions (the UI hides what the user cannot do; the API enforces it).</summary>
public sealed record CurrentUserResponse(Guid Id, string UserName, string? DisplayName, IReadOnlyList<string> Permissions);

public sealed record LoginRequest(
    [property: Required, StringLength(64)] string UserName,
    [property: Required, StringLength(256)] string Password);

public sealed record SetupRequest(
    [property: Required, StringLength(128)] string Token,
    [property: Required, StringLength(64, MinimumLength = 1)] string UserName,
    [property: Required, StringLength(256)] string Password,
    [property: StringLength(128)] string? DisplayName,
    [property: EmailAddress, StringLength(256)] string? Email);
