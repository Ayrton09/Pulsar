# 05 — Ciclo de vida de un game server

> Estado: **propuesta para revisión** · Cubre el punto 10.

## Tres dimensiones de estado (no un único enum)

Mezclar "instalando", "online" y "haciendo backup" en un único enum produce combinaciones imposibles de mantener. Pulsar separa:

| Dimensión | Valores | Dueño | Persistencia |
|---|---|---|---|
| `InstallState` | `NotInstalled`, `Installing`, `Installed`, `Failed` | Panel (por resultados de operaciones) | `servers.install_state` |
| `RuntimeState` | `Offline`, `Starting`, `Running`, `Stopping`, `Crashed` | **Agent** (observado) | `servers.runtime_state` (último observado) |
| `DesiredState` | `Stopped`, `Running` | **Panel** (intención del usuario/scheduler) | `servers.desired_state` |
| Operación activa | `ActiveOperationId` → `Install/Update/Reinstall/Backup/Restore/Delete` | Panel | `servers.active_operation_id` |
| Superposiciones | `IsSuspended`, nodo desconectado | Panel | columna / memoria |

La UI deriva un **estado visible** único: `Instalando 42 %`, `Actualizando`, `Restaurando backup`, `Iniciando`, `Online`, `Deteniendo`, `Offline`, `Crashed`, `Suspendido`, `Nodo desconectado (último estado: Online)`.

## Máquina de estados runtime (en el Agent)

```
                 Start                         readiness OK
   ┌─────────┐ ───────────► ┌──────────┐ ─────────────────► ┌─────────┐
   │ Offline │              │ Starting │                    │ Running │
   └─────────┘ ◄─────────── └──────────┘                    └─────────┘
      ▲   ▲   exit durante      │  exit inesperado              │   │
      │   │   Stopping          ▼                               │   │ exit inesperado
      │   │              ┌──────────┐ ◄─────────────────────────┘   │
      │   │              │ Crashed  │ ◄─────────────────────────────┘
      │   │              └──────────┘
      │   │                   │ auto-restart (backoff) → Starting
      │   │   Stop/Kill       │ Start manual → Starting
      │   └──────────── ┌──────────┐ ◄── Stop / Kill / Restart (desde Starting o Running)
      └──────────────── │ Stopping │
         exit / kill    └──────────┘
```

Transiciones:

| Desde | Evento | Hacia | Notas |
|---|---|---|---|
| Offline/Crashed | `Start` | Starting | Requiere `Installed`, no suspendido, sin operación exclusiva, puertos libres. |
| Starting | readiness OK | Running | Por `logPattern`/`processAlive`. |
| Starting | timeout de readiness | Running + alerta | Sigue vivo: no se mata por no detectar el patrón; se emite alerta. |
| Starting/Running | `Stop` | Stopping | Ejecuta pasos de parada del manifest. |
| Starting/Running | `Kill` | Stopping | `TerminateJobObject` inmediato. |
| Stopping | proceso principal termina | Offline | Si quedan procesos en el Job tras 5 s → se termina el Job. |
| Stopping | timeout de parada | Offline | Kill del Job; se registra `Reason = StopTimeoutKilled`. |
| Starting/Running | salida inesperada | Crashed | Se guarda exit code. |
| Crashed | política de auto-restart | Starting | Solo si `DesiredState = Running` y `restart_on_crash`. |

`Restart` = `Stop` + `Start` como **una** unidad en el mailbox del servidor (nada puede intercalarse entre medias).

### Política de crash

- Backoff: 5 s, 15 s, 60 s; máximo 3 reinicios en 10 min. Superado el límite, se queda en `Crashed` con alerta ("crash loop") y `DesiredState` pasa a `Stopped`.
- Exit code 0 durante `Running` sin orden de parada (p. ej. alguien ejecutó `quit` por consola): se trata como parada solicitada por el juego → `Offline` y `DesiredState = Stopped`, sin reinicio. (Configurable por manifest si algún juego usa exit 0 para "reiniciar".)

## Concurrencia: un mailbox por servidor

En el Agent, cada servidor tiene un `ServerSupervisor` con un `Channel<ServerMessage>` de **un solo lector**. Comandos (`Start`, `Stop`, `Install`, `Backup`…) y eventos internos (proceso terminó, readiness, timeout) se procesan **en orden y de uno en uno**. Elimina locks y condiciones de carrera ("Stop llega mientras Start está creando el proceso"). Los mensajes urgentes (`Kill`, `CancelOperation`) cancelan el `CancellationToken` del trabajo en curso antes de encolarse.

En el Panel, la exclusividad se garantiza con concurrencia optimista (`xmin`) sobre `servers` al fijar `active_operation_id`.

Matriz de compatibilidad (operación activa vs acción pedida):

| Activa \ Pedida | Start | Stop/Kill | Install/Update | Backup | Restore | Editar archivos |
|---|---|---|---|---|---|---|
| (ninguna) | ✓ | ✓ | requiere Offline | ✓ (aviso si Running) | requiere Offline | ✓ |
| Install/Update | ✗ | Kill cancela la operación | ✗ | ✗ | ✗ | ✗ |
| Backup | ✓ | ✓ | ✗ | ✗ | ✗ | ✓ |
| Restore | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ |

## Flujo completo de creación (v1)

```
1. POST /api/v1/servers {nodeId, templateId, name, variables, ports?}
   - autoriza server.create sobre el nodo; valida variables contra el manifest
   - asigna allocations (las pedidas o las primeras libres del nodo)
   - genera short_id; crea Server(NotInstalled, Offline, Stopped); audita server.created
2. Operation server_provision → ProvisionServerCommand
   Agent: crea <ServersRoot>\<shortId>, cuenta Windows dedicada, ACLs, reglas de firewall (si está habilitado)
3. Operation server_install → InstallServerCommand
   Agent: cola SteamCMD → progreso por OperationProgress → OperationCompleted
   Panel: install_state = Installed | Failed
4. Usuario ajusta mapa/slots/puertos/argumentos → ApplyServerSpecCommand
5. Start → … → Running
```

## Reconciliación (la clave de la estabilidad)

Al conectar, el agent envía `AgentHello` con el estado real de cada servidor. El panel:

1. Actualiza `runtime_state` de cada servidor con lo observado (el agent manda).
2. Servidores que el panel conoce y el agent no → alerta "servidor desconocido en el nodo" (no se recrea nada automáticamente).
3. Servidores que el agent conoce y el panel no (borrados mientras el nodo estaba offline) → el panel envía `DeleteServerCommand` solo si hay registro de auditoría del borrado; si no, alerta.
4. Operaciones `running` en BD que el agent no reporta en curso ni en su outbox → `interrupted`.
5. Envía `DesiredState` de cada servidor; el agent **no** arranca servidores automáticamente por reconciliación salvo `auto_start` tras boot (evita que reconectar arranque servidores inesperadamente).

Casos:

| Situación | Comportamiento |
|---|---|
| Panel cae | Los servidores siguen. Auto-restart por crash funciona (local). Scheduler en pausa. |
| Agent se reinicia / se actualiza | Sus servidores **se detienen** (Job con KILL_ON_JOB_CLOSE, ver doc 06). Al arrancar, inicia los que tengan `auto_start` o estuvieran `Running` antes de una parada controlada del servicio. |
| Reinicio de Windows | Igual que el anterior: servicio en arranque automático + `auto_start`. |
| Red intermitente | Reconexión con backoff; la consola se rellena desde el ring buffer por `Sequence`. |

## Borrado

`DeleteServerCommand` (operación): detiene (kill si hace falta), elimina reglas de firewall, borra directorio **sin seguir reparse points**, borra backups si se pidió, elimina la cuenta Windows y su perfil. El panel borra la fila al recibir `succeeded` (la auditoría permanece) y libera allocations.
