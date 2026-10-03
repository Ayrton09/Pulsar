namespace Pulsar.Application.Abstractions;

/// <summary>
/// Work done outside an HTTP request acts as someone else (the scheduler, for a task): set <see cref="Context"/> first
/// thing in the scope, before anything resolves <see cref="IRequestContext"/>.
/// </summary>
public sealed class RequestContextOverride
{
    public IRequestContext? Context { get; set; }
}
