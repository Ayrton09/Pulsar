namespace Pulsar.Api.Tests.Infrastructure;

/// <summary>Throwaway Data Protection key directories, so tests never touch %ProgramData%\Pulsar.</summary>
public static class TestKeys
{
    public static string NewDirectory() => Path.Combine(Path.GetTempPath(), "pulsar-tests", "keys-" + Guid.CreateVersion7().ToString("N"));

    public static void Delete(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
