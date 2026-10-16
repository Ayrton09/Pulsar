using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Pulsar.Api.Sftp;

/// <summary>SFTP version 3 (draft-ietf-secsh-filexfer-02): packet types, status codes and attribute flags.</summary>
internal static class SftpProtocol
{
    public const uint Version = 3;

    public const byte Init = 1, VersionReply = 2, Open = 3, Close = 4, Read = 5, Write = 6, LStat = 7, FStat = 8, SetStat = 9, FSetStat = 10,
        OpenDir = 11, ReadDir = 12, Remove = 13, MkDir = 14, RmDir = 15, RealPath = 16, Stat = 17, Rename = 18, ReadLink = 19, Symlink = 20,
        Status = 101, Handle = 102, Data = 103, Name = 104, Attrs = 105, Extended = 200, ExtendedReply = 201;

    public const uint Ok = 0, Eof = 1, NoSuchFile = 2, PermissionDenied = 3, Failure = 4, BadMessage = 5, OpUnsupported = 8;

    public const uint AttrSize = 0x1, AttrUidGid = 0x2, AttrPermissions = 0x4, AttrAcModTime = 0x8, AttrExtended = 0x80000000;

    public const uint OpenRead = 0x1, OpenWrite = 0x2, OpenAppend = 0x4, OpenCreate = 0x8, OpenTruncate = 0x10, OpenExclusive = 0x20;

    public const string PosixRename = "posix-rename@openssh.com";
    public const string Limits = "limits@openssh.com";
}

/// <summary>A request's attributes: what Pulsar acts on (the modification time); the rest is read and ignored.</summary>
internal readonly record struct SftpAttributes(ulong? Size, uint? Permissions, uint? ModifiedTime);

/// <summary>A malformed packet: answered with SSH_FX_BAD_MESSAGE.</summary>
internal sealed class SftpBadMessageException(string message) : Exception(message);

/// <summary>Reads one packet's fields, never past its end.</summary>
internal ref struct SftpReader(ReadOnlySpan<byte> packet)
{
    private static readonly UTF8Encoding s_strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly ReadOnlySpan<byte> _packet = packet;
    private int _position;

    public readonly int Remaining => _packet.Length - _position;

    public byte Byte()
    {
        Need(1);
        return _packet[_position++];
    }

    public uint UInt32()
    {
        Need(4);
        var value = BinaryPrimitives.ReadUInt32BigEndian(_packet[_position..]);
        _position += 4;
        return value;
    }

    public ulong UInt64()
    {
        Need(8);
        var value = BinaryPrimitives.ReadUInt64BigEndian(_packet[_position..]);
        _position += 8;
        return value;
    }

    public ReadOnlySpan<byte> Bytes()
    {
        var length = UInt32();
        if (length > (uint)Remaining)
        {
            throw new SftpBadMessageException("A string runs past the end of the packet.");
        }

        var bytes = _packet.Slice(_position, (int)length);
        _position += (int)length;
        return bytes;
    }

    /// <summary>UTF-8 text; invalid bytes are a bad message, never replaced.</summary>
    public string String()
    {
        var bytes = Bytes();
        try
        {
            return s_strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new SftpBadMessageException("A string is not valid UTF-8.");
        }
    }

    public SftpAttributes Attributes()
    {
        var flags = UInt32();
        ulong? size = (flags & SftpProtocol.AttrSize) != 0 ? UInt64() : null;
        if ((flags & SftpProtocol.AttrUidGid) != 0)
        {
            UInt32();
            UInt32();
        }

        uint? permissions = (flags & SftpProtocol.AttrPermissions) != 0 ? UInt32() : null;
        uint? modified = null;
        if ((flags & SftpProtocol.AttrAcModTime) != 0)
        {
            UInt32();
            modified = UInt32();
        }

        if ((flags & SftpProtocol.AttrExtended) != 0)
        {
            var count = UInt32();
            for (var i = 0u; i < count; i++)
            {
                // Each pair takes at least 8 bytes: a count past the packet fails here, not after a long loop.
                Bytes();
                Bytes();
            }
        }

        return new SftpAttributes(size, permissions, modified);
    }

    private readonly void Need(int bytes)
    {
        if (Remaining < bytes)
        {
            throw new SftpBadMessageException("The packet is too short.");
        }
    }
}

/// <summary>Builds one reply packet (its length first) in a pooled buffer.</summary>
internal sealed class SftpWriter : IDisposable
{
    private byte[] _buffer;
    private int _length;

    public SftpWriter(byte type, int capacity = 256)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(capacity, 64));
        _length = 4;
        Byte(type);
    }

    public int Length => _length;

    public ReadOnlyMemory<byte> Packet
    {
        get
        {
            BinaryPrimitives.WriteUInt32BigEndian(_buffer, (uint)(_length - 4));
            return _buffer.AsMemory(0, _length);
        }
    }

    public SftpWriter Byte(byte value)
    {
        Ensure(1);
        _buffer[_length++] = value;
        return this;
    }

    public SftpWriter UInt32(uint value)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(_length), value);
        _length += 4;
        return this;
    }

    public SftpWriter UInt64(ulong value)
    {
        Ensure(8);
        BinaryPrimitives.WriteUInt64BigEndian(_buffer.AsSpan(_length), value);
        _length += 8;
        return this;
    }

    public SftpWriter Bytes(ReadOnlySpan<byte> value)
    {
        UInt32((uint)value.Length);
        Ensure(value.Length);
        value.CopyTo(_buffer.AsSpan(_length));
        _length += value.Length;
        return this;
    }

    public SftpWriter String(string value)
    {
        var count = Encoding.UTF8.GetByteCount(value);
        UInt32((uint)count);
        Ensure(count);
        _length += Encoding.UTF8.GetBytes(value, _buffer.AsSpan(_length));
        return this;
    }

    /// <summary>Room for <paramref name="count"/> bytes written directly (<see cref="Advance"/> after).</summary>
    public Span<byte> Reserve(int count)
    {
        Ensure(count);
        return _buffer.AsSpan(_length, count);
    }

    public void Advance(int count) => _length += count;

    /// <summary>Overwrites four bytes written earlier (a count known only at the end).</summary>
    public void Patch(int position, uint value) => BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(position, 4), value);

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
    }

    private void Ensure(int more)
    {
        if (_length + more <= _buffer.Length)
        {
            return;
        }

        var larger = ArrayPool<byte>.Shared.Rent(Math.Max(_buffer.Length * 2, _length + more));
        _buffer.AsSpan(0, _length).CopyTo(larger);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = larger;
    }
}
