# 05 — Game server lifecycle

> Status: **implemented in Phase 3** for the test game (see "Implementation (Phase 3)" at the end).

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
| Addon install/remove (Phase 12; requires Offline) | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ |

## Full creation flow (v1)

```
1. POST /api/v1/servers {nodeId, templateId, name, variables?, ports?}
   - authorizes server.create; validates variables against the manifest (secrets encrypted)
   - assigns allocations (the requested ones, or the free one on each required port's default, else the lowest free)
   - renders the whole manifest once (values, ports) before anything reaches the node
   - generates short_id; creates Server(NotInstalled, Offline, Stopped); audits server.created
2. Operation server_provision → ProvisionServerCommand
   Agent: creates <ServersRoot>\<shortId>, a dedicated Windows account, ACLs; a new RCON password
3. Operation server_install → InstallServerCommand
   Agent: the server's own SteamCMD, as its account → progress via OperationProgress → OperationCompleted
   Panel: install_state = Installed | Failed
4. The user adjusts map/slots/ports/arguments (Settings and Network tabs); stored by the panel
5. Start → ApplyServerSpecCommand, then StartServerCommand → configuration files written, firewall rules
   (if managed) → … → Running
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
| Panel down | Servers keep running. Crash auto-restart works (local). Scheduler paused: runs missed meanwhile are recorded as skipped, not done late (Phase 8). |
| Agent restarts / is updated / crashes | Its servers **keep running** in their host processes (Phase 14, doc 06 "Server hosts"): the agent takes them over when it starts, before its first Hello, and their consoles continue where it stopped reading; a game that exited meanwhile is handled then (crash policy). When Windows shuts down, or the node is unenrolled, or `service stop --stop-servers` is used, the servers stop gracefully; on start, the agent starts those with `auto_start` or that were `Running` before that stop. A host that is gone (the machine restarted without stopping them) counts as an agent crash: only auto-start servers start. With `Pulsar:ServerHosts` false, servers stop with the agent as before Phase 14. |
| Windows reboot | Same as above: automatic service start + `auto_start`. |
| Flaky network | Reconnect with backoff; the console is backfilled from the ring buffer by `Sequence`. |

## Deletion

`DeleteServerCommand` (operation): stops (kills if needed), kills processes of its account outside the Job, removes firewall rules, deletes the directory **without following reparse points**, deletes its backups (Phase 7: always; the panel drops their rows with the server), removes the Windows account and its profile. The panel deletes the row when it receives `succeeded` (the audit trail remains) and frees the allocations.

## Implementation (Phase 3)

- **Agent:** `ServerSupervisor` (state machine and mailbox: readiness, stop steps, timeouts and crash backoff are timers posting messages, tagged with the process generation) and `ServerHost` (stored specs in `state\servers`, the supervisors, the commands, and operations whose results wait in `state\outbox` until the panel has them). **Panel:** `Server`, `GameTemplate` and `Operation` in the domain, `ServerManagement` (REST), `AgentServerEvents` (hub events and reconciliation).
- **Creation** needs a connected node. The panel creates the server and a provision operation; when the agent reports it done, the panel starts the install operation itself. "Reinstall" runs both again (offline servers only). Lifecycle commands need a connected node too; a node with servers cannot be deleted.
- **Deletion** is an operation: the agent kills the server, removes its folder and account and reports it; the panel then deletes the row (audited as done by the node; the user's request has its own entry). The agent removes the server's record last: a delete that failed half way (its account could not be removed, say) is finished by the retry, which finds the record; once the record is gone a retry only reports success.
- **Unreadable state.** A server record or an outbox result that cannot be read (corrupt JSON, or a record protected by DPAPI on another machine) is renamed `*.json.unreadable` and logged (for a record, with the server's id, which its file name carries); the other servers load and the Hello is sent. Deleting such a server fails with `server.record_unreadable` instead of reporting it gone: its folder and account are named after its short id, which is inside the unreadable record, so the agent leaves them and says so; once they and the set-aside file are removed by hand, deleting it again finishes.
- **Reconciliation** at every Hello: the agent's snapshot of each server is taken whatever its sequence (a restarted agent counts from zero) with reason `reconciled`; running operations the agent does not report are interrupted, except those whose result waits in its outbox (they are reported as in flight) and queued ones younger than a minute. `HelloAccepted` does not carry desired states yet: the agent keeps its own (set by the commands it receives) and resumes servers only as described above.
- **Acks.** A start (or restart) is acknowledged as soon as it is accepted, before the process exists: an account's first logon can take longer than the panel's 10 s ack timeout. A start that fails is reported as `Crashed` with reason `start_failed`. Whatever the failure (an unexpected error included, detail `process.start_failed`), the server never stays `Starting` without a process; a stop that times out with no process left ends `Offline`.
- **Delivery.** State changes are sent without waiting; when the agent stops, it delivers each server's last state again and waits for the panel, so the panel sees its servers stop. The hub stores agent events even if the connection closes right after them, and a result is applied once (deliveries are retried). An operation's result can arrive before the panel has recorded the agent's ack; the result wins (row versions on servers and operations).
- **UI (Phase 3):** one display status per server (an operation first, then the install state, then the runtime state), the reason when it needs explaining (exit code, crash loop, refused start, readiness timeout), and "last known" while its node is disconnected. Polling every 2 s until Phase 5's live channel.

## Implementation (Phase 4)

- **Allocations.** A node's `ip:port` pairs (IPv4; `0.0.0.0` is every interface and cannot coexist with the same port on a specific address), added by ranges on the node page (`node.manage`). A server holds one per manifest port; they are freed when it is deleted, and changed only while it is stopped (`server.settings.edit`).
- **Settings.** Variables, extra arguments (when the manifest allows them) and the restart policy (`server.startup.edit`; variables a manifest marks not user-editable need `server.settings.edit`). Secret values are encrypted with Data Protection and never returned. Changes are stored by the panel and reach the agent before the next start, even if its node was disconnected when they were made: `ApplyServerSpecCommand` precedes every start and restart. A running process keeps the spec it started with.
- **Update and cancel.** The update operation runs the manifest's `update` steps (optionally validating every file); installs and updates can be cancelled (the agent kills SteamCMD's Job and reports `cancelled`). Progress is shown live (the panel's memory) and written to the database on stage changes or every 15 seconds.
- **Templates.** Bundled ones register at startup; administrators (`template.manage`) import others after a preview of what they would run, download and write, and of the sections that differ from the previous version. Versions never change.
- **UI:** a creation wizard (game and node; the game's settings; its ports), Settings and Network tabs, a progress bar with Cancel, Update game, node allocations and a Games page with the import preview.

## Implementation (Phase 7)

- **Backups and the matrix.** The panel records the type of the active operation (`servers.active_operation_type`): during a backup a start or restart is accepted and files can be edited, as the matrix says; any other operation keeps the server from starting. On the node, every operation but a backup is exclusive (start, restart and file changes get `Busy`). A backup needs an installed server and may run while it does. A restore needs it installed and stopped, and nothing else happens to the server until it ends.
- **Backups of a running game (Phase 15).** When the game's process is alive (starting, running or stopping), the agent reads its files from a snapshot of their volume (doc 06, "Backup snapshots"): every file as it was at one moment, open ones included. Before that, a running game whose manifest has `backup.save` is asked to save: its commands go through the console like typed ones (a dim `[Pulsar]` note says why), and the agent waits for the confirmation line or the manifest's time (stage `saving`, then `snapshotting`). The `resume` commands (Minecraft's `save-on`) follow as soon as the snapshot exists; without a snapshot, after the files are read. A record in `state\paused-saves` is written before the save commands and deleted once the game took `resume`: an agent that stops in between sends `resume` to the game it takes over at its next start, so a game is never left not saving. A record whose `resume` the game did not take while it may still run (it was stopping, or its console failed) stays for the next run; a failure there is logged and never keeps the agent from starting. An agent that stops cancels its operations first and waits for them (at most a quarter of its shutdown budget) before it leaves its games running or stops them, so a cancelled backup resumes its game while the agent still holds its console. When no snapshot can be taken the files are read live (files written meanwhile may be half written, files the game holds open are left out and counted). A backup does not keep the server from starting: when a stopped server starts while its files are read (or started and stopped again), the result says `live` with `snapshot.game_started`, not `stopped`. The result says how the files were read (`stopped`, `snapshot` or `live` with a `snapshot.*` reason) and how the game answered (`confirmed`, `waited` or `unconfirmed`); the panel stores and shows both.
- **Clean restores (Phase 15).** A restore can first delete the files the backup's rules select that the backup does not have (stage `cleaning`), after the archive's hash and entries are checked and before anything is written. The rules are the ones the backup was made with: the agent records them in the archive's zip comment (`{"pulsar":1,"include":[…],"exclude":[…]}`, covered by its SHA-256), so a template update that widened them since never deletes files the backup could not have held. Only an archive without a readable record (made before Phase 15) is cleaned by the server's current manifest. Files outside the rules, links and Pulsar's own files stay. A file that cannot be deleted (held open, access denied) does not stop the others, and the backup's files are still written; the restore then fails with that file's `files.*` code and says how many were left. The panel refuses a clean restore of a backup that left files out (`backups.clean_incomplete`), and asks it only of agents that announce `backups.clean_restore` (an older one would ignore the request and keep the files).
- **Restore** writes the backup's files over the server's (each one atomically, as the file manager does) and leaves files the backup does not have as they are. Stray processes of the server's account are killed first, like before an install.
- **Retention.** Each server may keep a number of backups (`backup_limit`, none by default): after every new backup, and when the limit goes down, the oldest complete, unlocked backups beyond it are deleted (audited as done by the system). Locked backups are never deleted by retention, do not count, and must be unlocked before deleting them by hand. What cannot be deleted while the node is disconnected goes at the next run.
- **Interrupted** backups (the agent restarted or lost the operation) fail at the next Hello; the partial archive was already removed by the agent when it started.

## Implementation (Phase 8)

- **Scheduled actions** (start, stop, restart, backup, update, console command) go through the same requests a user's do, with the same rules: a scheduled restart of a server busy with an install fails with `server.busy`, a scheduled backup can run while the server does, a console command needs it running. They are audited as the schedule's, with the task's name. A backup or update the node refuses fails the run at once with the node's reason (and notifies, like a refused start).
- **Only while running.** Restarts and backups can be set to be skipped while the server is not running (a restart would otherwise start a stopped server); console commands are always skipped then.
- **Updates** need the server stopped, as a user's do: a nightly update is a stop, the update and a start, scheduled a few minutes apart. Chaining them in one task is post-v1.
- **Panel down.** Nothing runs. At the next start, each task's occurrences older than a minute are recorded as one skipped run saying how many were missed, and the task moves to its next occurrence; an occurrence less than a minute late still runs. A run the panel was doing when it stopped fails five minutes later (`schedules.interrupted`). An operation a task had started (a backup, an update) goes on in the agent, and the run shows the operation's outcome.

