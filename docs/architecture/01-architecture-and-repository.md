# 01 — Architecture, repository and dependencies

> Status: **proposal under review**. The repository skeleton described here exists since Phase 0.

## 1. Proposed architecture

Pulsar consists of **two executables** and **one database**:

| Component | What it is | Where it runs |
|---|---|---|
| **Pulsar Panel** | ASP.NET Core modular monolith: REST API, SignalR hubs, compiled SPA, scheduler, audit. | Windows (as a service) or Linux. Single instance in v1. |
| **PostgreSQL** | Internal panel database (and only the panel's). | Wherever the administrator chooses. No fallback. |
| **Pulsar Agent** | Windows service on every machine that runs game servers. | Windows 10 (1809+)/11, Windows Server 2019/2022/2025. |

Structural principles:

1. **The panel manages agents; agents manage game servers.** The panel never creates game processes or touches a node's filesystem.
2. **The agent opens the connection** (outbound WebSocket over TLS) to the panel. Nodes open no inbound ports for Pulsar (only game ports).
3. **Everything the panel asks an agent to do is a typed, signed operation** (`StartServerCommand`, `CreateBackupCommand`…). There is no "run a shell" command.
4. **The agent is the source of truth for actual state** (process alive, PID, CPU); **the panel is the source of truth for desired state** (configuration, should be running, permissions).
5. **Event-driven real time**: the agent pushes changes; the panel forwards them to subscribed browsers through SignalR. No HTTP polling for state.
6. **Games are data** (manifests), not core code.

### Modules of the monolith (panel)

Modules are folders with clear boundaries inside the layers, not separate projects or services:

| Module | Responsibility |
|---|---|
| `Access` | Users, authentication, roles, permissions, node/server access. |
| `Nodes` | Node registration, enrollment, credentials, inventory, port allocations. |
| `Templates` | Import, validation and versioning of game manifests. |
| `Servers` | Lifecycle, variables, assigned ports, startup configuration. |
| `Operations` | Tracking of long-running operations (install, update, backup, restore). |
| `Files` | Authorized facade over the agent's file manager. |
| `Backups` | Backup metadata and orchestration. |
| `Scheduling` | Typed scheduled tasks (cron + time zone). |
| `Audit` | Append-only log of relevant actions. |
| `Realtime` | SignalR hubs (browser and agent), subscriptions, live metrics in memory. |
| `Settings` | System configuration editable from the UI. |

## 2. Repository layout

```
pulsar/
├─ .github/
│  ├─ workflows/            ci.yml (.NET on Windows+Linux, web, e2e), codeql.yml
│  ├─ ISSUE_TEMPLATE/
│  └─ dependabot.yml
├─ docs/
│  ├─ architecture/         this design
│  ├─ adr/                  Architecture Decision Records
│  ├─ spikes/               findings of Phase 0 prototypes
│  └─ guides/               (later) panel/agent installation, manifests, development
├─ games/                   official manifests (MIT)
│  ├─ cs2/  css/  gmod/
│  └─ _test/                fake game for end-to-end tests (uses Pulsar.TestGameServer)
├─ schemas/                 manifest.v1.schema.json (MIT)
├─ spikes/                  throwaway Phase 0 prototypes, not part of the solution
├─ src/
│  ├─ Pulsar.Domain/
│  ├─ Pulsar.Application/
│  ├─ Pulsar.Infrastructure/
│  ├─ Pulsar.Api/               panel host (composition root); serves the SPA from wwwroot
│  ├─ Pulsar.Contracts/         panel ↔ agent protocol (shared)
│  ├─ Pulsar.Manifests/         manifest model, parser, validator and renderer (shared)
│  ├─ Pulsar.Agent/             agent host (Windows service, composition root)
│  ├─ Pulsar.Agent.Core/        platform-independent agent logic
│  ├─ Pulsar.Agent.Windows/     Win32 implementation (Job Objects, ConPTY, accounts, ACLs, firewall)
│  └─ Pulsar.Web/               React + TypeScript + Vite SPA (+ e2e/ with Playwright)
├─ tests/
│  ├─ Pulsar.Architecture.Tests/    project dependency rules (exists)
│  ├─ Pulsar.Api.Tests/             WebApplicationFactory integration tests (exists)
│  ├─ Pulsar.Domain.Tests/          (created when it has content)
│  ├─ Pulsar.Application.Tests/
│  ├─ Pulsar.Manifests.Tests/
│  ├─ Pulsar.Agent.Core.Tests/
│  ├─ Pulsar.Agent.Windows.Tests/   Windows integration with real processes
│  └─ Pulsar.TestGameServer/        "fake game" executable for process tests
├─ Directory.Build.props        Nullable, TreatWarningsAsErrors, AnalysisLevel, lock files…
├─ Directory.Packages.props     Central Package Management
├─ global.json                  SDK 10.0.x + Microsoft.Testing.Platform as test runner
├─ Pulsar.slnx
├─ .editorconfig · .gitattributes · .gitignore
└─ README.md · LICENSE · CONTRIBUTING.md · SECURITY.md · CODE_OF_CONDUCT.md
```

Test projects are created in the phase that gives them content, so the solution never carries empty projects.

### Why this split

- **Separate `Pulsar.Manifests`**: panel and agent must validate and render manifests with *exactly* the same code (the agent re-validates as defense in depth). It cannot live in `Application` (the agent must not depend on the panel) or in `Contracts` (it is not protocol).
- **Agent in three projects** (`Agent`, `Agent.Core`, `Agent.Windows`): all Win32 interop is isolated in a `net10.0-windows` project. `Agent.Core` (net10.0) holds supervision, SteamCMD, backups and validation, testable without Windows. A future `Pulsar.Agent.Linux` can be added without touching Windows code. It is the minimum abstraction behind `IGameProcessManager`/`WindowsGameProcessManager`.
- **Several test projects**: Windows tests with real processes run only on Windows runners and PostgreSQL integration tests only where PostgreSQL is available; one project would need fragile category filters.

`Pulsar.Contracts` is **only for the panel ↔ agent protocol**. Panel REST DTOs live in `Pulsar.Api` (endpoints) and `Pulsar.Application` (use case models) and are not shared with the agent, so changing the web API never breaks the node protocol.

## 3. Component diagram

```
 ┌──────────── Browser ──────────────┐
 │ React SPA (served by the panel)   │
 │  · REST  /api/v1/*   (cookie)     │
 │  · WSS   /hubs/panel (SignalR)    │
 └───────────────┬───────────────────┘
                 │ HTTPS
 ┌───────────────▼──────────────────── Pulsar Panel (one process) ──────────────────────────┐
 │ Pulsar.Api                                                                               │
 │  ├─ Static assets (MapStaticAssets) + fallback to index.html                             │
 │  ├─ Minimal API endpoints /api/v1  ──► permission + resource authorization               │
 │  ├─ PanelHub   /hubs/panel         (browsers, cookie)                                    │
 │  ├─ AgentHub   /agent/v1/hub       (agents, short JWT obtained via signed challenge)     │
 │  ├─ Agent auth /agent/v1/enroll · /auth/challenge · /auth/token · /transfers/{ticket}    │
 │  └─ AgentGateway (implements IAgentGateway: signs and sends commands)                    │
 │ Pulsar.Application  use cases, validation, orchestration, ports (interfaces)             │
 │ Pulsar.Domain       entities, rules, state machines, permission catalog                  │
 │ Pulsar.Infrastructure  EF Core/Npgsql, Identity, Data Protection, Serilog, scheduler     │
 │ Background services: Scheduler · OperationWatchdog · AuditRetention                      │
 └───────────────┬───────────────────────────────────────────────────▲──────────────────────┘
                 │ TCP (Npgsql)                                      │ outbound WSS opened by the agent
          ┌──────▼──────┐                                            │ (+ HTTPS for file transfers)
          │ PostgreSQL  │                                            │
          └─────────────┘           ┌────────────────────────────────┴───────── Windows node ──┐
                                    │ Pulsar Agent (Windows service, LocalSystem)               │
                                    │  ├─ PanelConnection (HubConnection + reconnect)           │
                                    │  ├─ CommandDispatcher (signature, anti-replay, validation)│
                                    │  ├─ ServerSupervisor (one mailbox per server)             │
                                    │  ├─ IGameProcessManager → WindowsGameProcessManager       │
                                    │  ├─ SteamCmdService (queue) · FileService · BackupService │
                                    │  ├─ ConsoleBroker (ring buffers + logs on disk)           │
                                    │  ├─ MetricsSampler (every 5 s, via Job accounting)        │
                                    │  └─ LocalStateStore (%ProgramData%\Pulsar\Agent)          │
                                    │            │ CreateProcessAsUser (per-server account)     │
                                    │   ┌────────▼───────┐  ┌────────────────┐                  │
                                    │   │ Job "srv A"    │  │ Job "srv B"    │   …              │
                                    │   │ cs2.exe (+kids)│  │ srcds.exe      │                  │
                                    │   │ user pulsar_a  │  │ user pulsar_b  │                  │
                                    │   └────────────────┘  └────────────────┘                  │
                                    └───────────────────────────────────────────────────────────┘
```

Typical flow ("start server"):

```
UI ─POST /api/v1/servers/{id}/power {start}─► Api ─► StartServerHandler
   (authorizes server.start on that server, validates the transition, audits)
   ─► IAgentGateway.SendAsync(signed StartServerCommand) ─► AgentHub ─► Agent
Agent ─ack(Accepted)─► Panel ─► 202 Accepted to the UI
Agent ─ServerStateChanged(Starting→Running)─► AgentHub ─► persists state ─► PanelHub group server:{id} ─► UI
```

## 4. Project dependencies

```
Pulsar.Domain           → (none)
Pulsar.Manifests        → (none)                     [shared panel/agent]
Pulsar.Contracts        → (none)                     [shared panel/agent]
Pulsar.Application      → Domain, Manifests, Contracts
Pulsar.Infrastructure   → Application, Domain
Pulsar.Api              → Application, Infrastructure, Contracts, Manifests   (composition root)

Pulsar.Agent.Core       → Contracts, Manifests
Pulsar.Agent.Windows    → Agent.Core
Pulsar.Agent            → Agent.Core, Agent.Windows                           (composition root)

Pulsar.Web              → (no .NET references; consumes the OpenAPI document of Pulsar.Api)
```

Rules, enforced by `tests/Pulsar.Architecture.Tests` (which reads the project files, so an unused but forbidden reference is still caught):

- The agent **never** references `Domain`, `Application`, `Infrastructure` or `Api`.
- `Domain`, `Contracts` and `Manifests` reference no EF Core, ASP.NET Core or Npgsql packages.
- Every source project must have explicit rules in the test; adding a project forces a decision about its dependencies.

### Pragmatic decision: `Application` uses EF Core directly

| Option | Pros | Cons |
|---|---|---|
| **A. `Application` defines `IPulsarDbContext` (DbSets + SaveChanges) and references `Microsoft.EntityFrameworkCore` (no provider)** | Expressive LINQ queries, efficient DTO projections, no trivial repositories. | `Application` knows EF Core (not Npgsql). |
| B. One repository per aggregate in `Application`, implemented in `Infrastructure` | Full ORM isolation. | Lots of boilerplate, abstractions that leak `IQueryable` anyway, harder for contributors. |

**Recommendation: A.** The domain stays pure (explicit requirement). ORM independence in `Application` has no real value: we will not switch ORMs and PostgreSQL is the only database.

### Other backend conventions

- **Minimal APIs** grouped per module (`app.MapGroup("/api/v1/servers").MapServerEndpoints()`), OpenAPI via `Microsoft.AspNetCore.OpenApi` (no Swashbuckle) and the built-in Minimal API validation of .NET 10 for DTO shape. Business rules are validated in handlers/domain.
- **No MediatR or AutoMapper** (both moved to commercial licenses in 2025 and add little here): plain handler classes registered in DI and explicit mapping.
- Expected errors as typed results (`Result<T>`/`Error`) mapped to `ProblemDetails`; exceptions only for exceptional cases.
- Strongly typed IDs in `Domain`/`Application` (`readonly record struct ServerId(Guid Value)`) with an EF conversion convention; plain `Guid` in DTOs and the protocol.
- `TimeProvider` injected instead of `DateTime.UtcNow` for deterministic tests.
- Serilog configured from `appsettings`, enriched with a correlation id, never destructuring whole DTOs (secret leakage risk).
- `Directory.Build.props`: `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild=true`, NuGet lock files.
- Tests use xUnit v3 on **Microsoft.Testing.Platform** (enabled in `global.json`; required by the .NET 10 SDK for xUnit v3).
- Unknown routes under `/api`, `/hubs` and `/agent` return a 404 `ProblemDetails` and never fall through to the SPA's `index.html` (covered by `Pulsar.Api.Tests`).
