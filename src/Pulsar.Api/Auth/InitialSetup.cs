using System.Security.Cryptography;
using System.Text;

namespace Pulsar.Api.Auth;

/// <summary>
/// Protects the first-administrator setup (docs/architecture/08): while no user exists, the panel prints a one-time
/// token to its log, and <c>POST /api/v1/auth/setup</c> only succeeds with it. This stops anyone else on the network
/// from claiming a freshly installed panel. Only a hash of the token is kept in memory; a restart issues a new one.
/// </summary>
public sealed partial class InitialSetup(ILogger<InitialSetup> logger)
{
    private byte[]? _tokenHash;

    /// <summary>Serializes setup attempts so two requests cannot both create a first administrator.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>Issues a new token (replacing any previous one), logs it and returns it.</summary>
    public string IssueToken()
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(24));
        Volatile.Write(ref _tokenHash, Hash(token));
        LogSetupToken(token);
        return token;
    }

    public bool IsValid(string candidate)
    {
        var expected = Volatile.Read(ref _tokenHash);
        return expected is not null && CryptographicOperations.FixedTimeEquals(expected, Hash(candidate));
    }

    public void Complete() => Volatile.Write(ref _tokenHash, null);

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "No users exist yet. Open the panel and complete the initial setup with this one-time token: {SetupToken}")]
    private partial void LogSetupToken(string setupToken);
}
