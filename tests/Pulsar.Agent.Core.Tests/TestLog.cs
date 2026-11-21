using Microsoft.Extensions.Logging;

namespace Pulsar.Agent.Core.Tests;

/// <summary>
/// Loggers that write to the running test's output, so a test that fails on a CI runner shows what the code it drove
/// logged (the server hosts' races there could not be told apart from a test's assertions alone).
/// </summary>
internal static class TestLog
{
    public static ILogger<T> For<T>() => new Logger<T>(TestContext.Current.TestOutputHelper);

    public static ILoggerFactory Factory() => new LoggerFactory(TestContext.Current.TestOutputHelper);

    private sealed class Logger<T>(ITestOutputHelper? output) : ILogger<T>
    {
        private readonly Writer _writer = new(output, typeof(T).Name);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => _writer.Enabled;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _writer.Write(logLevel, formatter(state, exception), exception);
    }

    private sealed class CategoryLogger(ITestOutputHelper? output, string category) : ILogger
    {
        private readonly Writer _writer = new(output, category);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => _writer.Enabled;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _writer.Write(logLevel, formatter(state, exception), exception);
    }

    private sealed class LoggerFactory(ITestOutputHelper? output) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => new CategoryLogger(output, categoryName[(categoryName.LastIndexOf('.') + 1)..]);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class Writer(ITestOutputHelper? output, string category)
    {
        public bool Enabled => output is not null;

        public void Write(LogLevel level, string message, Exception? exception)
        {
            if (output is null)
            {
                return;
            }

            try
            {
                output.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} {level,-11} {category}: {message}{(exception is null ? "" : " | " + exception.GetType().Name + ": " + exception.Message)}");
            }
            catch (InvalidOperationException)
            {
                // Written after its test ended (a host still ending): nobody reads it any more.
            }
        }
    }
}
