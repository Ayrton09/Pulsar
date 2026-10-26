namespace Pulsar.Agent.Core.Files;

/// <summary>
/// The CRC-32 a zip records for each entry (ISO 3309, reflected 0xEDB88320). .NET's zip reader does not check it, so
/// a damaged entry, or one cut at the size it declares, would be written out as if whole.
/// </summary>
internal struct Crc32
{
    private static readonly uint[] s_table = Table();
    private uint _value;

    public Crc32() => _value = uint.MaxValue;

    public readonly uint Value => ~_value;

    public void Append(ReadOnlySpan<byte> bytes)
    {
        var value = _value;
        foreach (var b in bytes)
        {
            value = s_table[(value ^ b) & 0xFF] ^ (value >> 8);
        }

        _value = value;
    }

    private static uint[] Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }
}
