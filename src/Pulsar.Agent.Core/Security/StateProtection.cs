namespace Pulsar.Agent.Core.Security;

/// <summary>
/// Encrypts state the agent keeps on disk that holds secrets (server records: secret variables, RCON passwords). The
/// state directory is already readable by SYSTEM and Administrators only; this keeps the secrets unreadable in copies of
/// it (backups, another machine).
/// </summary>
public interface IStateProtector
{
    /// <summary>Identifies the protection in stored files, so a record says how to read it back.</summary>
    string Name { get; }

    byte[] Protect(byte[] data);

    /// <exception cref="System.Security.Cryptography.CryptographicException">The data was not protected here, or was changed.</exception>
    byte[] Unprotect(byte[] data);
}

/// <summary>No encryption: tests, and platforms without a machine-bound protection.</summary>
public sealed class PlainStateProtector : IStateProtector
{
    public string Name => "none";

    public byte[] Protect(byte[] data) => data;

    public byte[] Unprotect(byte[] data) => data;
}
