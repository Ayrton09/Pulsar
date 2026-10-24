using System.Security.Cryptography.X509Certificates;
using Pulsar.Agent.Core.Servers;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security.WinTrust;

namespace Pulsar.Agent.Windows.Servers;

/// <summary>
/// Authenticode, as Windows checks it (<c>WinVerifyTrust</c>): a signature that chains to a trusted root over a file
/// that was not changed since, and the organization (<c>O=</c>) of its signer's certificate. Revocation is not checked:
/// it needs the network at every check, the chain and the file's integrity do not.
/// </summary>
public sealed class AuthenticodeSignatures : ICodeSignatures
{
    private const string OrganizationOid = "2.5.4.10";

    public bool IsSignedBy(string path, string organization) => HasValidSignature(path) && SignerOrganization(path) == organization;

    /// <summary>The organization of the certificate that signed <paramref name="path"/>; call after <see cref="HasValidSignature"/>.</summary>
    public static string? SignerOrganization(string path)
    {
#pragma warning disable SYSLIB0057 // Reads the signer of a signed file, which X509CertificateLoader does not do.
        using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
        return signer.SubjectName.EnumerateRelativeDistinguishedNames()
            .Where(n => n.GetSingleElementType().Value == OrganizationOid)
            .Select(n => n.GetSingleElementValue())
            .SingleOrDefault();
    }

    /// <summary>The signer's certificate subject, for messages.</summary>
    public static string SignerSubject(string path)
    {
#pragma warning disable SYSLIB0057 // As above.
        using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
        return signer.Subject;
    }

    public static unsafe bool HasValidSignature(string path)
    {
        fixed (char* file = path)
        {
            var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)sizeof(WINTRUST_FILE_INFO), pcwszFilePath = file };
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)sizeof(WINTRUST_DATA),
                dwUIChoice = WINTRUST_DATA_UICHOICE.WTD_UI_NONE,
                // Revocation needs the network at every check; the chain and the file's integrity do not.
                fdwRevocationChecks = WINTRUST_DATA_REVOCATION_CHECKS.WTD_REVOKE_NONE,
                dwUnionChoice = WINTRUST_DATA_UNION_CHOICE.WTD_CHOICE_FILE,
                dwStateAction = WINTRUST_DATA_STATE_ACTION.WTD_STATEACTION_VERIFY,
            };
            data.Anonymous.pFile = &fileInfo;
            var action = PInvoke.WINTRUST_ACTION_GENERIC_VERIFY_V2;
            var result = PInvoke.WinVerifyTrust(HWND.Null, &action, &data);
            data.dwStateAction = WINTRUST_DATA_STATE_ACTION.WTD_STATEACTION_CLOSE;
            _ = PInvoke.WinVerifyTrust(HWND.Null, &action, &data);
            return result == 0;
        }
    }
}
