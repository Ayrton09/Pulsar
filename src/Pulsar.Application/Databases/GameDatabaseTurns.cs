using Pulsar.Application.Abstractions;
using Pulsar.Domain.Common;

namespace Pulsar.Application.Databases;

/// <summary>
/// Choices of a server's game database take turns (1.0.37): a second one, a double click say, waits for the first, which
/// may still be making the database on its host, and then finds it the game's.
/// </summary>
public sealed class GameDatabaseTurns
{
    public CommandTurns<ServerId> Turns { get; } = new();
}
