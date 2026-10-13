using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Windows.Processes;

namespace Pulsar.Agent.Windows.Tests;

/// <summary>The Job Object of a server (ADR-0006): calls made while it is being disposed must never reach a reused handle.</summary>
public sealed class JobObjectTests
{
    [WindowsFact]
    public void ACapChangedAfterTheJobWasDisposedIsRefusedWithoutTouchingItsOldHandle()
    {
        var job = JobObject.Create();
        job.Dispose();

        // The handle value is closed (and may belong to another job by now): SetInformationJobObject must not be called with it.
        Assert.Throws<ObjectDisposedException>(() => job.SetLimits(new ProcessLimits(50, null)));
        Assert.Throws<ObjectDisposedException>(() => job.Usage());
        Assert.Equal(0, job.ActiveProcesses);
    }

    [WindowsFact]
    public async Task CapsChangedWhileTheJobIsDisposedNeverFailWithAnythingButTheDisposedError()
    {
        for (var round = 0; round < 50; round++)
        {
            var job = JobObject.Create();
            var changing = Task.Run(() =>
            {
                try
                {
                    for (var i = 0; i < 200; i++)
                    {
                        job.SetLimits(new ProcessLimits(1 + (i % 100), null));
                        _ = job.ActiveProcesses;
                        _ = job.Usage();
                    }
                }
                catch (ObjectDisposedException)
                {
                    // The job was disposed under it: the expected end.
                }
            }, TestContext.Current.CancellationToken);

            await Task.Delay(1, TestContext.Current.CancellationToken);
            job.Dispose();
            await changing; // any other exception (an invalid handle error) fails the test
        }
    }
}
