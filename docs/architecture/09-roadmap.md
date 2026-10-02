# 09 — Technical roadmap

> Status: **proposal under review**.
> Every phase ends with a clean build, green tests and a verifiable demo. A phase does not start before the previous one is closed.

## Phase 0 — Foundations and spikes (de-risking) · *done* (CI pending a remote)

**Repository** (done): `.slnx` solution, `Directory.Build.props`, `Directory.Packages.props`, `global.json`, `.editorconfig`, projects with the references of doc 01, architecture tests, minimal panel (health endpoint, SPA hosting, 404 for unknown backend routes), minimal agent host (Windows service ready), Vite + React + Tailwind + i18n skeleton with Vitest and Playwright, CI (Windows + Linux, `dotnet format --verify-no-changes`, tests, web lint/test/build, E2E), CodeQL, Dependabot, open source files (README, LICENSE AGPL-3.0, CONTRIBUTING with DCO, SECURITY, CODE_OF_CONDUCT), issue/PR templates, English documentation.

**Spikes** (throwaway code in `spikes/`, findings in `docs/spikes/`):

1. ConPTY vs pipes with `cs2.exe`, `srcds.exe` (CS:S, GMod) and `steamcmd.exe`: capture, colors, input, Ctrl+C, stop. *Done for SteamCMD and CS:S ([spike 0001](../spikes/0001-console-capture.md)); CS2 before its manifest (Phase 4), GMod pending.*
2. `CreateProcessAsUser` + dedicated account + ConPTY + `JOB_LIST` from a service in session 0 (window station/desktop, `LoadUserProfile`). Run in Windows Sandbox or a VM, never on a developer's main machine. *Done for a console process ([spike 0002](../spikes/0002-isolated-launch.md)); a real game as a dedicated account is checked in Phase 3.*
3. SteamCMD: bootstrap, progress format, exit codes, executable signature. *Done (spike 0001).*
4. Real readiness patterns for CS2/CS:S/GMod. *CS:S done (spike 0001).*

**Done when:** CI is green on both OSes; spike reports confirm or correct docs 04 and 06 and ADR-0006/0007.

## Phase 1 — Panel core · *done*

- PostgreSQL connection with a clear failure, migrations, health checks (`/health/live`, `/health/ready`).
- Identity, login/logout, initial setup with a token, cookies, antiforgery, rate limiting.
- Permission catalog, roles, assignments, `IPermissionEvaluator`, per-endpoint metadata + coverage test.
- Audit infrastructure.
- OpenAPI + generated TypeScript client.
- Frontend: router, shell, login/setup, user and role management.
- ProblemDetails conventions, `/api/v1` versioning.

**Done when:** an admin installs the panel, connects PostgreSQL, creates their user, creates another user with a limited role, and the actions are audited. Authorization matrix tests.

## Phase 2 — Nodes and protocol · *done*

- `Pulsar.Contracts` v1, `AgentHub`, enrollment, challenge/token, signed commands, anti-replay, revocation, rotation. *Done, including node key rotation (every 90 days or on request) and panel key rotation (doc 03, "Rotation").*
- Agent: Windows service host, `enroll`/`status`/`run` CLI, connection with reconnect, `Hello`/inventory, node metrics, file logging. *Done, plus `unenroll`, `service` and session hand-over before expiry (doc 03, "Agent implementation").*
- UI: node creation with the enrollment command, node list and node page (overview/system). *Done.*

**Done when:** an agent pairs, appears online with its inventory, survives network cuts, and revoking it disconnects it immediately. Tests: invalid signature, replay, revoked node, reused/expired token. *Met in development mode: `e2e/agent.spec.ts` drives the real `PulsarAgent.exe` from the UI (Windows CI leg), `RealAgentTests` cover reconnection, renewal and revocation, and the verifier tests cover signatures and replay. The elevated install is checked on a clean Windows by `scripts/sandbox/agent-install` (Windows Sandbox, panel and PostgreSQL inside it): refusal to install the service from a user-writable folder, machine key with its DACL, LocalSystem service with restart on failure, online, signed ping, service restart, revocation and `unenroll` all pass. The panel runs over HTTPS there (a certificate trusted machine-wide on the disposable machine), and the run also rotates the machine key as SYSTEM and fails on unexpected errors in the panel or agent logs. The same script runs in CI ("Agent install" job, windows-latest). Not covered yet: a TPM-backed **machine** key, because neither Windows Sandbox nor the GitHub runner has a TPM (TPM keys are covered in the user's store by `Pulsar.Agent.Tests`); it is checked in Phase 9 on a VM with a virtual TPM.*

## Phase 3 — Windows process management · *done*

- `IGameProcessManager` + `WindowsGameProcessManager` (Job, ConPTY/pipes, per-server accounts, ACL, desktop). *Done (doc 06, "Implementation (Phase 3)"): account passwords are set right before each logon instead of being stored.*
- `ServerSupervisor` with a mailbox, state machine, stepwise stop, crash + backoff. *Done, with the agent's `ServerHost` (stored specs, operations with an outbox) and the panel side: servers, templates, operations, hub events, reconciliation and the UI (doc 05, "Implementation (Phase 3)").*
- `Pulsar.TestGameServer` and the Windows integration suite. *Done (`Pulsar.Agent.Windows.Tests`; the account tests run as SYSTEM in CI).*
- `_test` manifest (no SteamCMD) to exercise the whole lifecycle. *Done: `games/_test` (key `pulsar-test`) with the runtime subset of `Pulsar.Manifests` (doc 04, "Implementation status").*

**Done when:** with the test game, a server is created, started, stopped, restarted and killed from the panel; no orphans in any test scenario; the process runs as its own account. *Met: `e2e/servers.spec.ts` drives the real `PulsarAgent.exe` and the test game server from the UI (create, start, restart, stop, kill, delete; the real processes come and go; Windows CI leg); `Pulsar.Api.Tests/Servers` covers the lifecycle, crashes, a game's own quit, resuming with the agent, reconciliation and a result that arrives before its ack, with the real agent core; the production install check (Windows Sandbox and CI) runs the test game server as its own account from the SYSTEM service (batch logon only, private folder, process owner, resume after a service restart, kill, deletion removes the account); killing the agent killed its servers (no orphan); and a real Counter-Strike: Source server ran as its account from SYSTEM (`scripts/sandbox/real-game`). Not covered yet: CS2 and GMod (with their manifests, Phase 4).*

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
- Agent install check (`scripts/sandbox/agent-install`) on a Hyper-V VM with a virtual TPM: the node key must be a TPM machine key, and it must survive a rotation.
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
| Application/API | xUnit + `WebApplicationFactory` + real PostgreSQL (`PULSAR_TEST_POSTGRES`, a fresh database per test class; CI starts the PostgreSQL preinstalled on the runners) | Authorization (endpoint × role matrix), IDOR, flows, migrations |
| Protocol | xUnit | Signature, replay, expiry, versioning, revoked session |
| Agent Core | xUnit | Supervisor, mailbox, SteamCMD parser, path validator |
| Agent Windows | xUnit (Windows runner) | Jobs, accounts, ACL, ConPTY, junctions, process cleanup |
| Frontend | Vitest + Testing Library | Components, realtime hooks, permissions in the UI |
| E2E | Playwright | Complete v1 flow |

All .NET tests run on Microsoft.Testing.Platform (xUnit v3). Commits follow Conventional Commits, one coherent change per commit, small pull requests.
