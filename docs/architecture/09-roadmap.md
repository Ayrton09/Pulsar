# 09 — Roadmap técnico

> Estado: **propuesta para revisión** · Cubre el punto 15.
> Cada fase termina con build limpio, tests en verde y una demo verificable. No se empieza una fase sin cerrar la anterior.

## Fase 0 — Fundaciones y spikes (de-risking)

**Repositorio:** solución `.slnx`, `Directory.Build.props`, `Directory.Packages.props`, `global.json`, `.editorconfig`, proyectos vacíos con las referencias de doc 01, Vite + React + Tailwind esqueleto, CI (Windows + Linux, `dotnet format --verify-no-changes`, tests, `npm ci`/lint/test/build), CodeQL, Dependabot, archivos open source (README, LICENSE AGPL-3.0, CONTRIBUTING con DCO, SECURITY, CODE_OF_CONDUCT), plantillas de issues/PR, ADRs aprobados.

**Spikes** (código desechable en `spikes/`, conclusiones escritas en `docs/spikes/`):

1. ConPTY vs pipes con `cs2.exe`, `srcds.exe` (CS:S, GMod) y `steamcmd.exe`: captura, colores, entrada, Ctrl+C, parada.
2. `CreateProcessAsUser` + cuenta dedicada + ConPTY + `JOB_LIST` desde un servicio en sesión 0 (window station/desktop, `LoadUserProfile`).
3. SteamCMD: bootstrap, formato de progreso, exit codes, firma del ejecutable.
4. Patrones de readiness reales de CS2/CS:S/GMod.

**Hecho cuando:** CI verde en ambos SO; informe de spikes que confirme o corrija docs 04 y 06.

## Fase 1 — Núcleo del panel

- Conexión a PostgreSQL con fallo claro, migraciones, health checks (`/health/live`, `/health/ready`).
- Identity, login/logout, setup inicial con token, cookies, antiforgery, rate limiting.
- Catálogo de permisos, roles, asignaciones, `IPermissionEvaluator`, metadata por endpoint + test de cobertura.
- Infraestructura de auditoría.
- OpenAPI + generación del cliente TS.
- Frontend: shell, login/setup, i18n, gestión de usuarios y roles.
- Serilog, ProblemDetails, versionado `/api/v1`, publicación de la SPA desde ASP.NET Core.

**Hecho cuando:** un admin instala el panel, conecta PostgreSQL, crea su usuario, crea otro usuario con un rol limitado y las acciones quedan auditadas. Tests de matriz de autorización.

## Fase 2 — Nodos y protocolo

- `Pulsar.Contracts` v1, `AgentHub`, enrolamiento, challenge/token, comandos firmados, anti-replay, revocación, rotación.
- Agent: host Windows Service, `enroll`/`status`/`run` CLI, conexión con reconexión, `Hello`/inventario, métricas de nodo.
- UI: alta de nodo con comando de enrolamiento, lista y página de nodo (overview/system).

**Hecho cuando:** un agent se empareja, aparece online con su inventario, sobrevive a cortes de red, y revocarlo lo desconecta al instante. Tests: firma inválida, replay, nodo revocado, token reutilizado/expirado.

## Fase 3 — Gestión de procesos en Windows

- `IGameProcessManager` + `WindowsGameProcessManager` (Job, ConPTY/pipes, cuentas por servidor, ACL, desktop).
- `ServerSupervisor` con mailbox, máquina de estados, parada por pasos, crash + backoff.
- `Pulsar.TestGameServer` y suite de integración Windows.
- Manifest `_test` (sin SteamCMD) para ejercitar todo el ciclo.

**Hecho cuando:** con el juego de test se crea, arranca, para, reinicia y mata un servidor desde el panel; no quedan huérfanos en ningún escenario de test; el proceso corre con su propia cuenta.

## Fase 4 — Manifests, SteamCMD y creación de servidores

- `Pulsar.Manifests` (parser, schema, validador, renderer, encoders), JSON Schema publicado.
- Plantillas empaquetadas e importación; manifest CS2.
- Allocations de puertos, firewall opcional.
- `SteamCmdService` (cola, progreso, reintentos, cancelación); operaciones install/update.
- UI: wizard de creación, pestañas Startup y Network, progreso de instalación en vivo.

**Hecho cuando:** se crea un servidor CS2 que instala por SteamCMD con progreso en vivo y arranca con mapa/slots/puerto/argumentos configurados.

## Fase 5 — Consola y métricas en tiempo real

- Ring buffer, logs rotados, suscripciones, backlog por secuencia, entrada de comandos auditada.
- Métricas por Job, `PanelHub`, caché en memoria.
- UI: consola xterm, cabecera de servidor con CPU/RAM/uptime, dashboard v1.

**Hecho cuando:** consola de CS2 en vivo con colores, comandos, reconexión sin huecos; CPU/RAM/uptime actualizándose sin polling.

## Fase 6 — File manager

- Operaciones de archivo en el agent con las 4 capas de defensa; transferencias por ticket.
- UI: navegador de archivos, editor de texto, subida/descarga.

**Hecho cuando:** suite de path traversal (léxica + junctions/symlinks/hard links reales) en verde y edición de `server.cfg` funcionando.

## Fase 7 — Backups

- Creación con globs del manifest, zip en streaming, progreso, cancelación, restauración anti zip-slip, borrado, descarga, retención por servidor.

**Hecho cuando:** crear, descargar y restaurar un backup de CS2 funciona, con duración/tamaño registrados y sin bloquear el agent.

## Fase 8 — Scheduler

- `BackgroundService` en el panel: tareas con `next_run_at`, selección con `FOR UPDATE SKIP LOCKED`, espera hasta la siguiente ejecución (sin polling agresivo; se despierta al crear/editar tareas), cálculo con **Cronos** respetando zona horaria y DST.
- Acciones tipadas: start, stop, restart, backup, update, console_command. Historial de ejecuciones.

**Hecho cuando:** un reinicio diario programado se ejecuta a la hora local correcta y queda auditado; tareas perdidas mientras el panel estaba apagado se marcan `skipped` (no se ejecutan en ráfaga).

## Fase 9 — Endurecimiento y release v1.0

- Revisión de seguridad completa contra doc 08, 2FA exigible, cabeceras, límites.
- E2E Playwright del flujo v1 completo (con el juego `_test` en CI; CS2 real en un job manual/nocturno por su tamaño).
- Manifests CS:S y GMod.
- Empaquetado: panel y agent como servicios Windows (`install-service`), guía de instalación, actualización y backup del panel; instalador "todo en uno" para un solo PC (panel + agent emparejados automáticamente).
- Documentación de contribución de manifests.

**Hecho cuando:** el flujo completo del objetivo v1 funciona en una instalación limpia de Windows Server 2022 y Windows 11 siguiendo solo la documentación.

## Post-v1 (en orden tentativo)

SCP: Secret Laboratory → Minecraft (runtimes gestionados, paso `download`) → bases MySQL/MariaDB para juegos → histórico de métricas agregado → query A2S → backups remotos (S3-compatible) → límites de recursos por Job → shim para sobrevivir reinicios del agent → API keys de usuario → login Steam no anónimo → modo mTLS opcional → backups con VSS → agent Linux.

## Estrategia de testing transversal

| Capa | Herramienta | Foco |
|---|---|---|
| Dominio | xUnit | Máquinas de estado, evaluación de permisos, reglas anti-escalada |
| Manifests | xUnit | Schema, validación, renderizado, casos de inyección, todos los `games/*` |
| Aplicación/API | xUnit + `WebApplicationFactory` + PostgreSQL real (Testcontainers en Linux; cadena de conexión por variable de entorno en Windows) | Autorización (matriz endpoint × rol), IDOR, flujos, migraciones |
| Protocolo | xUnit | Firma, replay, expiración, versionado, sesión revocada |
| Agent Core | xUnit | Supervisor, mailbox, SteamCMD parser, validador de rutas |
| Agent Windows | xUnit (runner Windows) | Jobs, cuentas, ACL, ConPTY, junctions, limpieza de procesos |
| Frontend | Vitest + Testing Library | Componentes, hooks de realtime, permisos en UI |
| E2E | Playwright | Flujo v1 completo |

Convención de commits: Conventional Commits (`feat(agent): …`, `fix(files): …`), un cambio coherente por commit, PRs pequeños.
