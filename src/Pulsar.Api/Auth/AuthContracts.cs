using System.ComponentModel.DataAnnotations;

namespace Pulsar.Api.Auth;

/// <summary>
/// What the SPA needs at startup: whether the panel still needs its first administrator, who is signed in, and
/// whether this connection is unencrypted (plain HTTP allowed by Security:AllowInsecureHttp), to warn the user.
/// </summary>
public sealed record SessionResponse(bool SetupRequired, CurrentUserResponse? User, bool InsecureConnection);

/// <summary>
/// The signed-in user and their global permissions (the UI hides what the user cannot do; the API enforces it).
/// <see cref="TwoFactorSetupRequired"/>: a role requires two-factor authentication and the user has not set it up;
/// until they do, only the session, logout and setting it up are open to them.
/// </summary>
/// <param name="Permissions">Permissions held globally (everywhere).</param>
/// <param name="NodePermissions">Permissions held on single nodes, and so on their servers.</param>
/// <param name="ServerPermissions">Permissions held on single servers.</param>
public sealed record CurrentUserResponse(
    Guid Id,
    string UserName,
    string? DisplayName,
    string? Email,
    IReadOnlyList<string> Permissions,
    bool TwoFactorEnabled,
    bool TwoFactorSetupRequired,
    IReadOnlyList<ScopedPermissions> NodePermissions,
    IReadOnlyList<ScopedPermissions> ServerPermissions);

/// <summary>What a user may do on one node or one server, besides their global permissions.</summary>
public sealed record ScopedPermissions(Guid Id, IReadOnlyList<string> Permissions);

/// <summary>
/// The answer to a correct password: the signed-in user, or <see cref="TwoFactorRequired"/> when the user still has to
/// give an authenticator or recovery code (POST /auth/login/two-factor, within five minutes).
/// </summary>
public sealed record LoginResponse(bool TwoFactorRequired, CurrentUserResponse? User);

/// <summary>The second sign-in step: a code from the authenticator app, or one of the recovery codes.</summary>
public sealed record TwoFactorLoginRequest(
    [property: StringLength(16)] string? Code,
    [property: StringLength(32)] string? RecoveryCode);

public sealed record TwoFactorStatusResponse(bool Enabled, bool Required, int RecoveryCodesLeft);

/// <summary>A new authenticator key, as text (in groups of four) and as the otpauth URI the QR code carries.</summary>
public sealed record TwoFactorSetupResponse(string SharedKey, string AuthenticatorUri);

/// <summary>A code from the app and the current password: a stolen session alone cannot turn it on.</summary>
public sealed record EnableTwoFactorRequest(
    [property: Required, StringLength(16)] string Code,
    [property: Required, StringLength(256)] string Password);

/// <summary>Shown once: the panel keeps only their hashes.</summary>
public sealed record RecoveryCodesResponse(IReadOnlyList<string> RecoveryCodes);

/// <summary>The current password, to confirm setting up or turning off two-factor authentication, or renewing the recovery codes.</summary>
public sealed record PasswordConfirmationRequest([property: Required, StringLength(256)] string Password);

public sealed record UpdateProfileRequest(
    [property: StringLength(128)] string? DisplayName,
    [property: EmailAddress, StringLength(256)] string? Email);

public sealed record ChangePasswordRequest(
    [property: Required, StringLength(256)] string CurrentPassword,
    [property: Required, StringLength(256)] string NewPassword);

public sealed record LoginRequest(
    [property: Required, StringLength(64)] string UserName,
    [property: Required, StringLength(256)] string Password);

public sealed record SetupRequest(
    [property: Required, StringLength(128)] string Token,
    [property: Required, StringLength(64, MinimumLength = 1)] string UserName,
    [property: Required, StringLength(256)] string Password,
    [property: StringLength(128)] string? DisplayName,
    [property: EmailAddress, StringLength(256)] string? Email);
