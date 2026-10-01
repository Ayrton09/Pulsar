using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.NetManagement;
using Windows.Win32.Security;
using Windows.Win32.Security.Authentication.Identity;
using Windows.Win32.UI.Shell;

namespace IsolatedLaunch;

/// <summary>A local account dedicated to one game server, as described in docs/architecture/06.</summary>
internal sealed class ServerAccount
{
    private const uint PolicyCreateAccount = 0x00000010;
    private const uint PolicyLookupNames = 0x00000800;
    private const uint ProfileNoUi = 0x00000001; // PI_NOUI

    private static readonly string[] s_grantedRights = ["SeBatchLogonRight"];
    private static readonly string[] s_deniedRights =
        ["SeDenyInteractiveLogonRight", "SeDenyRemoteInteractiveLogonRight", "SeDenyNetworkLogonRight"];

    private readonly string _password;

    private ServerAccount(string name, string password, SecurityIdentifier sid)
    {
        Name = name;
        _password = password;
        Sid = sid;
    }

    public string Name { get; }
    public SecurityIdentifier Sid { get; }

    public static unsafe ServerAccount Create(string name, Action<string> log)
    {
        // 64 random characters from a mixed alphabet satisfy common complexity policies.
        const string alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789!#%+-=?@";
        var password = "aA1!" + RandomNumberGenerator.GetString(alphabet, 60);

        fixed (char* namePtr = name)
        fixed (char* passwordPtr = password)
        {
            var info = new USER_INFO_1
            {
                usri1_name = namePtr,
                usri1_password = passwordPtr,
                usri1_priv = USER_PRIV.USER_PRIV_USER,
                usri1_flags = USER_ACCOUNT_FLAGS.UF_SCRIPT | USER_ACCOUNT_FLAGS.UF_DONT_EXPIRE_PASSWD | USER_ACCOUNT_FLAGS.UF_PASSWD_CANT_CHANGE,
            };
            uint parameterError;
            var status = PInvoke.NetUserAdd(null, 1, (byte*)&info, &parameterError);
            if (status != 0)
            {
                throw new InvalidOperationException($"NetUserAdd({name}) failed with {status} (parameter {parameterError})");
            }
        }

        var sid = (SecurityIdentifier)new NTAccount(name).Translate(typeof(SecurityIdentifier));
        var account = new ServerAccount(name, password, sid);
        log($"created account {name} ({sid})");

        account.RemoveFromUsersGroup(log);
        account.ChangeRights(add: true, log);
        return account;
    }

    public unsafe Logon LogOn(bool loadProfile, bool restrict, Action<string> log)
    {
        HANDLE token;
        fixed (char* namePtr = Name)
        fixed (char* domainPtr = ".")
        fixed (char* passwordPtr = _password)
        {
            if (!PInvoke.LogonUser(namePtr, domainPtr, passwordPtr, LOGON32_LOGON.LOGON32_LOGON_BATCH,
                    LOGON32_PROVIDER.LOGON32_PROVIDER_DEFAULT, &token))
            {
                throw new InvalidOperationException($"LogonUser({Name}, batch) failed: {Marshal.GetLastPInvokeErrorMessage()}");
            }
        }

        log($"LogonUser({Name}, LOGON32_LOGON_BATCH) OK");
        var profile = HANDLE.Null;
        void* environment = null;
        if (loadProfile)
        {
            fixed (char* namePtr = Name)
            {
                var info = new PROFILEINFOW { dwSize = (uint)sizeof(PROFILEINFOW), dwFlags = ProfileNoUi, lpUserName = namePtr };
                if (!PInvoke.LoadUserProfile(token, &info))
                {
                    throw new InvalidOperationException($"LoadUserProfile failed: {Marshal.GetLastPInvokeErrorMessage()}");
                }

                profile = info.hProfile;
            }

            if (!PInvoke.CreateEnvironmentBlock(&environment, token, false))
            {
                throw new InvalidOperationException($"CreateEnvironmentBlock failed: {Marshal.GetLastPInvokeErrorMessage()}");
            }

            log("LoadUserProfile + CreateEnvironmentBlock OK");
        }

        // The profile is loaded with the full token; only the child runs with the restricted one.
        // DISABLE_MAX_PRIVILEGE deletes every privilege except SeChangeNotifyPrivilege (e.g. SeShutdownPrivilege,
        // which Users hold by default on client editions).
        var restricted = HANDLE.Null;
        if (restrict)
        {
            if (!PInvoke.CreateRestrictedToken(token, CREATE_RESTRICTED_TOKEN_FLAGS.DISABLE_MAX_PRIVILEGE,
                    0, null, 0, null, 0, null, &restricted))
            {
                throw new InvalidOperationException($"CreateRestrictedToken failed: {Marshal.GetLastPInvokeErrorMessage()}");
            }

            log("CreateRestrictedToken(DISABLE_MAX_PRIVILEGE) OK");
        }

        return new Logon(token, restricted, profile, environment);
    }

    public unsafe void Delete(Action<string> log)
    {
        ChangeRights(add: false, log);

        fixed (char* sidPtr = Sid.Value)
        {
            // Fails harmlessly when no profile was ever loaded.
            log(PInvoke.DeleteProfile(sidPtr, null, null)
                ? $"deleted profile of {Name}"
                : $"DeleteProfile({Name}): {Marshal.GetLastPInvokeErrorMessage()}");
        }

        fixed (char* namePtr = Name)
        {
            var status = PInvoke.NetUserDel(null, namePtr);
            log(status == 0 ? $"deleted account {Name}" : $"NetUserDel({Name}) failed with {status}");
        }
    }

    // Finding (spike 2): NetUserAdd does not make the account a direct member (this returns 1377,
    // ERROR_MEMBER_NOT_IN_ALIAS), yet the token still carries BUILTIN\Users through its nested
    // NT AUTHORITY\Authenticated Users member. Kept to show it has no effect.
    private unsafe void RemoveFromUsersGroup(Action<string> log)
    {
        var users = (NTAccount)new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Translate(typeof(NTAccount));
        var groupName = users.Value.Split('\\')[^1]; // localized ("Users", "Usuarios", …)
        var member = $"{Environment.MachineName}\\{Name}";
        fixed (char* groupPtr = groupName)
        fixed (char* memberPtr = member)
        {
            var info = new LOCALGROUP_MEMBERS_INFO_3 { lgrmi3_domainandname = memberPtr };
            var status = PInvoke.NetLocalGroupDelMembers(null, groupPtr, 3, (byte*)&info, 1);
            log(status == 0 ? $"removed {Name} from {groupName}" : $"NetLocalGroupDelMembers({groupName}) returned {status}");
        }
    }

    private unsafe void ChangeRights(bool add, Action<string> log)
    {
        var attributes = new LSA_OBJECT_ATTRIBUTES { Length = (uint)sizeof(LSA_OBJECT_ATTRIBUTES) };
        LSA_HANDLE policy;
        var status = PInvoke.LsaOpenPolicy(null, &attributes, PolicyCreateAccount | PolicyLookupNames, &policy);
        if (status.Value != 0)
        {
            throw new InvalidOperationException($"LsaOpenPolicy failed: {PInvoke.LsaNtStatusToWinError(status)}");
        }

        try
        {
            var sidBytes = new byte[Sid.BinaryLength];
            Sid.GetBinaryForm(sidBytes, 0);
            fixed (byte* sidPtr = sidBytes)
            {
                var psid = new PSID(sidPtr);
                if (!add)
                {
                    status = PInvoke.LsaRemoveAccountRights(policy, psid, new BOOLEAN(1), null, 0);
                    log($"removed all user rights of {Name}: status {status.Value}");
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
                            throw new InvalidOperationException($"LsaAddAccountRights({right}) failed: {PInvoke.LsaNtStatusToWinError(status)}");
                        }
                    }
                }

                log($"granted {string.Join(", ", s_grantedRights)}; denied via {string.Join(", ", s_deniedRights)} for {Name}");
            }
        }
        finally
        {
            PInvoke.LsaClose(policy);
        }
    }
}

internal sealed unsafe class Logon(HANDLE token, HANDLE restricted, HANDLE profile, void* environment) : IDisposable
{
    /// <summary>The token the child is created with: the restricted one when requested.</summary>
    public nint Token => restricted != HANDLE.Null ? (nint)restricted.Value : (nint)token.Value;
    public nint Environment => (nint)environment;

    public void Dispose()
    {
        if (restricted != HANDLE.Null)
        {
            PInvoke.CloseHandle(restricted);
        }

        if (environment is not null)
        {
            PInvoke.DestroyEnvironmentBlock(environment);
        }

        if (profile != HANDLE.Null)
        {
            PInvoke.UnloadUserProfile(token, profile);
        }

        PInvoke.CloseHandle(token);
    }
}
