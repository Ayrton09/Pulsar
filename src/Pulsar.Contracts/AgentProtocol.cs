namespace Pulsar.Contracts;

/// <summary>
/// Versioning and endpoints of the panel ↔ agent protocol (docs/architecture/03). The major version is also part of
/// the agent endpoints (<c>/agent/v1/...</c>); within a major version only additive changes are allowed.
/// </summary>
public static class AgentProtocol
{
    public const int CurrentVersion = 1;

    /// <summary>Oldest protocol version the panel still accepts (N-1 support, docs/architecture/03).</summary>
    public const int MinimumSupportedVersion = 1;

    public const string BasePath = "/agent/v1";
    public const string PanelIdentityPath = BasePath + "/panel-identity";
    public const string EnrollPath = BasePath + "/enroll";
    public const string ChallengePath = BasePath + "/auth/challenge";
    public const string TokenPath = BasePath + "/auth/token";
    public const string HubPath = BasePath + "/hub";
    public const string RotateCredentialPath = BasePath + "/credentials/rotate";

    /// <summary>
    /// File content between the panel and the agent (docs/architecture/03, "Large transfers"): <c>GET</c> what a
    /// browser uploads, <c>PUT</c> what a browser downloads. The ticket comes in a signed command and works once.
    /// </summary>
    public const string TransferPath = BasePath + "/transfers/{ticket}";

    public static string Transfer(string ticket) => TransferPath.Replace("{ticket}", Uri.EscapeDataString(ticket), StringComparison.Ordinal);

    /// <summary>
    /// The most panel key ids an agent says it pins (a sign-in, a Hello). A panel holds two keys at most (the active one and
    /// one being retired); each id is a <see cref="ProtocolCrypto.Fingerprint"/>.
    /// </summary>
    public const int MaxPinnedPanelKeys = 8;

    /// <summary>The longest panel key id an agent may name (a fingerprint is 50 characters).</summary>
    public const int MaxPanelKeyIdLength = 64;

    /// <summary>
    /// Whether <paramref name="keyIds"/> is a list of pinned panel key ids an agent may send: one to
    /// <see cref="MaxPinnedPanelKeys"/> ids, each <c>sha256:</c> and base64url, <see cref="MaxPanelKeyIdLength"/> at most.
    /// </summary>
    public static bool ArePinnedPanelKeyIds(IReadOnlyList<string>? keyIds) =>
        keyIds is { Count: > 0 and <= MaxPinnedPanelKeys } && keyIds.All(id => id is { Length: > 7 and <= MaxPanelKeyIdLength }
            && id.StartsWith("sha256:", StringComparison.Ordinal) && id.AsSpan(7).IndexOfAnyExcept(s_base64Url) < 0);

    private static readonly System.Buffers.SearchValues<char> s_base64Url =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_");

    /// <summary>Largest hub message either side accepts.</summary>
    public const long MaximumMessageSize = 1024 * 1024;

    /// <summary>Signature domains: a signature made for one purpose can never be replayed as another.</summary>
    public static class Purposes
    {
        public const string Enroll = "pulsar-enroll-v1";
        public const string Authenticate = "pulsar-agent-auth-v1";
        public const string EnrollmentResult = "pulsar-enrollment-result-v1";
        public const string Command = "pulsar-command-v1";
        public const string RotateCredential = "pulsar-rotate-credential-v1";
        public const string AuthorizeRotation = "pulsar-authorize-rotation-v1";
        public const string RotationResult = "pulsar-rotation-result-v1";
        public const string AgentsAddress = "pulsar-agents-address-v1";
        public const string SessionGrant = "pulsar-session-grant-v1";
    }
}
