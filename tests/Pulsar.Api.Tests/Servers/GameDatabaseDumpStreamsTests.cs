using System.Diagnostics;
using Pulsar.Application.Databases;
using Pulsar.Infrastructure.Databases;

namespace Pulsar.Api.Tests.Servers;

public sealed class GameDatabaseDumpStreamsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(81920, 81920)]
    [InlineData(81921, 81921)]
    public async Task AnExactDumpIsCopiedIncludingTheEmptyOne(int length, int limit)
    {
        using var source = new MemoryStream(new byte[length]);
        using var destination = new MemoryStream();
        var guarded = 0;
        var copied = await GameDatabaseDumpStreams.CopyAsync(source, destination, limit, bytes => guarded += bytes, Ct, exact: true);

        Assert.Equal(length, copied);
        Assert.Equal(length, destination.Length);
        Assert.Equal(length, guarded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(81920)]
    public async Task AByteBeyondTheBudgetIsNeverWritten(int limit)
    {
        using var source = new MemoryStream(new byte[limit + 1]);
        using var destination = new MemoryStream();
        await Assert.ThrowsAsync<GameDatabaseDumpException>(() =>
            GameDatabaseDumpStreams.CopyAsync(source, destination, limit, _ => { }, Ct));

        Assert.True(destination.Length <= limit);
        Assert.Equal(limit + 1, source.Position);
    }

    [Fact]
    public async Task AShortRestoreIsRefused()
    {
        using var source = new MemoryStream([1, 2]);
        using var destination = new MemoryStream();
        var error = await Assert.ThrowsAsync<GameDatabaseDumpException>(() =>
            GameDatabaseDumpStreams.CopyAsync(source, destination, 3, _ => { }, Ct, exact: true));

        Assert.Contains("2 of 3", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASecondWriterChecksRoomAfterTheFirstWroteItsBlock()
    {
        var free = 3;
        var checks = 0;
        void EnsureRoom(int bytes)
        {
            checks++;
            if (bytes > free)
            {
                throw new GameDatabaseDumpException("No room for this block.");
            }
        }

        using var firstSource = new MemoryStream([1, 2]);
        using var secondSource = new MemoryStream([3, 4]);
        using var firstDestination = new PausingStream(bytes => free -= bytes);
        using var secondDestination = new MemoryStream();
        var first = GameDatabaseDumpStreams.CopyAsync(firstSource, firstDestination, 2, EnsureRoom, Ct);
        await firstDestination.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var second = GameDatabaseDumpStreams.CopyAsync(secondSource, secondDestination, 2, EnsureRoom, Ct);
        try
        {
            Assert.Equal(1, checks);
        }
        finally
        {
            firstDestination.Release.TrySetResult();
        }

        await first;
        await Assert.ThrowsAsync<GameDatabaseDumpException>(() => second);
        Assert.Equal(2, checks);
        Assert.Equal(2, firstDestination.Length);
        Assert.Equal(0, secondDestination.Length);
    }

    [Fact]
    public async Task AWriterFailureStopsTheProducerAndObservesBothReaders()
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        stopping.CancelAfter(TimeSpan.FromSeconds(10));
        using var process = StartProducer(wait: true);
        using var destination = new MemoryStream();
        var output = GameDatabaseDumpStreams.CopyAsync(process.StandardOutput.BaseStream, destination, 3, _ => { }, stopping.Token);
        var errors = process.StandardError.ReadToEndAsync(stopping.Token);

        await Assert.ThrowsAsync<GameDatabaseDumpException>(() => GameDatabaseDumpStreams.WaitAsync(process, output, errors, stopping));

        Assert.True(process.HasExited);
        Assert.True(output.IsCompleted);
        Assert.True(errors.IsCompleted);
        Assert.True(stopping.IsCancellationRequested);
        Assert.True(destination.Length <= 3);
    }

    [Fact]
    public async Task CancellationStopsTheProducerAndObservesBothReaders()
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var process = StartProducer(wait: true);
        var output = process.StandardOutput.ReadToEndAsync(stopping.Token);
        var errors = process.StandardError.ReadToEndAsync(stopping.Token);
        var waiting = GameDatabaseDumpStreams.WaitAsync(process, output, errors, stopping);
        await stopping.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        Assert.True(process.HasExited);
        Assert.True(output.IsCompleted);
        Assert.True(errors.IsCompleted);
    }

    [Fact]
    public async Task ACompleteProducerIsDrainedNormally()
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        stopping.CancelAfter(TimeSpan.FromSeconds(10));
        using var process = StartProducer(wait: false);
        using var destination = new MemoryStream();
        var output = GameDatabaseDumpStreams.CopyAsync(process.StandardOutput.BaseStream, destination, 4, _ => { }, stopping.Token, exact: true);
        var errors = process.StandardError.ReadToEndAsync(stopping.Token);
        await GameDatabaseDumpStreams.WaitAsync(process, output, errors, stopping);

        Assert.Equal(0, process.ExitCode);
        Assert.Equal("1234", System.Text.Encoding.ASCII.GetString(destination.ToArray()));
        Assert.False(stopping.IsCancellationRequested);
    }

    private static Process StartProducer(bool wait)
    {
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")
                : "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Environment.Clear();
        if (OperatingSystem.IsWindows())
        {
            start.Environment["SystemRoot"] = Path.GetDirectoryName(Environment.SystemDirectory);
            start.ArgumentList.Add("-NoLogo");
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("[Console]::Out.Write('1234'); [Console]::Out.Flush();" + (wait ? " Start-Sleep -Seconds 30" : string.Empty));
        }
        else
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("printf '1234';" + (wait ? " /bin/sleep 30" : string.Empty));
        }

        return Process.Start(start) ?? throw new InvalidOperationException("The disposable test producer did not start.");
    }

    private sealed class PausingStream(Action<int> written) : MemoryStream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
            written(buffer.Length);
        }
    }
}
