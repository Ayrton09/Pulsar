using Pulsar.Contracts;

namespace Pulsar.Agent.Core;

/// <summary>The machine the agent runs on, as reported to the panel.</summary>
public interface INodeSystem
{
    NodeInventory GetInventory(string? serversRoot);

    /// <summary>Node-wide usage since the previous sample (the first sample covers the time since boot).</summary>
    NodeMetrics SampleMetrics();
}
