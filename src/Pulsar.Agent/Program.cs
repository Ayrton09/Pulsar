using Pulsar.Agent;

#if !WINDOWS
// The last step of a game's start on Linux: its own arguments (the game's among them), never parsed as the agent's.
if (args.Length > 0 && args[0] == Pulsar.Agent.Linux.GameExec.Command)
{
    return Pulsar.Agent.Linux.GameExec.Run(args);
}

#endif
try
{
    var line = CommandLine.Parse(args, AgentCommands.Flags);
    return line.Command switch
    {
        "enroll" => await AgentCommands.EnrollAsync(line),
        "status" => await AgentCommands.StatusAsync(line),
        "unenroll" => await AgentCommands.UnenrollAsync(line),
        "run" => await AgentHost.RunAsync(line),
        "host" => await HostCommand.RunAsync(line),
        "service" => await AgentCommands.ServiceAsync(line),
        "help" or "h" or "?" => AgentCommands.Help(),
        _ => throw new UsageException($"Unknown command '{line.Command}'."),
    };
}
catch (UsageException ex)
{
    await Console.Error.WriteLineAsync(ex.Message);
    await Console.Error.WriteLineAsync("Run 'PulsarAgent help' for usage.");
    return 2;
}
