using System.Globalization;
using Pulsar.ReleasePacker;

// pulsar-release-packer tar <folder> <output.tar.gz> <commit time, Unix seconds>
// Run by scripts/release/Build-Release.ps1 to pack the Linux tarball (TarPacker).
if (args is ["tar", var folder, var output, var time] && long.TryParse(time, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
{
    try
    {
        TarPacker.Pack(folder, output, DateTimeOffset.FromUnixTimeSeconds(seconds));
        return 0;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
    {
        Console.Error.WriteLine($"pulsar-release-packer: {ex.Message}");
        return 1;
    }
}

Console.Error.WriteLine("usage: pulsar-release-packer tar <folder> <output.tar.gz> <commit time, Unix seconds>");
return 2;
