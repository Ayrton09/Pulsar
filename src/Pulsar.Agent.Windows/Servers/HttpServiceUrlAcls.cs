using System.ComponentModel;
using System.Runtime.Versioning;
using System.Security.Principal;
using Pulsar.Agent.Core.Processes;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Networking.HttpServer;

namespace Pulsar.Agent.Windows.Servers;

/// <summary>
/// The HTTP Server API's URL reservations (what <c>netsh http add/delete/show urlacl</c> works on), for
/// <see cref="HttpUrlReservations"/>: changing them needs an administrator, which the agent's service is (SYSTEM).
/// </summary>
[SupportedOSPlatform("windows6.0.6000")]
public sealed unsafe class HttpServiceUrlAcls : IHttpUrlAcls
{
    private static readonly HTTPAPI_VERSION s_version = new() { HttpApiMajorVersion = 1, HttpApiMinorVersion = 0 };

    public string? Find(string url)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        return Within(() =>
        {
            fixed (char* prefix = url)
            {
                var query = new HTTP_SERVICE_CONFIG_URLACL_QUERY
                {
                    QueryDesc = HTTP_SERVICE_CONFIG_QUERY_TYPE.HttpServiceConfigQueryExact,
                    KeyDesc = new HTTP_SERVICE_CONFIG_URLACL_KEY { pUrlPrefix = prefix },
                };
                uint size = 0;
                var result = PInvoke.HttpQueryServiceConfiguration(HANDLE.Null, HTTP_SERVICE_CONFIG_ID.HttpServiceConfigUrlAclInfo, &query,
                    (uint)sizeof(HTTP_SERVICE_CONFIG_URLACL_QUERY), null, 0, &size, null);
                if (result == (uint)WIN32_ERROR.ERROR_FILE_NOT_FOUND)
                {
                    return null;
                }

                if (result != (uint)WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER)
                {
                    throw new Win32Exception((int)result);
                }

                var buffer = new byte[size];
                fixed (byte* output = buffer)
                {
                    result = PInvoke.HttpQueryServiceConfiguration(HANDLE.Null, HTTP_SERVICE_CONFIG_ID.HttpServiceConfigUrlAclInfo, &query,
                        (uint)sizeof(HTTP_SERVICE_CONFIG_URLACL_QUERY), output, size, &size, null);
                    if (result == (uint)WIN32_ERROR.ERROR_FILE_NOT_FOUND)
                    {
                        return null;
                    }

                    if (result != 0)
                    {
                        throw new Win32Exception((int)result);
                    }

                    var found = (HTTP_SERVICE_CONFIG_URLACL_SET*)output;
                    return found->ParamDesc.pStringSecurityDescriptor.ToString();
                }
            }
        });
    }

    public void Add(string url, string securityDescriptor)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        ArgumentException.ThrowIfNullOrEmpty(securityDescriptor);
        Within(() =>
        {
            fixed (char* prefix = url)
            fixed (char* descriptor = securityDescriptor)
            {
                var set = new HTTP_SERVICE_CONFIG_URLACL_SET
                {
                    KeyDesc = new HTTP_SERVICE_CONFIG_URLACL_KEY { pUrlPrefix = prefix },
                    ParamDesc = new HTTP_SERVICE_CONFIG_URLACL_PARAM { pStringSecurityDescriptor = descriptor },
                };
                var result = PInvoke.HttpSetServiceConfiguration(HANDLE.Null, HTTP_SERVICE_CONFIG_ID.HttpServiceConfigUrlAclInfo, &set,
                    (uint)sizeof(HTTP_SERVICE_CONFIG_URLACL_SET), null);
                if (result != 0)
                {
                    throw new Win32Exception((int)result);
                }
            }

            return 0;
        });
    }

    public void Delete(string url)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        Within(() =>
        {
            fixed (char* prefix = url)
            {
                var set = new HTTP_SERVICE_CONFIG_URLACL_SET { KeyDesc = new HTTP_SERVICE_CONFIG_URLACL_KEY { pUrlPrefix = prefix } };
                var result = PInvoke.HttpDeleteServiceConfiguration(HANDLE.Null, HTTP_SERVICE_CONFIG_ID.HttpServiceConfigUrlAclInfo, &set,
                    (uint)sizeof(HTTP_SERVICE_CONFIG_URLACL_SET), null);
                if (result is not 0 and not (uint)WIN32_ERROR.ERROR_FILE_NOT_FOUND)
                {
                    throw new Win32Exception((int)result);
                }
            }

            return 0;
        });
    }

    /// <summary>The security identifier a server's game runs as: its account's, or the agent's own in development.</summary>
    public static string SidOf(ProcessIdentity identity) => identity switch
    {
        ProcessIdentity.AccountIdentity account => ((SecurityIdentifier)new NTAccount(account.Name).Translate(typeof(SecurityIdentifier))).Value,
        _ => WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("The agent's own account has no security identifier."),
    };

    // The configuration calls need the API initialized for configuration, and released afterwards.
    private static T Within<T>(Func<T> work)
    {
        var result = PInvoke.HttpInitialize(s_version, HTTP_INITIALIZE.HTTP_INITIALIZE_CONFIG);
        if (result != 0)
        {
            throw new Win32Exception((int)result);
        }

        try
        {
            return work();
        }
        finally
        {
            _ = PInvoke.HttpTerminate(HTTP_INITIALIZE.HTTP_INITIALIZE_CONFIG);
        }
    }
}
