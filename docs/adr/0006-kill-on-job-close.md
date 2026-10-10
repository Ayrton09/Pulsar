# ADR-0006 — Job Objects with KILL_ON_JOB_CLOSE: servers do not survive the agent (v1)

Status: Accepted (2026-10-01) — confirmed by [spike 0001](../spikes/0001-console-capture.md): terminating the Job always removed the whole tree (including srcds and the `conhost.exe` it creates), with no orphans.

## Context
Orphaned processes must be impossible and the process tree must always be cleaned up. If the agent dies, the console (ConPTY/pipes) it owns is lost as well.

## Decision
One unnamed Job Object per server, with `KILL_ON_JOB_CLOSE`, `DIE_ON_UNHANDLED_EXCEPTION`, UI restrictions and no breakaway. The process is created inside the Job (`PROC_THREAD_ATTRIBUTE_JOB_LIST` + `CREATE_SUSPENDED`). If the agent terminates, Windows kills all its servers. On start, the agent restarts the servers with `auto_start` or those that were running before an orderly stop.

## Alternatives
- Servers that survive the agent (named Jobs and reattach): the console is lost anyway and ambiguous states appear.
- A shim process per server owning the Job and console: the right long-term answer, but adds IPC and another binary. Deferred; `IGameProcess` is designed to allow it.

## Consequences
- Strong guarantee of no orphans and always-consistent state.
- Updating or restarting the agent means downtime for the node's servers: it must be scheduled and documented.

## Amendment (Phase 14, 2026-10-09): the shim
The deferred alternative is built: each game runs under a host process of its own (`PulsarAgent host`), which owns its Job and console, and the agent drives it over a named pipe (doc 06, "Server hosts"). The Job keeps `KILL_ON_JOB_CLOSE`, now held by the host: the guarantee of no orphans stands (a host that dies takes its game with it), while restarting, updating or crashing the agent no longer stops the games. Windows shutting down still stops them gracefully. The host is the agent's own program in another mode, not another binary; `Pulsar:ServerHosts` set to `false` keeps the v1 behavior.
