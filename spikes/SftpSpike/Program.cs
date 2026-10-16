using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Keys;
using Microsoft.DevTunnels.Ssh.Messages;
using Microsoft.DevTunnels.Ssh.Tcp;
using SftpSpike;

// SFTP spike (Phase 17). Throwaway. server: DevTunnels.Ssh server on 127.0.0.1 + our SFTP v3. sshnet: SSH.NET client tests.
//   dotnet run -- server --port 2222 --data <dir> [--ki two|single]
//   dotnet run -- sshnet --port 2222
var mode = args.Length > 0 ? args[0] : "server";
string Opt(string name, string def) { var i = Array.IndexOf(args, "--" + name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
var port = int.Parse(Opt("port", "2222"));
var data = Opt("data", Path.Combine(Path.GetTempPath(), "sftpspike"));
if (mode == "probe") return await Probe.RunAsync(port);
return mode == "sshnet" ? SshNetTests.Run(port) : await Server.RunAsync(port, data, Opt("ki", "two"));

namespace SftpSpike
{
    /// <summary>The "subsystem" channel request (RFC 4254 6.5): the library has no message type for it.</summary>
    internal sealed class SubsystemRequestMessage : ChannelRequestMessage
    {
        public SubsystemRequestMessage() { RequestType = "subsystem"; }
        public string? Subsystem { get; set; }
        protected override void OnRead(ref Microsoft.DevTunnels.Ssh.IO.SshDataReader reader) { base.OnRead(ref reader); Subsystem = reader.ReadString(Encoding.UTF8); }
        protected override void OnWrite(ref Microsoft.DevTunnels.Ssh.IO.SshDataWriter writer) { base.OnWrite(ref writer); writer.Write(Subsystem ?? "", Encoding.UTF8); }
    }

    internal static class Server
    {
        private const string Code = "123456";
        // user -> (password, needs a TOTP-like code)
        private static readonly Dictionary<string, (string Pw, bool Totp)> Users = new() { ["alice"] = ("pw-alice", false), ["bob"] = ("pw-bob", true) };
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, KiState> Ki = [];
        private sealed class KiState { public bool PasswordOk; }

        private static void Log(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {s}");

        public static async Task<int> RunAsync(int port, string data, string kiMode)
        {
            Directory.CreateDirectory(data);
            var root = Path.Combine(data, "jail");
            Directory.CreateDirectory(Path.Combine(root, "srv1"));

            // 1. persisted ECDSA P-256 host key
            var keyFile = Path.Combine(data, "hostkey.pem");
            IKeyPair hostKey;
            if (File.Exists(keyFile)) hostKey = Microsoft.DevTunnels.Ssh.KeyPair.ImportKeyFile(keyFile, null);
            else
            {
                hostKey = new Microsoft.DevTunnels.Ssh.Algorithms.ECDsa.KeyPair(System.Security.Cryptography.ECDsa.Create(ECCurve.NamedCurves.nistP256));
                Microsoft.DevTunnels.Ssh.KeyPair.ExportPrivateKeyFile(hostKey, keyFile, null, KeyFormat.Pkcs8);
            }
            var blob = hostKey.GetPublicKeyBytes(hostKey.KeyAlgorithmName).ToArray();
            Log($"host key {hostKey.KeyAlgorithmName} SHA256:{Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=')}");

            var cfg = new SshSessionConfiguration();
            cfg.AuthenticationMethods.Clear();
            cfg.AuthenticationMethods.Add(AuthenticationMethods.Password);
            cfg.AuthenticationMethods.Add(AuthenticationMethods.KeyboardInteractive);
            Log("defaults kex=" + string.Join(',', cfg.KeyExchangeAlgorithms.Select(a => a?.Name)) + " hostkey=" + string.Join(',', cfg.PublicKeyAlgorithms.Select(a => a?.Name))
                + " enc=" + string.Join(',', cfg.EncryptionAlgorithms.Select(a => a?.Name)) + " mac=" + string.Join(',', cfg.HmacAlgorithms.Select(a => a?.Name)) + " maxAuthAttempts=" + cfg.MaxClientAuthenticationAttempts);

            // optional filters to test restricted algorithm sets: SPIKE_ENC, SPIKE_KEX (comma lists of algorithm names)
            Filter("SPIKE_ENC", cfg.EncryptionAlgorithms);
            Filter("SPIKE_KEX", cfg.KeyExchangeAlgorithms);
            var trace = new TraceSource("ssh", SourceLevels.Critical);
            trace.Listeners.Add(new TextWriterTraceListener(Console.Out));
            using var server = new SshServer(cfg, trace) { Credentials = new SshServerCredentials(hostKey) };
            server.ExceptionRaised += (_, e) => Log("EXC " + e.GetType().Name + ": " + e.Message);
            server.SessionOpened += (_, s) => { Log("session opened"); s.Closed += (_, c) => Log($"session closed: {c.Reason} {c.Exception?.Message}"); };

            // 2. authentication
            server.SessionAuthenticating += (sender, e) => OnAuth(sender, e, kiMode);

            // 3. channels: only "session"; only the "subsystem sftp" request
            server.ChannelOpening += (_, e) =>
            {
                var t = e.Request.ChannelType;
                if (t == SshChannel.SessionChannelType) { Log("channel open: session -> accepted"); return; }
                Log($"channel open: {t} -> REFUSED");
                e.FailureReason = SshChannelOpenFailureReason.AdministrativelyProhibited;
                e.FailureDescription = "only sftp";
            };
            server.ChannelRequest += (sender, e) => OnChannelRequest(sender, e, root);
            server.SessionRequest += (_, e) => { Log($"session (global) request {e.RequestType} -> REFUSED"); e.IsAuthorized = false; };

            Log($"listening 127.0.0.1:{port} root={root}");
            var accept = server.AcceptSessionsAsync(port, IPAddress.Loopback);
            var stop = Path.Combine(data, "stop");
            var until = DateTime.UtcNow.AddMinutes(20);
            while (!File.Exists(stop) && DateTime.UtcNow < until && !accept.IsCompleted) await Task.Delay(300);
            Log("stopping");
            return 0;
        }

        private static void Filter<T>(string env, ICollection<T?> list) where T : SshAlgorithm
        {
            var keep = Environment.GetEnvironmentVariable(env)?.Split(',');
            if (keep is null) return;
            foreach (var a in list.ToList()) if (!keep.Contains(a?.Name)) list.Remove(a);
            Log(env + " -> " + string.Join(',', list.Select(a => a?.Name)));
        }

        private static ClaimsPrincipal Principal(string user) => new(new ClaimsIdentity([new Claim(ClaimTypes.Name, user)], "pulsar"));

        private static void OnAuth(object? sender, SshAuthenticatingEventArgs e, string kiMode)
        {
            var user = e.Username ?? "";
            var name = user.Contains('.') ? user[..user.LastIndexOf('.')] : user;
            Users.TryGetValue(name, out var u);
            Log($"auth {e.AuthenticationType} user='{user}' infoReq={(e.InfoRequest != null)} infoResp={(e.InfoResponse != null)}");
            switch (e.AuthenticationType)
            {
                case SshAuthenticationType.ClientPassword:
                    // a user who needs a second factor can never get in with the password method alone
                    if (u.Pw != null && !u.Totp && e.Password == u.Pw) e.AuthenticationTask = Task.FromResult(Principal(user));
                    Log($"  password -> {(e.AuthenticationTask != null ? "OK" : "refused")}");
                    break;
                case SshAuthenticationType.ClientInteractive:
                    OnInteractive(sender!, e, user, u, kiMode);
                    break;
                default:
                    Log("  refused (publickey/none/other)");
                    break;
            }
        }

        private static void OnInteractive(object session, SshAuthenticatingEventArgs e, string user, (string Pw, bool Totp) u, string kiMode)
        {
            var st = Ki.GetOrCreateValue(session);
            if (e.InfoResponse is null) // first round: ask
            {
                st.PasswordOk = false;
                var req = new AuthenticationInfoRequestMessage("");
                req.AddPrompt("Password: ", false);
                if (kiMode == "single" && u.Totp) req.AddPrompt("Verification code: ", false);
                e.InfoRequest = req;
                return;
            }
            var r = e.InfoResponse.Responses;
            Log($"  ki responses={r.Count}");
            if (u.Pw == null) { return; } // unknown user: refuse (a real server would still run the same checks to avoid user enumeration)
            if (!st.PasswordOk)
            {
                if (r.Count < 1 || r[0] != u.Pw) { Log("  ki: bad password"); return; }
                if (!u.Totp) { e.AuthenticationTask = Task.FromResult(Principal(user)); Log("  ki: OK (no 2FA)"); return; }
                if (kiMode == "single") { Finish(e, user, r.Count > 1 ? r[1] : ""); return; }
                st.PasswordOk = true; // second round: ask for the code only after the password was right
                var req = new AuthenticationInfoRequestMessage("");
                req.AddPrompt("Verification code: ", false);
                e.InfoRequest = req;
                return;
            }
            st.PasswordOk = false;
            Finish(e, user, r.Count > 0 ? r[0] : "");
        }

        private static void Finish(SshAuthenticatingEventArgs e, string user, string code)
        {
            if (code == Code) { e.AuthenticationTask = Task.FromResult(Principal(user)); Log("  ki: OK (2FA)"); } else Log("  ki: bad code");
        }

        private static void OnChannelRequest(object? sender, SshRequestEventArgs<ChannelRequestMessage> e, string root)
        {
            var who = e.Principal?.Identity?.Name;
            if (e.RequestType == "subsystem" && sender is SshChannel ch && who != null)
            {
                var name = e.Request.ConvertTo<SubsystemRequestMessage>().Subsystem;
                if (name == "sftp")
                {
                    var sub = who.Contains('.') ? who[(who.LastIndexOf('.') + 1)..] : null; // name.<shortId> -> that server's folder as the root
                    var dir = sub is null ? root : Path.Combine(root, sub);
                    if (!Directory.Exists(dir)) { Log($"channel request subsystem sftp for '{who}': no such server '{sub}' -> REFUSED"); e.IsAuthorized = false; return; }
                    Log($"channel request subsystem sftp user='{who}' root={dir} -> accepted");
                    var stream = new SshStream(ch);
                    var sftp = new SftpSession(stream, dir, s => Log("  sftp " + s));
                    e.IsAuthorized = true;
                    _ = Task.Run(async () =>
                    {
                        try { await sftp.RunAsync(CancellationToken.None); }
                        catch (Exception ex) { Log("  sftp ended: " + ex.GetType().Name + " " + ex.Message); }
                        finally { await ch.CloseAsync(CancellationToken.None); }
                    });
                    return;
                }
                Log($"channel request subsystem '{name}' -> REFUSED");
            }
            else Log($"channel request {e.RequestType} (sender={sender?.GetType().Name}, user={who}) -> REFUSED");
            e.IsAuthorized = false;
        }
    }
}
