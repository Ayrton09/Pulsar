namespace Pulsar.Agent.Core.Servers;

/// <summary>Copies a mutable file up to a byte budget before decoding or parsing it.</summary>
internal static class BoundedFileContents
{
    /// <summary>Returns null after reading the first excess byte. Leaves the source open.</summary>
    public static MemoryStream? Read(Stream source, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        var content = new MemoryStream(Math.Min(maximumBytes, 4096));
        try
        {
            Span<byte> buffer = stackalloc byte[4096];
            while (true)
            {
                var remaining = maximumBytes - (int)content.Length;
                var count = source.Read(buffer[..(remaining >= buffer.Length ? buffer.Length : remaining + 1)]);
                if (count == 0)
                {
                    content.Position = 0;
                    return content;
                }

                if (count > remaining)
                {
                    content.Dispose();
                    return null;
                }

                content.Write(buffer[..count]);
            }
        }
        catch
        {
            content.Dispose();
            throw;
        }
    }
}
