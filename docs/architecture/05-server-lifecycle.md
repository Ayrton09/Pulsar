# 05 — Game server lifecycle

> Status: **proposal under review**.

## Three state dimensions (not a single enum)

Mixing "installing", "online" and "backing up" in a single enum produces combinations that are impossible to maintain. Pulsar separates them:

| Dimension | Values | Owner | Persistence |
|---|---|---|---|
| `InstallState` | `NotInstalled`, `Installing`, `Installed`, `Failed` | Panel (from operation results) | `servers.install_state` |
| `RuntimeState` | `Offline`, `Starting`, `Running`, `Stopping`, `Crashed` | **Agent** (observed) | `servers.runtime_state` (last observed) |
| `DesiredState` | `Stopped`, `Running` | **Panel** (user/scheduler intent) | `servers.desired_state` |
| Active operation | `ActiveOperationId` → `Install/Update/Reinstall/Backup/Restore/Delete` | Panel | `servers.active_operation_id` |
| Overlays | `IsSuspended`, node disconnected | Panel | column / memory |

The UI derives a single **display status**: `Installing 42 %`, `Updating`, `Restoring backup`, `Starting`, `Online`, `Stopping`, `Offline`, `Crashed`, `Suspended`, `Node disconnected (last state: Online)`.

## Runtime state machine (in the agent)

```
                 Start                         readiness OK
   ┌─────────┐ ───────────► ┌──────────┐ ─────────────────► ┌─────────┐
   │ Offline │              │ Starting │                    │ Running │
   └─────────┘ ◄─────────── └──────────┘                    └─────────┘
      ▲   ▲   exit while        │  unexpected exit              │   │
      │   │   Stopping          ▼                               │   │ unexpected exit
      │   │              ┌──────────┐ ◄─────────────────────────┘   │
      │   │              │ Crashed  │ ◄─────────────────────────────┘
      │   │              └──────────┘
      │   │                   │ auto-restart (backoff) → Starting
      │   │   Stop/Kill       │ manual Start → Starting
      │   └──────────── ┌──────────┐ ◄── Stop / Kill / Restart (from Starting or Running)
      └──────────────── │ Stopping │
         exit / kill    └──────────┘
```

Transitions:

| From | Event | To | Notes |
|---|---|---|---|
| Offline/Crashed | `Start` | Starting | Requires `Installed`, not suspended, no exclusive operation, free ports. |
| Starting | readiness OK | Running | Via `logPattern`/`processAlive`. |
| Starting | readiness timeout | Running + alert | Still alive: a server is not killed for missing the pattern; an alert is raised. |
| Starting/Running | `Stop` | Stopping | Runs the manifest's stop steps. |
| Starting/Running | `Kill` | Stopping | Immediate `TerminateJobObject`. |
| Stopping | main process exits | Offline | If processes remain in the Job after 5 s → the Job is terminated. |
| Stopping | stop timeout | Offline | Job killed; `Reason = StopTimeoutKilled`. |
| Starting/Running | unexpected exit | Crashed | Exit code recorded. |
| Crashed | auto-restart policy | Starting | Only if `DesiredState = Running` and `restart_on_crash`. |

`Restart` = `Stop` + `Start` as **one** unit in the server mailbox (nothing can interleave).

### Crash policy

- Backoff: 5 s, 15 s, 60 s; at most 3 restarts within 10 minutes. Past the limit the server stays `Crashed` with a "crash loop" alert and `DesiredState` becomes `Stopped`.
- Exit code 0 while `Running` without a stop request (e.g. someone typed `quit` in the console): treated as a stop requested by the game → `Offline` and `DesiredState = Stopped`, no restart. (Configurable per manifest if some game uses exit 0 to mean "restart".)

## Concurrency: one mailbox per server

In the agent, every server has a `ServerSupervisor` with a **single-reader** `Channel<ServerMessage>`. Commands (`Start`, `Stop`, `Install`, `Backup`…) and internal events (process exited, readiness, timeout) are processed **in order, one at a time**. This removes locks and race conditions ("Stop arrives while Start is creating the process"). Urgent messages (`Kill`, `CancelOperation`) cancel the `CancellationToken` of the work in progress before being queued.

In the panel, exclusivity is guaranteed with optimistic concurrency (`xmin`) on `servers` when setting `active_operation_id`.

Compatibility matrix (active operation vs requested action):

| Active \ Requested | Start | Stop/Kill | Install/Update | Backup | Restore | Edit files |
|---|---|---|---|---|---|---|
| (none) | ✓ | ✓ | requires Offline | ✓ (warning if Running) | requires Offline | ✓ |
| Install/Update | ✗ | Kill cancels the operation | ✗ | ✗ | ✗ | ✗ |
| Backup | ✓ | ✓ | ✗ | ✗ | ✗ | ✓ |
| Restore | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ |

## Full creation flow (v1)

```
1. POST /api/v1/servers {nodeId, templateId, name, variables, ports?}
   - authorizes server.create on the node; validates variables against the manifest
   - assigns allocations (the requested ones or the first free ones of the node)
   - generates short_id; creates Server(NotInstalled, Offline, Stopped); audits server.created
2. Operation server_provision → ProvisionServerCommand
   Agent: creates <ServersRoot>\<shortId>, a dedicated Windows account, ACLs, firewall rules (if enabled)
3. Operation server_install → InstallServerCommand
   Agent: SteamCMD queue → progress via OperationProgress → OperationCompleted
   Panel: install_state = Installed | Failed
4. The user adjusts map/slots/ports/arguments → ApplyServerSpecCommand
5. Start → … → Running
```

## Reconciliation (the key to stability)

On connect, the agent sends `AgentHello` with the actual state of every server. The panel:

1. Updates every server's `runtime_state` with what was observed (the agent wins).
2. Servers known to the panel but not to the agent → "unknown server on node" alert (nothing is recreated automatically).
3. Servers known to the agent but not to the panel (deleted while the node was offline) → the panel sends `DeleteServerCommand` only if the deletion is recorded in the audit log; otherwise it raises an alert.
4. Operations `running` in the database that the agent reports neither in flight nor in its outbox → `interrupted`.
5. Sends each server's `DesiredState`; the agent does **not** start servers because of reconciliation, except `auto_start` after boot (so reconnecting never starts servers unexpectedly).

Cases:

| Situation | Behavior |
|---|---|
| Panel down | Servers keep running. Crash auto-restart works (local). Scheduler paused. |
| Agent restarts / is updated | Its servers **stop** (Job with KILL_ON_JOB_CLOSE, see doc 06). On start, it starts those with `auto_start` or that were `Running` before an orderly service stop. |
| Windows reboot | Same as above: automatic service start + `auto_start`. |
| Flaky network | Reconnect with backoff; the console is backfilled from the ring buffer by `Sequence`. |

## Deletion

`DeleteServerCommand` (operation): stops (kills if needed), removes firewall rules, deletes the directory **without following reparse points**, deletes backups if requested, removes the Windows account and its profile. The panel deletes the row when it receives `succeeded` (the audit trail remains) and frees the allocations.
