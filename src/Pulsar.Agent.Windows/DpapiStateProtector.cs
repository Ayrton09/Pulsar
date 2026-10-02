using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Pulsar.Agent.Core.Security;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security.Cryptography;

namespace Pulsar.Agent.Windows;

/// <summary>
/// DPAPI with the machine's key (<c>CRYPTPROTECT_LOCAL_MACHINE</c>): the agent's state can be decrypted only on this
/// machine. Any process of the machine could call DPAPI too, which is why the files also stay readable by SYSTEM and
/// Administrators only; the encryption protects copies of them (docs/architecture/08, "Secrets").
/// </summary>
public sealed class DpapiStateProtector : IStateProtector
{
    private const uint LocalMachine = 0x4;
    private const uint UiForbidden = 0x1;

    private static readonly byte[] s_entropy = "Pulsar.Agent.State.v1"u8.ToArray();

    public string Name => "dpapi-machine";

    public byte[] Protect(byte[] data) => Transform(data, protect: true);

    public byte[] Unprotect(byte[] data) => Transform(data, protect: false);

    private static unsafe byte[] Transform(byte[] data, bool protect)
    {
        ArgumentNullException.ThrowIfNull(data);
        fixed (byte* input = data)
        fixed (byte* entropy = s_entropy)
        {
            var inBlob = new CRYPT_INTEGER_BLOB { cbData = (uint)data.Length, pbData = input };
            var entropyBlob = new CRYPT_INTEGER_BLOB { cbData = (uint)s_entropy.Length, pbData = entropy };
            CRYPT_INTEGER_BLOB outBlob = default;
            var ok = protect
                ? PInvoke.CryptProtectData(&inBlob, null, &entropyBlob, null, null, LocalMachine | UiForbidden, &outBlob)
                : PInvoke.CryptUnprotectData(&inBlob, null, &entropyBlob, null, null, UiForbidden, &outBlob);
            if (!ok)
            {
                throw new CryptographicException($"DPAPI failed: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
            }

            try
            {
                return new ReadOnlySpan<byte>(outBlob.pbData, (int)outBlob.cbData).ToArray();
            }
            finally
            {
                PInvoke.LocalFree((HLOCAL)outBlob.pbData);
            }
        }
    }
}
