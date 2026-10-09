using System.Security.Cryptography;
using Pulsar.Agent.Core.Security;

namespace Pulsar.Agent.Core.Steam;

/// <summary>
/// The login SteamCMD caches after a Steam account signed in (its <c>config/config.vdf</c>, which holds the token that
/// spares the password and the Steam Guard code next time), kept by the agent per account in its state directory
/// (SYSTEM and Administrators only), encrypted with <see cref="IStateProtector"/>. It is lent to a server's SteamCMD for
/// one run only: the game, its mods and the file manager never find it in the server's folder.
/// </summary>
public sealed class SteamLoginStore(AgentPaths paths, IStateProtector protector)
{
    /// <summary>What SteamCMD writes its cached login to, relative to its own folder.</summary>
    public const string CachedLoginFile = "config/config.vdf";

    /// <summary>A cached login is a few kilobytes; anything far larger is not one.</summary>
    public const int MaxSize = 1024 * 1024;

    /// <summary>The cached login of the account; null when there is none or it cannot be read here.</summary>
    public byte[]? Load(Guid accountId)
    {
        var file = PathOf(accountId);
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            return protector.Unprotect(File.ReadAllBytes(file));
        }
        catch (CryptographicException)
        {
            File.Delete(file); // protected on another machine, or damaged: a new login replaces it
            return null;
        }
    }

    public void Save(Guid accountId, byte[] login)
    {
        ArgumentNullException.ThrowIfNull(login);
        Directory.CreateDirectory(paths.SteamLoginsDirectory);
        var file = PathOf(accountId);
        var temporary = file + ".tmp";
        File.WriteAllBytes(temporary, protector.Protect(login));
        File.Move(temporary, file, overwrite: true);
    }

    public void Forget(Guid accountId)
    {
        if (Directory.Exists(paths.SteamLoginsDirectory))
        {
            File.Delete(PathOf(accountId));
        }
    }

    private string PathOf(Guid accountId) => Path.Combine(paths.SteamLoginsDirectory, $"{accountId:N}.bin");
}
