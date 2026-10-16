using System.Security.Claims;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Messages;
using Microsoft.DevTunnels.Ssh.Tcp;

namespace SftpSpike;

/// <summary>A DevTunnels.Ssh client that sends the requests OpenSSH will not: every one except subsystem sftp must be refused.</summary>
internal static class Probe
{
    private static async Task<string> Try(string what, Func<Task<bool>> f)
    {
        string r;
        try { r = await f().WaitAsync(TimeSpan.FromSeconds(10)) ? "ACCEPTED" : "refused"; }
        catch (Exception e) { r = "refused/closed (" + e.GetType().Name + ": " + e.Message.Split('\n')[0] + ")"; }
        Console.WriteLine($"  {what,-34} -> {r}");
        return r;
    }

    public static async Task<int> RunAsync(int port)
    {
        using var client = new SshClient(new SshSessionConfiguration(), new System.Diagnostics.TraceSource("probe"));
        using var client2 = new SshClient(new SshSessionConfiguration(), new System.Diagnostics.TraceSource("probe2"));
        var s2 = await client2.OpenSessionAsync("127.0.0.1", port);
        s2.Authenticating += (_, e) => { if (e.AuthenticationType == SshAuthenticationType.ServerPublicKey) e.AuthenticationTask = Task.FromResult(new ClaimsPrincipal()); };
        var key = new Microsoft.DevTunnels.Ssh.Algorithms.ECDsa.KeyPair(System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256));
        Console.WriteLine("publickey auth accepted? " + await s2.AuthenticateAsync(new SshClientCredentials("alice", key)));
        var s = await client.OpenSessionAsync("127.0.0.1", port);
        s.Authenticating += (_, e) => { if (e.AuthenticationType == SshAuthenticationType.ServerPublicKey) e.AuthenticationTask = Task.FromResult(new ClaimsPrincipal()); };
        Console.WriteLine("auth with password: " + await s.AuthenticateAsync(new SshClientCredentials("alice", "pw-alice")));
        var bad = 0;
        async Task Expect(string what, Func<Task<bool>> f) { if ((await Try(what, f)).StartsWith("ACCEPTED")) bad++; }

        foreach (var t in new[] { "x11-req", "auth-agent-req@openssh.com", "env", "pty-req", "shell", "exec", "window-change", "break", "xon-xoff", "subsystem-other" })
        {
            var ch = await s.OpenChannelAsync();
            await Expect("session channel request " + t, () => ch.RequestAsync(new ChannelRequestMessage { RequestType = t, WantReply = true }));
            await ch.CloseAsync();
        }
        await Expect("subsystem 'shell' (not sftp)", async () => { var ch = await s.OpenChannelAsync(); return await ch.RequestAsync(new SubsystemRequestMessage { Subsystem = "shell", WantReply = true }); });
        foreach (var t in new[] { "tcpip-forward", "cancel-tcpip-forward", "streamlocal-forward@openssh.com", "no-more-sessions@openssh.com", "keepalive@openssh.com" })
            await Expect("global request " + t, () => s.RequestAsync(new SessionRequestMessage { RequestType = t, WantReply = true }));
        foreach (var t in new[] { "direct-tcpip", "x11", "forwarded-tcpip", "auth-agent@openssh.com", "direct-streamlocal@openssh.com", "other" })
        {
            if (s.IsClosed) { Console.WriteLine("  (session already closed by the server)"); break; }
            await Expect("channel open " + t, async () => { var ch = await s.OpenChannelAsync(t); return ch != null; });
        }
        Console.WriteLine(bad == 0 ? "PROBE: nothing but sftp was accepted" : $"PROBE: {bad} unexpected acceptances");
        return bad;
    }

}
