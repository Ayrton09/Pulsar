using Pulsar.Application.Abstractions;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;

namespace Pulsar.Application.Scheduling;

/// <summary>A scheduled task acting: audited as the schedule (with the task's name); the entries of one run share its id.</summary>
public sealed class ScheduleRequestContext(string taskName, ScheduledRunId runId) : IRequestContext
{
    public UserId? UserId => null;

    public AuditActor Actor { get; } = new(AuditActorType.Schedule, null, taskName);

    public AuditRequestInfo RequestInfo { get; } = new(null, null, runId.Value.ToString());
}
