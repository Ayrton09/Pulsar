# ADR-0006 — Job Objects with KILL_ON_JOB_CLOSE: servers do not survive the agent (v1)

Status: Proposed — to be confirmed by the Phase 0 spikes.

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
