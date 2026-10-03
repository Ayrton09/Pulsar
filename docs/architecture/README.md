# Pulsar v1 architecture

> Status: **draft under review**. Phase 0 (repository skeleton and Windows spikes) is in progress; nothing in these documents is implemented beyond that. Decisions that are hard to reverse live in [`../adr`](../adr/README.md).

## Summary in ten lines

1. **Modular monolith** on ASP.NET Core (.NET 10) + PostgreSQL + a React SPA served by the panel itself. One instance, no Redis or external queues.
2. **Pulsar Agent** is a Windows service on every node; **the agent opens** an outbound WebSocket (SignalR) connection to the panel. Nodes open no ports for Pulsar.
3. **Per-node identity** with a non-exportable ECDSA P-256 key (TPM-backed when available); authentication by signed challenge; **every panel command is signed** and replay-protected.
4. **Typed operations only.** No API anywhere executes a shell.
5. **Games are declarative manifests** with a closed set of steps; arguments are rendered as argv without a shell and variables are strictly typed and validated.
6. **One Windows Job Object per server** (`KILL_ON_JOB_CLOSE`, process created inside the Job) → no orphaned processes.
7. **Three console modes** chosen by the manifest: ConPTY, pipes, or log file + RCON (Source engine servers on Windows allocate their own console; see spike 0001).
8. **One Windows account per server**: plugin code never runs as SYSTEM and cannot see other servers.
9. **Scoped RBAC** (global ⊃ node ⊃ server), permissions defined in code, anti-escalation rules.
10. **Event-driven real time** (agent → panel → browser); metrics live in memory, not in PostgreSQL.

## Documents

| # | Document |
|---|---|
| 01 | [Architecture, repository and dependencies](01-architecture-and-repository.md) |
| 02 | [Entities, permissions and PostgreSQL schema](02-domain-and-database.md) |
| 03 | [Panel ↔ Agent protocol and node authentication](03-panel-agent-protocol.md) |
| 04 | [Game manifests](04-game-manifests.md) |
| 05 | [Game server lifecycle](05-server-lifecycle.md) |
| 06 | [Windows process management: Job Objects, ConPTY, isolation](06-windows-process-management.md) |
| 07 | [Frontend](07-frontend.md) |
| 08 | [Security model and risks](08-security-and-risks.md) |
| 09 | [Technical roadmap](09-roadmap.md) |

## Decisions that are hard to change later

| ADR | Decision | Status | Why it is expensive to reverse |
|---|---|---|---|
| [0001](../adr/0001-modular-monolith-single-instance.md) | Modular monolith, single panel instance | Proposed | In-memory state (connections, metrics) assumes one process. |
| [0002](../adr/0002-agent-initiated-signalr-connection.md) | Agent → panel over SignalR/WebSockets | Proposed | Every deployed agent speaks this protocol. |
| [0003](../adr/0003-node-identity-and-signed-commands.md) | Per-node keys + proof of possession + signed commands | Proposed | Changing identity means re-enrolling every node. |
| [0004](../adr/0004-uuidv7.md) | Application-generated UUIDv7 | Accepted | IDs live in the database, URLs, protocol and resource names. |
| [0005](../adr/0005-windows-account-per-server.md) | One Windows account per server, agent as SYSTEM | **Accepted** | ACLs and disk layout of every existing server. |
| [0006](../adr/0006-kill-on-job-close.md) | Servers die with the agent (v1) | **Accepted** | Operational expectations and agent design. |
| [0007](../adr/0007-console-conpty-or-pipes.md) | Console modes per manifest (ConPTY, pipes, log file + RCON); not stored in PostgreSQL | **Accepted** | Stream format and console UX. |
| [0008](../adr/0008-declarative-manifests.md) | Declarative manifests, closed steps, version pinning | Proposed | The community writes manifests against this format. |
| [0009](../adr/0009-identity-cookies-and-scoped-rbac.md) | Identity + cookies; own scoped RBAC | Accepted | User tables and permission model. |
| [0010](../adr/0010-panel-side-scheduler.md) | Panel-side scheduler on PostgreSQL | Accepted | Task semantics when the panel is down. |
| [0011](../adr/0011-secrets-with-data-protection.md) | Secrets with Data Protection | Proposed | The key ring is required to restore the panel. |
| [0012](../adr/0012-node-local-zip-backups.md) | Node-local zip backups (v1) | Accepted | Backup format users accumulate. |
| [0013](../adr/0013-licensing.md) | AGPL-3.0 core, MIT manifests and schemas, DCO | **Accepted** | Relicensing needs every contributor's consent. |

## Decisions already taken

- **Language:** code, comments, commits, issues and public documentation in English. The UI ships in English and Spanish from day one.
- **Licensing:** AGPL-3.0-only for the core; MIT for `games/` and `schemas/`; contributions under the DCO (ADR-0013).
- **Isolation:** one Windows account per game server, implemented in Phase 3 together with process management (ADR-0005).
- **Starting point:** Phase 0 = repository skeleton + Windows spikes in parallel (see [roadmap](09-roadmap.md)).

## Open questions

1. **Panel on Linux.** The panel has nothing Windows-specific; supporting it on Linux costs nothing extra (the agent stays Windows-first). Proposed: yes.
2. **Windows 10.** Out of support since October 2025. Proposed: best effort on 22H2, full support for Windows 11 and Windows Server 2019/2022/2025.
3. **Code of conduct contact.** `CODE_OF_CONDUCT.md` needs a reporting address before the first public release.
