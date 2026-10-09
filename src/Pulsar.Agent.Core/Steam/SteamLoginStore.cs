using System.Security.Cryptography;
using System.Text;
using Pulsar.Agent.Core.Security;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Steam;

/// <summary>
/// The login SteamCMD caches after a Steam account signed in (its <c>config/config.vdf</c>, which holds the token that
/// spares the password and the Steam Guard code next time), kept by the agent per account in its state directory
/// (SYSTEM and Administrators only), encrypted with <see cref="IStateProtector"/>. It is lent to a server's SteamCMD for
/// one run only: the game, its mods and the file manager never find it in the server's folder. It is kept with a hash
/// of the account's name and password: once the panel changes either, the old login is not used again.
/// </summary>
public sealed class SteamLoginStore(AgentPaths paths, IStateProtector protector)
{
    /// <summary>What SteamCMD writes its cached login to, relative to its own folder.</summary>
    public const string CachedLoginFile = "config/config.vdf";

    /// <summary>A cached login is a few kilobytes; anything far larger is not one.</summary>
    public const int MaxSize = 1024 * 1024;

    private const int StampSize = SHA256.HashSizeInBytes;

    /// <summary>The cached login of the account with these credentials; null when there is none or it cannot be read here.</summary>
    public byte[]? Load(SteamLogin login)
    {
        ArgumentNullException.ThrowIfNull(login);
        var file = PathOf(login.AccountId);
        if (!File.Exists(file))
        {
            return null;
        }

        byte[] content;
        try
        {
            content = protector.Unprotect(File.ReadAllBytes(file));
        }
        catch (CryptographicException)
        {
            File.Delete(file); // protected on another machine, or damaged: a new login replaces it
            return null;
        }

        if (content.Length <= StampSize || !CryptographicOperations.FixedTimeEquals(content.AsSpan(0, StampSize), Stamp(login)))
        {
            File.Delete(file); // cached for a name or password the account no longer has
            return null;
        }

        return content[StampSize..];
    }

    public void Save(SteamLogin login, byte[] cached)
    {
        ArgumentNullException.ThrowIfNull(login);
        ArgumentNullException.ThrowIfNull(cached);
        Directory.CreateDirectory(paths.SteamLoginsDirectory);
        var file = PathOf(login.AccountId);
        var temporary = file + ".tmp";
        File.WriteAllBytes(temporary, protector.Protect([.. Stamp(login), .. cached]));
        File.Move(temporary, file, overwrite: true);
    }

    public void Forget(Guid accountId)
    {
        if (Directory.Exists(paths.SteamLoginsDirectory))
        {
            File.Delete(PathOf(accountId));
        }
    }

    private static byte[] Stamp(SteamLogin login) => SHA256.HashData(Encoding.UTF8.GetBytes($"pulsar-steam-login\n{login.UserName}\n{login.Password}"));

    private string PathOf(Guid accountId) => Path.Combine(paths.SteamLoginsDirectory, $"{accountId:N}.bin");
}
