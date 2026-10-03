using Pulsar.Agent.Core.Files;

namespace Pulsar.Agent.Core.Tests;

public sealed class DirectServerFileManagerTests : ServerFileManagerContract
{
    protected override IServerFileManager Files { get; } = new DirectServerFileManager();
}
