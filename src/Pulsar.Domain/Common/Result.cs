namespace Pulsar.Domain.Common;

/// <summary>A business rule violation. <see cref="Code"/> is stable and machine readable; the message is for logs and developers.</summary>
public sealed record DomainError(string Code, string Message);

/// <summary>Outcome of an operation that can fail because of a business rule (not because of a bug or the infrastructure).</summary>
public readonly record struct Result
{
    private Result(DomainError? error) => Error = error;

    public DomainError? Error { get; }

    public bool IsSuccess => Error is null;

    public static Result Success() => new(null);

    public static Result Failure(DomainError error) => new(error ?? throw new ArgumentNullException(nameof(error)));

    public static implicit operator Result(DomainError error) => Failure(error);
}

/// <summary>Outcome with a value on success.</summary>
public readonly record struct Result<T>
{
    private readonly T? _value;

    private Result(T? value, DomainError? error)
    {
        _value = value;
        Error = error;
    }

    public DomainError? Error { get; }

    public bool IsSuccess => Error is null;

    /// <summary>The value; throws when the result is a failure, which is always a bug in the caller.</summary>
    public T Value => IsSuccess ? _value! : throw new InvalidOperationException($"Result is a failure: {Error!.Code}");

    // Created through implicit conversions (no static factories on a generic type, CA1000).
    public static implicit operator Result<T>(T value) => new(value, null);

    public static implicit operator Result<T>(DomainError error) => new(default, error ?? throw new ArgumentNullException(nameof(error)));
}
