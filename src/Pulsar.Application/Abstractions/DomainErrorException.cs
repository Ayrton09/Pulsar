using Pulsar.Domain.Common;

namespace Pulsar.Application.Abstractions;

/// <summary>
/// A <see cref="DomainError"/> raised where no result can be returned (a step every request shares, such as reading a
/// stored template): the API answers it as that error's problem, never as a server error. <see cref="Exception.Message"/>
/// may say more than the error does, for the panel's log.
/// </summary>
public sealed class DomainErrorException(DomainError error, string? detail = null) : Exception(detail ?? error?.Message)
{
    public DomainError Error { get; } = error ?? throw new ArgumentNullException(nameof(error));
}
