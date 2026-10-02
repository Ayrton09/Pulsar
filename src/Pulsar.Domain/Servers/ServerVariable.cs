using Pulsar.Domain.Common;

namespace Pulsar.Domain.Servers;

/// <summary>
/// A server's value for one manifest variable (docs/architecture/02). Secret values are kept only encrypted
/// (<see cref="ProtectedValue"/>, ASP.NET Core Data Protection) and are never returned by the API.
/// </summary>
public sealed class ServerVariable
{
    public const int MaxValueLength = 4096;

    private ServerVariable()
    {
        // For EF Core materialization.
        Key = null!;
    }

    private ServerVariable(ServerId serverId, string key, DateTimeOffset now)
    {
        ServerId = serverId;
        Key = key;
        UpdatedAt = now;
    }

    public ServerId ServerId { get; private set; }

    public string Key { get; private set; }

    public string? Value { get; private set; }

    public byte[]? ProtectedValue { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public UserId? UpdatedBy { get; private set; }

    public bool IsSecret => ProtectedValue is not null;

    public static ServerVariable Plain(ServerId serverId, string key, string value, UserId? by, DateTimeOffset now)
    {
        var variable = new ServerVariable(serverId, key, now);
        variable.SetPlain(value, by, now);
        return variable;
    }

    public static ServerVariable Secret(ServerId serverId, string key, byte[] protectedValue, UserId? by, DateTimeOffset now)
    {
        var variable = new ServerVariable(serverId, key, now);
        variable.SetSecret(protectedValue, by, now);
        return variable;
    }

    public void SetPlain(string value, UserId? by, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
        ProtectedValue = null;
        UpdatedBy = by;
        UpdatedAt = now;
    }

    public void SetSecret(byte[] protectedValue, UserId? by, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(protectedValue);
        Value = null;
        ProtectedValue = protectedValue;
        UpdatedBy = by;
        UpdatedAt = now;
    }
}
