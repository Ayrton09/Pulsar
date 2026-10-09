using Pulsar.Domain.Common;

namespace Pulsar.Domain.Servers;

/// <summary>
/// A Steam account SteamCMD signs in with (Phase 13), for dedicated servers only an account that owns them may download
/// (and for servers someone simply chose one for). Its password is a secret: the application encrypts it before it
/// reaches the storage, and it goes to a node only inside the install and update commands that need it.
/// </summary>
public sealed class SteamAccount
{
    public const int MaxNameLength = 64;
    public const int MaxUserNameLength = 64;
    public const int MaxAccounts = 20;

    private SteamAccount()
    {
        // For EF Core materialization.
        Name = null!;
        UserName = null!;
        PasswordProtected = null!;
    }

    private SteamAccount(DateTimeOffset now)
        : this()
    {
        Id = SteamAccountId.New();
        CreatedAt = now;
    }

    public SteamAccountId Id { get; private set; }

    /// <summary>What the panel calls it ("Servers account").</summary>
    public string Name { get; private set; }

    /// <summary>The Steam account name it signs in with.</summary>
    public string UserName { get; private set; }

    public byte[] PasswordProtected { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Result<SteamAccount> Create(string name, string userName, byte[]? passwordProtected, DateTimeOffset now)
    {
        if (passwordProtected is null)
        {
            return SteamAccountErrors.PasswordRequired;
        }

        var account = new SteamAccount(now);
        var set = account.Change(name, userName, passwordProtected, now);
        return set.IsSuccess ? account : set.Error!;
    }

    /// <summary>Replaces the settings; a null <paramref name="passwordProtected"/> keeps the current password.</summary>
    public Result Change(string name, string userName, byte[]? passwordProtected, DateTimeOffset now)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxNameLength || trimmed.Any(char.IsControl))
        {
            return SteamAccountErrors.NameInvalid;
        }

        // Steam account names: letters, digits and underscores (3 to 64), never anything SteamCMD's command line would split.
        var user = userName?.Trim() ?? string.Empty;
        if (user.Length is < 3 or > MaxUserNameLength || !user.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            return SteamAccountErrors.UserNameInvalid;
        }

        Name = trimmed;
        UserName = user;
        if (passwordProtected is not null)
        {
            PasswordProtected = passwordProtected;
        }

        UpdatedAt = now;
        return Result.Success();
    }
}

public static class SteamAccountErrors
{
    public static DomainError NotFound { get; } = new("steam_account.not_found", "The Steam account does not exist.");

    public static DomainError NameInvalid { get; } =
        new("steam_account.name_invalid", $"A name is one line of 1 to {SteamAccount.MaxNameLength} characters.");

    public static DomainError UserNameInvalid { get; } =
        new("steam_account.user_name_invalid", "A Steam account name is 3 to 64 letters, digits and underscores.");

    public static DomainError PasswordRequired { get; } = new("steam_account.password_required", "The password is needed.");

    public static DomainError PasswordInvalid { get; } =
        new("steam_account.password_invalid", "A Steam password is 1 to 64 characters, without spaces or quotes.");

    public static DomainError PasswordUnreadable { get; } =
        new("steam_account.password_unreadable", "The account's password cannot be read with this panel's keys: set it again.");

    public static DomainError InUse { get; } = new("steam_account.in_use", "Servers use this Steam account: choose another one for them first.");

    public static DomainError TooMany { get; } = new("steam_account.too_many", $"The panel has at most {SteamAccount.MaxAccounts} Steam accounts.");

    public static DomainError Required { get; } =
        new("steamcmd.account_required", "Only a Steam account that owns this game's dedicated server may download it: choose one in the server's settings.");

    public static DomainError Unsupported { get; } =
        new("steam_account.unsupported", "The node's agent cannot sign SteamCMD in with a Steam account (update the agent).");

    public static DomainError GuardCodeInvalid { get; } =
        new("steam_account.guard_code_invalid", "A Steam Guard code is 5 letters or digits.");
}
