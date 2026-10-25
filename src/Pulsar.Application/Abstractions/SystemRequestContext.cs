using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;

namespace Pulsar.Application.Abstractions;

/// <summary>The panel acting by itself (the game updates): audited as the system, under <paramref name="name"/>.</summary>
public sealed class SystemRequestContext(string name) : IRequestContext
{
    public UserId? UserId => null;

    public AuditActor Actor { get; } = new(AuditActorType.System, null, name);

    public AuditRequestInfo RequestInfo => AuditRequestInfo.None;
}
