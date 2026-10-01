# 01 — Arquitectura, repositorio y dependencias

> Estado: **propuesta para revisión** · Cubre los puntos 1–4 del encargo.

## 1. Arquitectura propuesta

Pulsar se compone de **dos ejecutables** y **una base de datos**:

| Componente | Qué es | Dónde corre |
|---|---|---|
| **Pulsar Panel** | Monolito modular ASP.NET Core: API REST, hubs SignalR, SPA compilada, scheduler, auditoría. | Windows (servicio) o Linux. Una sola instancia en v1. |
| **PostgreSQL** | Base de datos interna del panel (y solo del panel). | Donde el administrador decida. Sin fallback. |
| **Pulsar Agent** | Servicio Windows en cada máquina que ejecuta game servers. | Windows 10 (1809+)/11, Windows Server 2019/2022/2025. |

Principios estructurales:

1. **El Panel administra Agents; los Agents administran game servers.** El panel nunca crea procesos de juegos ni toca el filesystem de un nodo.
2. **El Agent inicia la conexión** (WebSocket saliente, TLS) hacia el panel. Los nodos no abren puertos de entrada para Pulsar (solo los puertos de juego).
3. **Todo lo que el panel le pide a un Agent es una operación tipada y firmada** (`StartServerCommand`, `CreateBackupCommand`…). No existe un comando "ejecutar shell".
4. **El Agent es la fuente de verdad del estado real** (proceso vivo, PID, CPU); **el Panel es la fuente de verdad del estado deseado** (configuración, debe estar encendido, permisos).
5. **Tiempo real por eventos**: el Agent empuja cambios; el panel los reenvía a los navegadores suscritos vía SignalR. Sin polling HTTP de estado.
6. **Los juegos son datos** (manifests), no código del core.

### Módulos del monolito (Panel)

Los módulos son carpetas con límites claros dentro de las capas, no proyectos ni servicios separados:

| Módulo | Responsabilidad |
|---|---|
| `Access` | Usuarios, autenticación, roles, permisos, accesos por nodo/servidor. |
| `Nodes` | Alta de nodos, enrolamiento, credenciales, inventario, puertos (allocations). |
| `Templates` | Importación, validación y versionado de game manifests. |
| `Servers` | Ciclo de vida, variables, puertos asignados, configuración de arranque. |
| `Operations` | Seguimiento de operaciones largas (instalar, actualizar, backup, restore). |
| `Files` | Fachada autorizada hacia el file manager del Agent. |
| `Backups` | Metadatos y orquestación de backups. |
| `Scheduling` | Tareas programadas tipadas (cron + zona horaria). |
| `Audit` | Registro append-only de acciones relevantes. |
| `Realtime` | Hubs SignalR (navegador y agent), suscripciones, métricas en vivo en memoria. |
| `Settings` | Configuración del sistema editable desde la UI. |

## 2. Estructura del repositorio

```
pulsar/
├─ .github/
│  ├─ workflows/            ci.yml (build+test win/linux), codeql.yml, release.yml
│  ├─ ISSUE_TEMPLATE/
│  └─ dependabot.yml
├─ docs/
│  ├─ architecture/         este diseño
│  ├─ adr/                  Architecture Decision Records
│  ├─ guides/               instalación panel, instalación agent, manifests, desarrollo
│  └─ security/             modelo de amenazas detallado
├─ games/                   manifests oficiales (licencia MIT, ver ADR/preguntas abiertas)
│  ├─ cs2/manifest.json
│  ├─ css/manifest.json
│  ├─ gmod/manifest.json
│  └─ _test/manifest.json   juego falso para tests E2E (usa Pulsar.TestGameServer)
├─ schemas/
│  └─ manifest.v1.schema.json   JSON Schema público del formato de manifest
├─ src/
│  ├─ Pulsar.Domain/
│  ├─ Pulsar.Application/
│  ├─ Pulsar.Infrastructure/
│  ├─ Pulsar.Api/               host del Panel (composition root)
│  ├─ Pulsar.Contracts/         protocolo Panel ↔ Agent (compartido)
│  ├─ Pulsar.Manifests/         modelo, parser, validador y renderer de manifests (compartido)
│  ├─ Pulsar.Agent/             host del Agent (Windows Service, composition root)
│  ├─ Pulsar.Agent.Core/        lógica del Agent independiente de plataforma
│  ├─ Pulsar.Agent.Windows/     implementación Win32 (Job Objects, ConPTY, cuentas, ACL, firewall)
│  └─ Pulsar.Web/               SPA React + TypeScript + Vite (+ e2e/ con Playwright)
├─ tests/
│  ├─ Pulsar.Domain.Tests/
│  ├─ Pulsar.Application.Tests/
│  ├─ Pulsar.Manifests.Tests/
│  ├─ Pulsar.Api.Tests/             integración: WebApplicationFactory + PostgreSQL real
│  ├─ Pulsar.Agent.Core.Tests/
│  ├─ Pulsar.Agent.Windows.Tests/   integración Windows con procesos reales
│  └─ Pulsar.TestGameServer/        ejecutable "juego falso" para tests de procesos
├─ Directory.Build.props        Nullable, TreatWarningsAsErrors, AnalysisLevel, etc.
├─ Directory.Packages.props     Central Package Management (versiones en un único lugar)
├─ global.json                  SDK 10.0.x
├─ Pulsar.slnx
├─ .editorconfig
├─ README.md · LICENSE (AGPL-3.0) · CONTRIBUTING.md · SECURITY.md · CODE_OF_CONDUCT.md
```

### Por qué esta división (y no la lista original tal cual)

La lista propuesta (`Api, Application, Domain, Infrastructure, Contracts, Agent, Tests`) se mantiene con tres ajustes justificados:

- **`Pulsar.Manifests` separado**: el panel y el agent deben validar y renderizar manifests con *exactamente* el mismo código (el agent revalida por defensa en profundidad). No puede vivir en `Application` (el agent no debe depender del panel) ni en `Contracts` (no es protocolo).
- **Agent en tres proyectos** (`Agent`, `Agent.Core`, `Agent.Windows`): toda la interop Win32 queda aislada en un proyecto `net10.0-windows`. `Agent.Core` (net10.0) contiene supervisión, SteamCMD, backups y validación, testeable sin Windows. Un futuro `Pulsar.Agent.Linux` se añade sin tocar el código Windows. Es la abstracción mínima para cumplir el requisito `IGameProcessManager`/`WindowsGameProcessManager`.
- **Varios proyectos de test** en vez de un `Pulsar.Tests`: los tests Windows con procesos reales deben poder ejecutarse solo en runners Windows, y los de integración con PostgreSQL solo donde haya PostgreSQL. Un solo proyecto obligaría a filtrar por categorías frágiles.

`Pulsar.Contracts` queda **exclusivamente para el protocolo Panel ↔ Agent**. Los DTO REST del panel viven en `Pulsar.Api` (endpoints) y `Pulsar.Application` (modelos de caso de uso); no se comparten con el Agent para que cambiar la API web nunca rompa el protocolo de nodos.

## 3. Diagrama de componentes

```
 ┌──────────── Navegador ────────────┐
 │ SPA React (servida por el Panel)  │
 │  · REST  /api/v1/*   (cookie)     │
 │  · WSS   /hubs/panel (SignalR)    │
 └───────────────┬───────────────────┘
                 │ HTTPS
 ┌───────────────▼──────────────────── Pulsar Panel (1 proceso) ─────────────────────────────┐
 │ Pulsar.Api                                                                               │
 │  ├─ Static files + fallback a index.html                                                 │
 │  ├─ Minimal API endpoints /api/v1  ──► autorización por permiso + recurso                │
 │  ├─ PanelHub   /hubs/panel         (navegadores, cookie)                                 │
 │  ├─ AgentHub   /agent/v1/hub       (agents, JWT corto obtenido por challenge firmado)     │
 │  ├─ Agent auth /agent/v1/enroll · /auth/challenge · /auth/token · /transfers/{ticket}    │
 │  └─ AgentGateway (impl. de IAgentGateway: firma y envía comandos)                        │
 │ Pulsar.Application  casos de uso, validación, orquestación, puertos (interfaces)         │
 │ Pulsar.Domain       entidades, reglas, máquinas de estado, catálogo de permisos          │
 │ Pulsar.Infrastructure  EF Core/Npgsql, Identity, Data Protection, Serilog, scheduler     │
 │ Background services: Scheduler · OperationWatchdog · AuditRetention                      │
 └───────────────┬───────────────────────────────────────────────────▲──────────────────────┘
                 │ TCP (Npgsql)                                      │ WSS saliente iniciado por el Agent
          ┌──────▼──────┐                                            │ (+ HTTPS para transferencias de archivos)
          │ PostgreSQL  │                                            │
          └─────────────┘           ┌────────────────────────────────┴───────── Nodo Windows ──┐
                                    │ Pulsar Agent (Windows Service, LocalSystem)               │
                                    │  ├─ PanelConnection (HubConnection + reconexión)          │
                                    │  ├─ CommandDispatcher (firma, anti-replay, validación)    │
                                    │  ├─ ServerSupervisor (un mailbox por servidor)            │
                                    │  ├─ IGameProcessManager → WindowsGameProcessManager       │
                                    │  ├─ SteamCmdService (cola) · FileService · BackupService  │
                                    │  ├─ ConsoleBroker (ring buffers + logs en disco)          │
                                    │  ├─ MetricsSampler (cada 5 s, vía Job accounting)         │
                                    │  └─ LocalStateStore (%ProgramData%\Pulsar\Agent)          │
                                    │            │ CreateProcessAsUser (cuenta por servidor)    │
                                    │   ┌────────▼───────┐  ┌────────────────┐                  │
                                    │   │ Job "srv A"    │  │ Job "srv B"    │   …              │
                                    │   │ cs2.exe (+hijos)│ │ srcds.exe      │                  │
                                    │   │ user pulsar_a  │  │ user pulsar_b  │                  │
                                    │   └────────────────┘  └────────────────┘                  │
                                    └───────────────────────────────────────────────────────────┘
```

Flujo típico ("iniciar servidor"):

```
UI ─POST /api/v1/servers/{id}/power {start}─► Api ─► StartServerHandler
   (autoriza server.start sobre ese servidor, valida transición, audita)
   ─► IAgentGateway.SendAsync(StartServerCommand firmado) ─► AgentHub ─► Agent
Agent ─ack(Accepted)─► Panel ─► 202 Accepted a la UI
Agent ─ServerStateChanged(Starting→Running)─► AgentHub ─► persiste estado ─► PanelHub grupo server:{id} ─► UI
```

## 4. Dependencias entre proyectos

```
Pulsar.Domain           → (nada)
Pulsar.Manifests        → (nada)                     [compartido panel/agent]
Pulsar.Contracts        → (nada)                     [compartido panel/agent]
Pulsar.Application      → Domain, Manifests, Contracts
Pulsar.Infrastructure   → Application, Domain
Pulsar.Api              → Application, Infrastructure, Contracts, Manifests   (composition root)

Pulsar.Agent.Core       → Contracts, Manifests
Pulsar.Agent.Windows    → Agent.Core
Pulsar.Agent            → Agent.Core, Agent.Windows                           (composition root)

Pulsar.Web              → (ninguna referencia .NET; consume OpenAPI generado por Pulsar.Api)
```

Reglas (verificadas por las referencias de proyecto, es decir, en compilación):

- El Agent **nunca** referencia `Domain`, `Application`, `Infrastructure` ni `Api`.
- `Domain` no referencia EF Core, ASP.NET Core ni Npgsql.
- `Contracts` y `Manifests` no tienen dependencias de Pulsar y mínimas de terceros (solo BCL + `System.Text.Json`).

### Decisión pragmática: `Application` usa EF Core directamente

| Opción | Pros | Contras |
|---|---|---|
| **A. `Application` define `IPulsarDbContext` (DbSet + SaveChanges) y referencia `Microsoft.EntityFrameworkCore` (sin provider)** | Consultas LINQ expresivas, proyecciones a DTO eficientes, cero repositorios triviales. | `Application` conoce EF Core (no Npgsql). |
| B. Repositorio por agregado en `Application`, implementado en `Infrastructure` | Aislamiento total del ORM. | Mucho boilerplate, abstracciones que filtran IQueryable igualmente, más difícil para contribuidores. |

**Recomendación: A.** El dominio sigue puro (requisito explícito). La independencia del ORM en `Application` no aporta valor real: no vamos a cambiar de ORM, y PostgreSQL es la única base. Esta es la estructura que más contribuidores reconocen.

### Otras convenciones de backend

- **Minimal APIs** agrupadas por módulo (`app.MapGroup("/api/v1/servers").MapServerEndpoints()`), OpenAPI con `Microsoft.AspNetCore.OpenApi` (incluido en ASP.NET Core; sin Swashbuckle) y la validación integrada de Minimal APIs de .NET 10 para la forma de los DTO. Las reglas de negocio se validan en los handlers/dominio.
- **Sin MediatR ni AutoMapper** (ambos pasaron a licencias comerciales en 2025 y no aportan lo suficiente): handlers como clases normales registradas en DI y mapeo explícito.
- Errores esperables como resultados tipados (`Result<T>`/`Error`) traducidos a `ProblemDetails`; excepciones solo para lo excepcional.
- IDs fuertemente tipados en `Domain`/`Application` (`readonly record struct ServerId(Guid Value)`) con conversión EF por convención; `Guid` en DTOs y en el protocolo. Evita intercambiar `nodeId` y `serverId` por error, a un coste único pequeño.
- `TimeProvider` inyectado (no `DateTime.UtcNow` directo) para tests deterministas.
- Serilog con configuración por `appsettings`, enriquecido con `CorrelationId`, sin destructurar DTOs completos (riesgo de filtrar secretos).
- `Directory.Build.props`: `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-recommended`.
