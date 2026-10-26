using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Pulsar.Agent.Core.Processes;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.NetManagement;
using Windows.Win32.Security;
using Windows.Win32.Security.Authentication.Identity;
using Windows.Win32.UI.Shell;

namespace Pulsar.Agent.Windows.Servers;

/// <summary>
/// One local account per game server (docs/architecture/06, ADR-0005): <c>pulsar_&lt;shortId&gt;</c>, allowed to log on
/// as a batch job and denied every interactive, remote desktop and network logon. Its password is never stored: a new
/// random one is set right before every logon (<see cref="LogOn"/>), so there is no secret at rest to steal or lose.
/// </summary>
/// <param name="logonLocks">Where the logon locks are (<see cref="LogonLock"/>): <c>AgentPaths.LogonLocksDirectory</c>.</param>
public sealed partial class WindowsServerAccounts(ILogger<WindowsServerAccounts> logger, string logonLocks)
{
    public const string Prefix = "pulsar_";

    private const uint PolicyCreateAccount = 0x00000010;
    private const uint PolicyLookupNames = 0x00000800;
    private const uint ProfileNoUi = 0x00000001; // PI_NOUI
    private const uint UserExists = 2224; // NERR_UserExists
    private const uint UserNotFound = 2221; // NERR_UserNotFound
    private const uint IncludeIndirect = 0x00000001; // LG_INCLUDE_INDIRECT
    private const uint PreferredLength = uint.MaxValue; // MAX_PREFERRED_LENGTH
    private const int ProfileDeleteAttempts = 4;

    private static readonly TimeSpan s_profileDeleteRetry = TimeSpan.FromSeconds(1);

    private static readonly string[] s_grantedRights = ["SeBatchLogonRight"];
    private static readonly string[] s_deniedRights =
        ["SeDenyInteractiveLogonRight", "SeDenyRemoteInteractiveLogonRight", "SeDenyNetworkLogonRight"];

    private static readonly System.Buffers.SearchValues<char> s_shortIdCharacters =
        System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789");

    public static string AccountName(string shortId) => Prefix + shortId;

    /// <summary>Raised with the account's name once <see cref="Delete"/> is done with it (the file manager drops its cached token).</summary>
    public event Action<string>? Deleted;

    /// <summary>Local accounts do not exist on a domain controller; the agent refuses to run servers there.</summary>
    public static bool IsDomainController()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ProductOptions");
        return string.Equals(key?.GetValue("ProductType") as string, "LanmanNT", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Creates the account (or takes over an existing one with that name, if <see cref="RefusalToAdopt"/> allows) and
    /// sets its logon rights.
    /// </summary>
    public unsafe SecurityIdentifier Ensure(string name, string description)
    {
        ValidateName(name);
        var password = NewPassword();
        fixed (char* namePtr = name)
        fixed (char* passwordPtr = password)
        fixed (char* commentPtr = description)
        {
            var info = new USER_INFO_1
            {
                usri1_name = namePtr,
                usri1_password = passwordPtr,
                usri1_priv = USER_PRIV.USER_PRIV_USER,
                usri1_comment = commentPtr,
                usri1_flags = USER_ACCOUNT_FLAGS.UF_SCRIPT | USER_ACCOUNT_FLAGS.UF_DONT_EXPIRE_PASSWD | USER_ACCOUNT_FLAGS.UF_PASSWD_CANT_CHANGE,
            };
            var status = PInvoke.NetUserAdd(null, 1, (byte*)&info, null);
            if (status == UserExists)
            {
                // Looked at before anything of it changes (its rights, its password).
                var (groups, comment) = ExistingAccount(name);
                if (RefusalToAdopt(groups, comment, description) is { } refusal)
                {
                    throw new GameProcessException("server.account_unusable",
                        $"The account {name} exists already and {refusal}: Pulsar does not run a server as it. Delete or rename that account.");
                }

                LogAccountExists(name);
            }
            else if (status != 0)
            {
                throw new Win32Exception((int)status, $"Cannot create the account {name}.");
            }
            else
            {
                LogAccountCreated(name);
            }
        }

        var sid = Sid(name);
        ChangeRights(sid, add: true);
        return sid;
    }

    /// <summary>Removes the account, its rights and its profile. False when it did not exist.</summary>
    public unsafe bool Delete(string name)
    {
        ValidateName(name);
        SecurityIdentifier sid;
        try
        {
            sid = Sid(name);
        }
        catch (IdentityNotMappedException)
        {
            DeleteLogonLock(name); // a delete retried after the account went, before its lock did
            Deleted?.Invoke(name);
            return false;
        }

        ChangeRights(sid, add: false);
        DeleteProfile(name, sid);

        fixed (char* namePtr = name)
        {
            var status = PInvoke.NetUserDel(null, namePtr);
            if (status is not 0 and not UserNotFound)
            {
                throw new Win32Exception((int)status, $"Cannot delete the account {name}.");
            }
        }

        LogAccountDeleted(name);
        DeleteLogonLock(name);
        Deleted?.Invoke(name);
        return true;
    }

    /// <summary>
    /// Why an existing account of a server's name is not taken over, or null when it may be: Pulsar made it (its comment
    /// is the one Pulsar sets: a provisioning that stopped halfway, a reinstalled agent, a server prepared again), and it
    /// is in no local group but Users (<paramref name="groups"/>; null for one whose SID is unknown), since the server's
    /// processes would keep an administrator's groups (their token drops privileges, not groups). Not the privilege level
    /// <c>NetUserGetInfo</c> reports: <c>NetUserAdd</c> puts Pulsar's accounts in no group (spike 0002), and Windows then
    /// calls them guests.
    /// </summary>
    internal static string? RefusalToAdopt(IReadOnlyCollection<SecurityIdentifier?> groups, string? comment, string description) =>
        groups.Any(g => g is null || !g.IsWellKnown(WellKnownSidType.BuiltinUsersSid)) ? "is in a local group besides Users (Administrators, say)"
        : !string.Equals(comment, description, StringComparison.Ordinal) ? "was not made by Pulsar"
        : null;

    private static unsafe (IReadOnlyCollection<SecurityIdentifier?> Groups, string? Comment) ExistingAccount(string name)
    {
        byte* buffer = null;
        string? comment;
        fixed (char* namePtr = name)
        {
            var status = PInvoke.NetUserGetInfo(null, namePtr, 1, &buffer);
            if (status != 0)
            {
                throw new Win32Exception((int)status, $"Cannot read the account {name}.");
            }
        }

        try
        {
            comment = ((USER_INFO_1*)buffer)->usri1_comment.ToString();
        }
        finally
        {
            _ = PInvoke.NetApiBufferFree(buffer);
        }

        // Its local groups, those it is in through a global group included.
        buffer = null;
        uint read;
        uint total;
        fixed (char* namePtr = name)
        {
            var status = PInvoke.NetUserGetLocalGroups(null, namePtr, 0, IncludeIndirect, &buffer, PreferredLength, &read, &total);
            if (status != 0)
            {
                throw new Win32Exception((int)status, $"Cannot read the groups of the account {name}.");
            }
        }

        try
        {
            var groups = new List<SecurityIdentifier?>();
            var entries = (LOCALGROUP_USERS_INFO_0*)buffer;
            for (var i = 0; i < read; i++)
            {
                groups.Add(GroupSid(entries[i].lgrui0_name.ToString()));
            }

            return (groups, comment);
        }
        finally
        {
            _ = PInvoke.NetApiBufferFree(buffer);
        }
    }

    private static SecurityIdentifier? GroupSid(string group)
    {
        try
        {
            return (SecurityIdentifier)new NTAccount(group).Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes the account's profile, its folder included. One never loaded has nothing to delete; one still in use (a
    /// process of the account just ending, the profile service unloading it) is tried again a few times. Once the account
    /// is gone nothing can name the profile any more, so a profile left behind is said (a warning), not kept quiet.
    /// </summary>
    private unsafe void DeleteProfile(string name, SecurityIdentifier sid)
    {
        fixed (char* sidPtr = sid.Value)
        {
            for (var attempt = 1; !PInvoke.DeleteProfile(sidPtr, null, null); attempt++)
            {
                var error = (WIN32_ERROR)Marshal.GetLastPInvokeError();
                if (error is WIN32_ERROR.ERROR_FILE_NOT_FOUND or WIN32_ERROR.ERROR_PATH_NOT_FOUND)
                {
                    return; // never loaded
                }

                if (attempt == ProfileDeleteAttempts)
                {
                    LogProfileNotDeleted(name, new Win32Exception((int)error).Message);
                    return;
                }

                Thread.Sleep(s_profileDeleteRetry);
            }
        }
    }

    private void DeleteLogonLock(string name)
    {
        try
        {
            File.Delete(Path.Combine(logonLocks, name + ".lock"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An empty file: left for the next time.
        }
    }

    public static bool Exists(string name)
    {
        try
        {
            _ = Sid(name);
            return true;
        }
        catch (IdentityNotMappedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Logs the account on as a batch job with a freshly set password, loads its profile and environment, and derives
    /// the token the server runs with: every privilege removed except SeChangeNotifyPrivilege (spike 0002).
    /// Keep the result alive while the process runs: disposing it unloads the profile.
    /// </summary>
    public unsafe AccountLogon LogOn(string name)
    {
        var token = LogOnBatch(name);
        var logon = new AccountLogon(token);
        try
        {
            fixed (char* namePtr = name)
            {
                var profile = new PROFILEINFOW { dwSize = (uint)sizeof(PROFILEINFOW), dwFlags = ProfileNoUi, lpUserName = namePtr };
                if (!PInvoke.LoadUserProfile(token, &profile))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), $"LoadUserProfile({name}) failed.");
                }

                logon.Profile = profile.hProfile;
            }

            void* environment;
            if (!PInvoke.CreateEnvironmentBlock(&environment, token, false))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateEnvironmentBlock failed.");
            }

            logon.Environment = ReadEnvironmentBlock((char*)environment);
            PInvoke.DestroyEnvironmentBlock(environment);

            // The profile is loaded with the full token; only the child runs with the restricted one.
            HANDLE restricted;
            if (!PInvoke.CreateRestrictedToken(token, CREATE_RESTRICTED_TOKEN_FLAGS.DISABLE_MAX_PRIVILEGE, 0, null, 0, null, 0, null, &restricted))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateRestrictedToken failed.");
            }

            logon.Restricted = restricted;
            logon.Sid = Sid(name);
            return logon;
        }
        catch
        {
            logon.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A token to act as the account in the agent's own threads (the file manager impersonates it, docs/architecture/08):
    /// logged on as a batch job with a freshly set password and stripped of its privileges like the server's processes.
    /// No profile is loaded. The caller owns the handle.
    /// </summary>
    public unsafe SafeAccessTokenHandle LogOnForImpersonation(string name)
    {
        var token = LogOnBatch(name);
        try
        {
            HANDLE restricted;
            if (!PInvoke.CreateRestrictedToken(token, CREATE_RESTRICTED_TOKEN_FLAGS.DISABLE_MAX_PRIVILEGE, 0, null, 0, null, 0, null, &restricted))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateRestrictedToken failed.");
            }

            return new SafeAccessTokenHandle(restricted);
        }
        finally
        {
            PInvoke.CloseHandle(token);
        }
    }

    private unsafe HANDLE LogOnBatch(string name)
    {
        ValidateName(name);
        var password = NewPassword();

        // Setting a password and logging on with it is one step: another logon in between (a server starting in its host
        // process while the agent's file manager acts as its account) would set its own password and make this one fail.
        // Hence a lock shared by the agent and the hosts, which no server account can reach (LogonLock).
        using (LogonLock.Acquire(logonLocks, name, TimeSpan.FromSeconds(30)))
        {
            fixed (char* namePtr = name)
            fixed (char* passwordPtr = password)
            {
                var info = new USER_INFO_1003 { usri1003_password = passwordPtr };
                var status = PInvoke.NetUserSetInfo(null, namePtr, 1003, (byte*)&info, null);
                if (status != 0)
                {
                    throw new Win32Exception((int)status, $"Cannot set the password of {name}.");
                }
            }

            HANDLE token;
            fixed (char* namePtr = name)
            fixed (char* domainPtr = ".")
            fixed (char* passwordPtr = password)
            {
                if (!PInvoke.LogonUser(namePtr, domainPtr, passwordPtr, LOGON32_LOGON.LOGON32_LOGON_BATCH, LOGON32_PROVIDER.LOGON32_PROVIDER_DEFAULT, &token))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), $"LogonUser({name}) failed.");
                }
            }

            return token;
        }
    }

    private static void ValidateName(string name)
    {
        // Local account names: at most 20 characters; ours are always the prefix plus a short id.
        if (!name.StartsWith(Prefix, StringComparison.Ordinal) || name.Length > 20
            || name.Length == Prefix.Length || name.AsSpan(Prefix.Length).ContainsAnyExcept(s_shortIdCharacters))
        {
            throw new ArgumentException($"'{name}' is not a Pulsar server account name.", nameof(name));
        }
    }

    internal static SecurityIdentifier Sid(string name) =>
        (SecurityIdentifier)new NTAccount(Environment.MachineName, name).Translate(typeof(SecurityIdentifier));

    private static string NewPassword()
    {
        // 64 random characters with every character class: satisfies any local complexity policy.
        const string alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789!#%+-=?@";
        return "aA1!" + RandomNumberGenerator.GetString(alphabet, 60);
    }

    private static unsafe Dictionary<string, string> ReadEnvironmentBlock(char* block)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (*block != '\0')
        {
            var entry = new string(block);
            block += entry.Length + 1;
            // Entries such as "=C:=C:\" (per-drive directories) start with '=' and are not variables.
            var separator = entry.IndexOf('=', 1);
            if (separator > 0)
            {
                variables[entry[..separator]] = entry[(separator + 1)..];
            }
        }

        return variables;
    }

    private unsafe void ChangeRights(SecurityIdentifier sid, bool add)
    {
        var attributes = new LSA_OBJECT_ATTRIBUTES { Length = (uint)sizeof(LSA_OBJECT_ATTRIBUTES) };
        LSA_HANDLE policy;
        var status = PInvoke.LsaOpenPolicy(null, &attributes, PolicyCreateAccount | PolicyLookupNames, &policy);
        if (status.Value != 0)
        {
            throw new Win32Exception((int)PInvoke.LsaNtStatusToWinError(status), "LsaOpenPolicy failed.");
        }

        try
        {
            var sidBytes = new byte[sid.BinaryLength];
            sid.GetBinaryForm(sidBytes, 0);
            fixed (byte* sidPtr = sidBytes)
            {
                var psid = new PSID(sidPtr);
                if (!add)
                {
                    // Removing every right of an account that has none returns "not found": nothing to do.
                    _ = PInvoke.LsaRemoveAccountRights(policy, psid, new BOOLEAN(1), null, 0);
                    return;
                }

                foreach (var right in s_grantedRights.Concat(s_deniedRights))
                {
                    fixed (char* rightPtr = right)
                    {
                        var unicode = new LSA_UNICODE_STRING
                        {
                            Buffer = rightPtr,
                            Length = (ushort)(right.Length * sizeof(char)),
                            MaximumLength = (ushort)(right.Length * sizeof(char)),
                        };
                        status = PInvoke.LsaAddAccountRights(policy, psid, &unicode, 1);
                        if (status.Value != 0)
                        {
                            throw new Win32Exception((int)PInvoke.LsaNtStatusToWinError(status), $"LsaAddAccountRights({right}) failed.");
                        }
                    }
                }
            }
        }
        finally
        {
            PInvoke.LsaClose(policy);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Created the local account {Account}")]
    private partial void LogAccountCreated(string account);

    [LoggerMessage(Level = LogLevel.Information, Message = "The local account {Account} already exists; reusing it")]
    private partial void LogAccountExists(string account);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted the local account {Account} and its profile")]
    private partial void LogAccountDeleted(string account);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The profile of {Account} could not be deleted ({Reason}): its profile folder stays, with what the server left there; delete it by hand")]
    private partial void LogProfileNotDeleted(string account, string reason);
}

/// <summary>A logged-on server account: the token to create its process with and its environment.</summary>
public sealed class AccountLogon : IDisposable
{
    private readonly HANDLE _token;
    private bool _disposed;

    internal AccountLogon(HANDLE token) => _token = token;

    internal HANDLE Profile { get; set; }

    internal HANDLE Restricted { get; set; }

    /// <summary>The token the server process is created with.</summary>
    internal HANDLE Token => Restricted;

    internal unsafe nint TokenHandle => (nint)Restricted.Value;

    public SecurityIdentifier Sid { get; internal set; } = null!;

    public IReadOnlyDictionary<string, string> Environment { get; internal set; } = new Dictionary<string, string>();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!Restricted.IsNull)
        {
            PInvoke.CloseHandle(Restricted);
            Restricted = HANDLE.Null;
        }

        if (!Profile.IsNull)
        {
            PInvoke.UnloadUserProfile(_token, Profile);
            Profile = HANDLE.Null;
        }

        PInvoke.CloseHandle(_token);
    }
}
