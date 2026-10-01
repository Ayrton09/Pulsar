namespace Pulsar.Api;

/// <summary>
/// The panel cannot start because of its configuration. The message is meant for the administrator, contains no
/// secrets, and is logged without a stack trace.
/// </summary>
public sealed class PanelStartupException : Exception
{
    public PanelStartupException(string message)
        : base(message)
    {
    }

    public PanelStartupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public PanelStartupException()
    {
    }
}
