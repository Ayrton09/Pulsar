using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SftpSpike;

/// <summary>A path that leaves the jail or is not a plain Windows file name.</summary>
internal sealed class JailException(string message) : Exception(message);

/// <summary>Minimal SFTP v3 server over a stream (SSH_FXP_*), jailed in one folder. Spike code.</summary>
internal sealed partial class SftpSession(Stream stream, string root, Action<string> log)
{
    private const int MaxPacket = 256 * 1024 + 4096;
    private const int MaxHandles = 32;

    private const byte Init = 1, Version = 2, Open = 3, Close = 4, Read = 5, Write = 6, LStat = 7, FStat = 8, SetStat = 9,
        FSetStat = 10, OpenDir = 11, ReadDir = 12, Remove = 13, MkDir = 14, RmDir = 15, RealPath = 16, Stat = 17, Rename = 18,
        Status = 101, Handle = 102, Data = 103, Name = 104, Attrs = 105;

    private const uint Ok = 0, Eof = 1, NoSuchFile = 2, PermissionDenied = 3, Failure = 4, BadMessage = 5, OpUnsupported = 8;

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\..*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceName();

    private readonly string _root = Path.GetFullPath(root).TrimEnd('\\');
    private readonly Dictionary<string, object> _handles = [];
    private int _next;

    private sealed class DirState(List<string> entries) { public List<string> Entries { get; } = entries; public int Pos; }

    public async Task RunAsync(CancellationToken ct)
    {
        try { await LoopAsync(ct); }
        finally { Dispose(); }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var lenBuf = new byte[4];
        while (true)
        {
            if (!await FillAsync(lenBuf, ct)) return;
            var len = BinaryPrimitives.ReadUInt32BigEndian(lenBuf);
            if (len is < 1 or > MaxPacket) { log($"bad packet length {len}, closing"); return; }
            var body = new byte[len];
            if (!await FillAsync(body, ct)) return;
            var reply = Dispatch(body);
            if (reply is not null) await stream.WriteAsync(reply, ct);
        }
    }

    private async Task<bool> FillAsync(byte[] buf, CancellationToken ct)
    {
        var n = 0;
        while (n < buf.Length)
        {
            var r = await stream.ReadAsync(buf.AsMemory(n), ct);
            if (r == 0) return false;
            n += r;
        }
        return true;
    }

    private byte[]? Dispatch(byte[] p)
    {
        var rd = new Rd(p);
        var type = rd.U8();
        if (type == Init) return new Wr(Version).U32(3).Done();
        var id = rd.U32();
        try
        {
            switch (type)
            {
                case RealPath: return NameReply(id, [(Virt(rd.Str()), null)]);
                case Stat or LStat: return AttrsReply(id, Phys(rd.Str()));
                case FStat: return _handles.TryGetValue(rd.Str(), out var f) && f is FileStream fs ? AttrsReply(id, fs.Name) : St(id, Failure, "bad handle");
                case OpenDir: return OpenDirectory(id, rd.Str());
                case ReadDir: return ReadDirectory(id, rd.Str());
                case Close: return CloseHandle(id, rd.Str());
                case Open: return OpenFile(id, rd);
                case Read: return ReadFile(id, rd);
                case Write: return WriteFile(id, rd);
                case Remove: File.Delete(Phys(rd.Str())); return St(id, Ok);
                case MkDir: Directory.CreateDirectory(Phys(rd.Str())); return St(id, Ok);
                case RmDir: Directory.Delete(Phys(rd.Str()), false); return St(id, Ok);
                case Rename: return RenameEntry(id, rd.Str(), rd.Str());
                case SetStat or FSetStat: return St(id, Ok); // permissions/times accepted and ignored
                default: return St(id, OpUnsupported, $"type {type} not supported");
            }
        }
        catch (JailException e) { log($"JAIL refused: {e.Message}"); return St(id, PermissionDenied, "path not allowed"); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return St(id, NoSuchFile, "no such file"); }
        catch (UnauthorizedAccessException) { return St(id, PermissionDenied, "permission denied"); }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException) { return St(id, Failure, e.Message); }
    }

    // ---- paths ----

    /// <summary>Normalises a client path to a virtual absolute path; refuses ".." above the root and non-file-name characters.</summary>
    private static string Virt(string path)
    {
        var parts = new List<string>();
        foreach (var seg in path.Split('/'))
        {
            if (seg is "" or ".") continue;
            if (seg == "..") { if (parts.Count == 0) throw new JailException("'..' above root: " + path); parts.RemoveAt(parts.Count - 1); continue; }
            if (seg.IndexOfAny(['\\', ':', '*', '?', '"', '<', '>', '|', '\0']) >= 0 || seg.Any(char.IsControl)) throw new JailException("bad character: " + path);
            if (seg != seg.TrimEnd('.', ' ') || DeviceName().IsMatch(seg)) throw new JailException("Windows-special name: " + path);
            parts.Add(seg);
        }
        return "/" + string.Join('/', parts);
    }

    private string Phys(string path)
    {
        var v = Virt(path);
        var full = Path.GetFullPath(Path.Combine(_root, v.TrimStart('/').Replace('/', '\\')));
        if (full != _root && !full.StartsWith(_root + "\\", StringComparison.OrdinalIgnoreCase)) throw new JailException("escapes: " + path);
        return full;
    }

    // ---- operations ----

    private byte[] OpenDirectory(uint id, string path)
    {
        if (_handles.Count >= MaxHandles) return St(id, Failure, "too many open handles");
        var dir = Phys(path);
        if (!Directory.Exists(dir)) return St(id, NoSuchFile, "no such directory");
        return HandleReply(id, new DirState([.. Directory.EnumerateFileSystemEntries(dir)]));
    }

    private byte[] ReadDirectory(uint id, string h)
    {
        if (!_handles.TryGetValue(h, out var o) || o is not DirState d) return St(id, Failure, "bad handle");
        if (d.Pos >= d.Entries.Count) return St(id, Eof);
        var batch = d.Entries.Skip(d.Pos).Take(64).ToList();
        d.Pos += batch.Count;
        return NameReply(id, batch.Select(e => (Path.GetFileName(e), (string?)e)));
    }

    private byte[] OpenFile(uint id, Rd rd)
    {
        var path = Phys(rd.Str());
        var flags = rd.U32();
        rd.SkipAttrs();
        if (_handles.Count >= MaxHandles) return St(id, Failure, "too many open handles");
        bool w = (flags & 2) != 0, creat = (flags & 8) != 0, trunc = (flags & 0x10) != 0, excl = (flags & 0x20) != 0;
        var mode = creat ? (excl ? FileMode.CreateNew : trunc ? FileMode.Create : FileMode.OpenOrCreate) : trunc ? FileMode.Truncate : FileMode.Open;
        var access = w ? ((flags & 1) != 0 ? FileAccess.ReadWrite : FileAccess.Write) : FileAccess.Read;
        if (Directory.Exists(path)) return St(id, Failure, "is a directory");
        return HandleReply(id, new FileStream(path, mode, access, w ? FileShare.None : FileShare.Read, 1, FileOptions.Asynchronous));
    }

    private byte[] ReadFile(uint id, Rd rd)
    {
        if (!_handles.TryGetValue(rd.Str(), out var o) || o is not FileStream fs) return St(id, Failure, "bad handle");
        var off = rd.U64();
        var len = (int)Math.Min(rd.U32(), 256 * 1024);
        var buf = new byte[len];
        fs.Position = (long)off;
        var n = fs.Read(buf, 0, len);
        return n == 0 ? St(id, Eof) : new Wr(Data).U32(id).Bytes(buf.AsSpan(0, n)).Done();
    }

    private byte[] WriteFile(uint id, Rd rd)
    {
        if (!_handles.TryGetValue(rd.Str(), out var o) || o is not FileStream fs) return St(id, Failure, "bad handle");
        var off = rd.U64();
        var data = rd.BytesSpan();
        fs.Position = (long)off;
        fs.Write(data);
        return St(id, Ok);
    }

    private byte[] CloseHandle(uint id, string h)
    {
        if (!_handles.Remove(h, out var o)) return St(id, Failure, "bad handle");
        (o as IDisposable)?.Dispose();
        return St(id, Ok);
    }

    private byte[] RenameEntry(uint id, string from, string to)
    {
        string a = Phys(from), b = Phys(to);
        if (File.Exists(b) || Directory.Exists(b)) return St(id, Failure, "target exists");
        if (Directory.Exists(a)) Directory.Move(a, b); else File.Move(a, b);
        return St(id, Ok);
    }

    public void Dispose()
    {
        foreach (var o in _handles.Values) (o as IDisposable)?.Dispose();
        _handles.Clear();
    }

    // ---- replies ----

    private byte[] HandleReply(uint id, object state)
    {
        var h = (++_next).ToString("x8", CultureInfo.InvariantCulture);
        _handles[h] = state;
        return new Wr(Handle).U32(id).Str(h).Done();
    }

    private static byte[] St(uint id, uint code, string msg = "") => new Wr(Status).U32(id).U32(code).Str(msg).Str("en").Done();

    private byte[] AttrsReply(uint id, string phys)
    {
        var w = new Wr(Attrs).U32(id);
        return WriteAttrs(w, phys) ? w.Done() : St(id, NoSuchFile, "no such file");
    }

    private static bool WriteAttrs(Wr w, string phys)
    {
        FileSystemInfo fi = Directory.Exists(phys) ? new DirectoryInfo(phys) : new FileInfo(phys);
        if (!fi.Exists) return false;
        var dir = fi is DirectoryInfo;
        var mtime = (uint)new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();
        w.U32(1 | 2 | 4 | 8).U64(dir ? 0UL : (ulong)((FileInfo)fi).Length).U32(1000).U32(1000).U32(dir ? 0x41EDu : 0x81A4u).U32(mtime).U32(mtime);
        return true;
    }

    /// <summary>SSH_FXP_NAME. Each entry: file name and, for listings, the physical path to build the ls -l style long name from.</summary>
    private byte[] NameReply(uint id, IEnumerable<(string name, string? phys)> entries)
    {
        var list = entries.ToList();
        var w = new Wr(Name).U32(id).U32((uint)list.Count);
        foreach (var (name, phys) in list)
        {
            w.Str(name).Str(phys is null ? name : LongName(name, phys));
            if (phys is null || !WriteAttrs(w, phys)) w.U32(0);
        }
        return w.Done();
    }

    private static string LongName(string name, string phys)
    {
        FileSystemInfo fi = Directory.Exists(phys) ? new DirectoryInfo(phys) : new FileInfo(phys);
        var dir = fi is DirectoryInfo;
        var t = fi.LastWriteTime;
        var when = t > DateTime.Now.AddMonths(-6) ? t.ToString("MMM dd HH:mm", CultureInfo.InvariantCulture) : t.ToString("MMM dd  yyyy", CultureInfo.InvariantCulture);
        return string.Create(CultureInfo.InvariantCulture, $"{(dir ? "drwxr-xr-x" : "-rw-r--r--")}    1 pulsar   pulsar   {(dir ? 0 : ((FileInfo)fi).Length),10} {when} {name}");
    }

    // ---- wire helpers ----

    private sealed class Rd(byte[] b)
    {
        private int _p;
        public byte U8() => b[_p++];
        public uint U32() { var v = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(_p)); _p += 4; return v; }
        public ulong U64() { var v = BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(_p)); _p += 8; return v; }
        public ReadOnlySpan<byte> BytesSpan() { var n = (int)U32(); if (n < 0 || n > b.Length - _p) throw new ArgumentException("bad length"); var s = b.AsSpan(_p, n); _p += n; return s; }
        public string Str() => Encoding.UTF8.GetString(BytesSpan());
        public void SkipAttrs()
        {
            var f = U32();
            if ((f & 1) != 0) _p += 8;
            if ((f & 2) != 0) _p += 8;
            if ((f & 4) != 0) _p += 4;
            if ((f & 8) != 0) _p += 8;
            if ((f & 0x80000000) != 0) { var c = U32(); for (var i = 0; i < c; i++) { BytesSpan(); BytesSpan(); } }
        }
    }

    private sealed class Wr
    {
        private readonly MemoryStream _m = new();
        public Wr(byte type) { _m.Write(new byte[4]); _m.WriteByte(type); }
        public Wr U32(uint v) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(s, v); _m.Write(s); return this; }
        public Wr U64(ulong v) { Span<byte> s = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(s, v); _m.Write(s); return this; }
        public Wr Bytes(ReadOnlySpan<byte> v) { U32((uint)v.Length); _m.Write(v); return this; }
        public Wr Str(string v) => Bytes(Encoding.UTF8.GetBytes(v));
        public byte[] Done() { var a = _m.ToArray(); BinaryPrimitives.WriteUInt32BigEndian(a, (uint)(a.Length - 4)); return a; }
    }
}
