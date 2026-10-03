# Architecture Decision Records

Format: context → decision → alternatives → consequences. An accepted ADR is not edited: it is superseded by a new one that marks it as *Superseded*.

Statuses: `Proposed` · `Accepted` · `Superseded by NNNN` · `Rejected`.

To add an ADR, copy an existing one and use the next number.

| ADR | Title | Status |
|---|---|---|
| [0001](0001-modular-monolith-single-instance.md) | Modular monolith with a single panel instance | Proposed |
| [0002](0002-agent-initiated-signalr-connection.md) | Agent-initiated persistent connection over SignalR | Proposed |
| [0003](0003-node-identity-and-signed-commands.md) | Per-node keys, proof of possession and signed commands | Proposed |
| [0004](0004-uuidv7.md) | Application-generated UUIDv7 identifiers | Accepted |
| [0005](0005-windows-account-per-server.md) | Agent as LocalSystem, one Windows account per game server | Accepted |
| [0006](0006-kill-on-job-close.md) | Job Objects with KILL_ON_JOB_CLOSE | Accepted |
| [0007](0007-console-conpty-or-pipes.md) | Console modes per manifest (ConPTY, pipes, log file + RCON) | Accepted |
| [0008](0008-declarative-manifests.md) | Declarative manifests with closed steps and version pinning | Proposed |
| [0009](0009-identity-cookies-and-scoped-rbac.md) | ASP.NET Core Identity with cookies and scoped RBAC | Accepted |
| [0010](0010-panel-side-scheduler.md) | Panel-side scheduler on PostgreSQL | Accepted |
| [0011](0011-secrets-with-data-protection.md) | Secrets encrypted with ASP.NET Core Data Protection | Proposed |
| [0012](0012-node-local-zip-backups.md) | Node-local zip backups (v1) | Accepted |
| [0013](0013-licensing.md) | AGPL-3.0 core, MIT manifests and schemas, DCO | Accepted |
