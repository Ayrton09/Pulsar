using System.Diagnostics;
using System.Security.Cryptography;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace SftpSpike;

/// <summary>SSH.NET client checks against the spike server (what Phase 17's automated tests would use).</summary>
internal static class SshNetTests
{
    private static int _fail;

    private static void Check(string what, bool ok, string extra = "")
    {
        if (!ok) _fail++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what} {extra}");
    }

    private static T? Try<T>(string what, Func<T> f, bool expectFail = false)
    {
        try { var t = Task.Run(f); if (!t.Wait(20000)) { Check(what, expectFail, "[HUNG: no answer in 20 s]"); return default; } var r = t.Result; if (expectFail) Check(what, false, "(should have failed)"); return r; }
        catch (Exception e) { Check(what, expectFail, $"[{e.GetType().Name}: {e.Message}]"); return default; }
    }

    private static ConnectionInfo Info(int port, string user, params AuthenticationMethod[] m) => new("127.0.0.1", port, user, m) { Timeout = TimeSpan.FromSeconds(15) };

    private static KeyboardInteractiveAuthenticationMethod Ki(string user, string pw, string code, List<string> prompts)
    {
        var ki = new KeyboardInteractiveAuthenticationMethod(user);
        ki.AuthenticationPrompt += (_, e) =>
        {
            foreach (var p in e.Prompts) { prompts.Add(p.Request); p.Response = p.Request.Contains("assword") ? pw : code; }
        };
        return ki;
    }

    private static SftpClient Client(ConnectionInfo ci)
    {
        var c = new SftpClient(ci);
        c.HostKeyReceived += (_, e) => { Console.WriteLine($"  hostkey {e.HostKeyName} SHA256:{e.FingerPrintSHA256}"); e.CanTrust = true; };
        return c;
    }

    public static int Run(int port)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "sftpspike-client");
        Directory.CreateDirectory(tmp);

        // alice: password
        using (var c = Client(Info(port, "alice", new PasswordAuthenticationMethod("alice", "pw-alice"))))
        {
            if (Try("alice password connect", () => { c.Connect(); return true; }))
            {
                var ci = c.ConnectionInfo;
                Console.WriteLine($"  negotiated kex={ci.CurrentKeyExchangeAlgorithm} hostkey={ci.CurrentHostKeyAlgorithm} enc={ci.CurrentServerEncryption} mac={ci.CurrentServerHmacAlgorithm}");
                Console.WriteLine("  pwd=" + c.WorkingDirectory);
                var f = Path.Combine(tmp, "up.bin"); var g = Path.Combine(tmp, "down.bin");
                File.WriteAllBytes(f, RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 17));
                using (var s = File.OpenRead(f)) c.UploadFile(s, "/netup.bin");
                using (var s = File.Create(g)) c.DownloadFile("/netup.bin", s);
                Check("upload+download 3 MB identical", SHA256.HashData(File.ReadAllBytes(f)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(g))));
                Console.WriteLine("  ls /: " + string.Join(", ", c.ListDirectory("/").Select(e => e.Name + (e.IsDirectory ? "/" : "") + ":" + e.Length)));
                c.CreateDirectory("/netdir"); c.RenameFile("/netup.bin", "/netdir/renamed.bin");
                Check("rename into folder", c.Exists("/netdir/renamed.bin"));
                c.DeleteFile("/netdir/renamed.bin"); c.DeleteDirectory("/netdir");
                Check("delete file + rmdir", !c.Exists("/netdir"));
                Try("path traversal /../../x refused", () => c.ReadAllText("/../../x"), expectFail: true);
                Try("path with backslash refused", () => c.ReadAllText("/..\\..\\x"), expectFail: true);
                Try("path with ADS colon refused", () => c.ReadAllText("/a:b"), expectFail: true);

                // 50 MB throughput
                var big = Path.Combine(tmp, "big.bin");
                using (var fs = File.Create(big)) { var b = RandomNumberGenerator.GetBytes(1 << 20); for (var i = 0; i < 50; i++) fs.Write(b); }
                var sw = Stopwatch.StartNew();
                using (var s = File.OpenRead(big)) c.UploadFile(s, "/big.bin");
                var up = sw.Elapsed; sw.Restart();
                using (var s = File.Create(big + ".dl")) c.DownloadFile("/big.bin", s);
                Console.WriteLine($"  SSH.NET 50 MB: put {50 / up.TotalSeconds:F0} MB/s, get {50 / sw.Elapsed.TotalSeconds:F0} MB/s");
                c.DeleteFile("/big.bin"); File.Delete(big); File.Delete(big + ".dl");
            }
        }

        Try("alice wrong password refused", () => { using var c = Client(Info(port, "alice", new PasswordAuthenticationMethod("alice", "nope"))); c.Connect(); return 1; }, expectFail: true);

        // alice: keyboard-interactive (what OpenSSH prefers when the server offers it)
        var p1 = new List<string>();
        using (var c = Client(Info(port, "alice", Ki("alice", "pw-alice", "", p1))))
            Check("alice keyboard-interactive connect", Try("alice ki", () => { c.Connect(); return c.IsConnected; }), "prompts=" + string.Join("|", p1));

        // bob needs the code: password method alone must fail
        Try("bob password-only refused (2FA)", () => { using var c = Client(Info(port, "bob", new PasswordAuthenticationMethod("bob", "pw-bob"))); c.Connect(); return 1; }, expectFail: true);
        var p2 = new List<string>();
        using (var c = Client(Info(port, "bob", Ki("bob", "pw-bob", "123456", p2))))
            Check("bob keyboard-interactive + code connect", Try("bob ki", () => { c.Connect(); return c.IsConnected; }), "prompts=" + string.Join("|", p2));
        var p3 = new List<string>();
        Try("bob wrong code refused", () => { using var c = Client(Info(port, "bob", Ki("bob", "pw-bob", "000000", p3))); c.Connect(); return 1; }, expectFail: true);
        Console.WriteLine("  wrong-code prompts: " + string.Join("|", p3));

        // bob.srv1: one server as the root
        using (var c = Client(Info(port, "bob.srv1", Ki("bob.srv1", "pw-bob", "123456", []))))
        {
            if (Try("bob.srv1 connect", () => { c.Connect(); return true; }))
            {
                c.WriteAllText("/only-in-srv1.txt", "x");
                Check("bob.srv1 root is the server folder", c.ListDirectory("/").Any(e => e.Name == "only-in-srv1.txt"));
                c.DeleteFile("/only-in-srv1.txt");
            }
        }
        Try("bob.nope (unknown server) refused", () => { using var c = Client(Info(port, "bob.nope", Ki("bob.nope", "pw-bob", "123456", []))); c.Connect(); c.ListDirectory("/"); return 1; }, expectFail: true);

        // no shell / exec with SSH.NET
        using (var s = new SshClient(Info(port, "alice", new PasswordAuthenticationMethod("alice", "pw-alice"))))
        {
            s.HostKeyReceived += (_, e) => e.CanTrust = true;
            if (Try("ssh client connect", () => { s.Connect(); return true; }))
            {
                Try("exec 'id' refused", () => { var cmd = s.CreateCommand("id"); var o = cmd.Execute(); return o + "|exit=" + cmd.ExitStatus; }, expectFail: true);
                Try("shell stream refused", () => { using var sh = s.CreateShellStream("x", 80, 24, 0, 0, 1024); Thread.Sleep(500); return sh.CanRead; }, expectFail: true);
                var lp = new ForwardedPortLocal("127.0.0.1", 0, "127.0.0.1", 9);
                s.AddForwardedPort(lp);
                lp.Exception += (_, e) => Console.WriteLine("  forward exception (expected): " + e.Exception.Message);
                lp.Start();
                using var tc = new System.Net.Sockets.TcpClient();
                tc.Connect("127.0.0.1", (int)lp.BoundPort);
                tc.GetStream().Write("hello"u8);
                Thread.Sleep(1000);
                Console.WriteLine("  local forward: after connecting + writing, tcp still connected=" + tc.Connected + " (see server log: direct-tcpip must be REFUSED)");
                lp.Stop();
            }
        }

        Console.WriteLine(_fail == 0 ? "ALL SSH.NET CHECKS PASSED" : $"{_fail} SSH.NET CHECKS FAILED");
        return _fail == 0 ? 0 : 1;
    }
}
