# 09 — Technical roadmap

> Status: **proposal under review**.
> Every phase ends with a clean build, green tests and a verifiable demo. A phase does not start before the previous one is closed.

## Phase 0 — Foundations and spikes (de-risking) · *in progress*

**Repository** (done): `.slnx` solution, `Directory.Build.props`, `Directory.Packages.props`, `global.json`, `.editorconfig`, projects with the references of doc 01, architecture tests, minimal panel (health endpoint, SPA hosting, 404 for unknown backend routes), minimal agent host (Windows service ready), Vite + React + Tailwind + i18n skeleton with Vitest and Playwright, CI (Windows + Linux, `dotnet format --verify-no-changes`, tests, web lint/test/build, E2E), CodeQL, Dependabot, open source files (README, LICENSE AGPL-3.0, CONTRIBUTING with DCO, SECURITY, CODE_OF_CONDUCT), issue/PR templates, English documentation.

**Spikes** (throwaway code in `spikes/`, findings in `docs/spikes/`):

1. ConPTY vs pipes with `cs2.exe`, `srcds.exe` (CS:S, GMod) and `steamcmd.exe`: capture, colors, input, Ctrl+C, stop.
2. `CreateProcessAsUser` + dedicated account + ConPTY + `JOB_LIST` from a service in session 0 (window station/desktop, `LoadUserProfile`). Run in Windows Sandbox or a VM, never on a developer's main machine.
3. SteamCMD: bootstrap, progress format, exit codes, executable signature.
4. Real readiness patterns for CS2/CS:S/GMod.

**Done when:** CI is green on both OSes; spike reports confirm or correct docs 04 and 06 and ADR-0006/0007.

## Phase 1 — Panel core

- PostgreSQL connection with a clear failure, migrations, health checks (`/health/live`, `/health/ready`).
- Identity, login/logout, initial setup with a token, cookies, antiforgery, rate limiting.
- Permission catalog, roles, assignments, `IPermissionEvaluator`, per-endpoint metadata + coverage test.
- Audit infrastructure.
- OpenAPI + generated TypeScript client.
- Frontend: router, shell, login/setup, user and role management.
- ProblemDetails conventions, `/api/v1` versioning.

**Done when:** an admin installs the panel, connects PostgreSQL, creates their user, creates another user with a limited role, and the actions are audited. Authorization matrix tests.

## Phase 2 — Nodes and protocol

- `Pulsar.Contracts` v1, `AgentHub`, enrollment, challenge/token, signed commands, anti-replay, revocation, rotation.
- Agent: Windows service host, `enroll`/`status`/`run` CLI, connection with reconnect, `Hello`/inventory, node metrics, file logging.
- UI: node creation with the enrollment command, node list and node page (overview/system).

**Done when:** an agent pairs, appears online with its inventory, survives network cuts, and revoking it disconnects it immediately. Tests: invalid signature, replay, revoked node, reused/expired token.

## Phase 3 — Windows process management

- `IGameProcessManager` + `WindowsGameProcessManager` (Job, ConPTY/pipes, per-server accounts, ACL, desktop).
- `ServerSupervisor` with a mailbox, state machine, stepwise stop, crash + backoff.
- `Pulsar.TestGameServer` and the Windows integration suite.
- `_test` manifest (no SteamCMD) to exercise the whole lifecycle.

**Done when:** with the test game, a server is created, started, stopped, restarted and killed from the panel; no orphans in any test scenario; the process runs as its own account.

## Phase 4 — Manifests, SteamCMD and server creation

- `Pulsar.Manifests` (parser, schema, validator, renderer, encoders), published JSON Schema.
- Bundled templates and import; CS2 manifest.
- Port allocations, optional firewall management.
- `SteamCmdService` (queue, progress, retries, cancellation); install/update operations.
- UI: creation wizard, Startup and Network tabs, live install progress.

**Done when:** a CS2 server is created, installs through SteamCMD with live progress, and starts with the configured map/slots/port/arguments.

## Phase 5 — Real-time console and metrics

- Ring buffer, rotated logs, subscriptions, backlog by sequence, audited command input.
- Job-based metrics, `PanelHub`, in-memory cache.
- UI: xterm console, server header with CPU/RAM/uptime, dashboard v1.

**Done when:** live CS2 console with colors, commands, reconnect without gaps; CPU/RAM/uptime updating without polling.

## Phase 6 — File manager

- Agent file operations with the four layers of defense; transfers through tickets.
- UI: file browser, text editor, upload/download.

**Done when:** the path traversal suite (lexical + real junctions/symlinks/hard links) is green and editing `server.cfg` works.

## Phase 7 — Backups

- Creation with manifest globs, streaming zip, progress, cancellation, anti zip-slip restore, deletion, download, per-server retention.

**Done when:** creating, downloading and restoring a CS2 backup works, with duration/size recorded and without blocking the agent.

## Phase 8 — Scheduler

- Panel `BackgroundService`: tasks with `next_run_at`, selection with `FOR UPDATE SKIP LOCKED`, waits until the next run (no aggressive polling; woken when tasks change), occurrences computed with **Cronos** honoring time zones and DST.
- Typed actions: start, stop, restart, backup, update, console_command. Run history.

**Done when:** a daily scheduled restart runs at the correct local time and is audited; runs missed while the panel was down are marked `skipped` (not executed in a burst).

## Phase 9 — Hardening and v1.0 release

- Full security review against doc 08, enforceable 2FA, headers, limits.
- Playwright E2E of the complete v1 flow (with the `_test` game in CI; real CS2 in a manual/nightly job because of its size).
- CS:S and GMod manifests.
- Packaging: panel and agent as Windows services (`install-service`), installation/update/backup guides; an "all-in-one" installer for a single PC (panel + agent paired automatically).
- Manifest contribution documentation.

**Done when:** the full v1 flow works on a clean Windows Server 2022 and Windows 11 installation following only the documentation.

## Post-v1 (tentative order)

SCP: Secret Laboratory → Minecraft (managed runtimes, `download` step) → MySQL/MariaDB databases for games → aggregated metric history → A2S query → remote backups (S3-compatible) → per-Job resource limits → shim to survive agent restarts → user API keys → non-anonymous Steam login → optional mTLS mode → VSS backups → Linux agent.

## Cross-cutting testing strategy

| Layer | Tool | Focus |
|---|---|---|
| Architecture | xUnit (`Pulsar.Architecture.Tests`) | Project dependency rules |
| Domain | xUnit | State machines, permission evaluation, anti-escalation rules |
| Manifests | xUnit | Schema, validation, rendering, injection cases, every `games/*` |
| Application/API | xUnit + `WebApplicationFactory` + real PostgreSQL (Testcontainers on Linux; connection string from an environment variable on Windows) | Authorization (endpoint × role matrix), IDOR, flows, migrations |
| Protocol | xUnit | Signature, replay, expiry, versioning, revoked session |
| Agent Core | xUnit | Supervisor, mailbox, SteamCMD parser, path validator |
| Agent Windows | xUnit (Windows runner) | Jobs, accounts, ACL, ConPTY, junctions, process cleanup |
| Frontend | Vitest + Testing Library | Components, realtime hooks, permissions in the UI |
| E2E | Playwright | Complete v1 flow |

All .NET tests run on Microsoft.Testing.Platform (xUnit v3). Commits follow Conventional Commits, one coherent change per commit, small pull requests.
