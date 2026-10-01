# 03 — Protocolo Panel ↔ Agent y autenticación de nodos

> Estado: **propuesta para revisión** · Cubre los puntos 7–8. Decisiones: ADR-0002, ADR-0003.

## 7. Diseño del protocolo

### 7.1 Transporte: comparación

| Opción | Pros | Contras |
|---|---|---|
| **SignalR (WebSockets) iniciado por el Agent** | Mismo stack que el navegador; reconexión automática; hubs tipados (`Hub<T>`); *client results* (.NET 7+) para ack; el cliente .NET envía `Authorization` en cabecera (no en query string); WebSockets atraviesa cualquier reverse proxy (IIS/ARR, nginx, Caddy, Cloudflare). | El contrato son nombres de método + DTO (no un IDL); hay que disciplinar el versionado. |
| gRPC bidi streaming | IDL protobuf, evolución de esquema excelente. | Requiere HTTP/2 extremo a extremo: mal soporte detrás de IIS/ARR y proxies comunes en Windows; toolchain protobuf extra. |
| WebSocket crudo con framing propio | Control total. | Reimplementar reconexión, multiplexado, keep-alive, acks. Más superficie de bugs. |

**Recomendación: SignalR sobre WebSockets** (transporte fijado a WebSockets, sin fallback a long polling). El tipado se garantiza en C# con contratos compartidos en `Pulsar.Contracts` y — clave para la seguridad — **los comandos viajan como sobres firmados**, por lo que la integridad no depende del transporte.

Protocolo de hub: JSON (System.Text.Json con source generators). MessagePack solo si las mediciones de consola lo justifican.

### 7.2 Endpoints del panel para Agents

| Endpoint | Uso | Auth |
|---|---|---|
| `GET  /agent/v1/panel-identity` | Clave(s) pública(s) del panel + fingerprint. | Pública |
| `POST /agent/v1/enroll` | Enrolamiento con token de un solo uso. | Token de enrolamiento |
| `POST /agent/v1/auth/challenge` | Pide nonce. | Ninguna (rate limited) |
| `POST /agent/v1/auth/token` | Devuelve JWT de sesión (15 min) tras verificar firma. | Firma del nodo |
| `POST /agent/v1/credentials/rotate` | Rotación de clave del nodo. | JWT + prueba con clave nueva |
| `WSS  /agent/v1/hub` | Canal persistente. | JWT (`aud=pulsar-agent`) |
| `GET/PUT /agent/v1/transfers/{ticket}` | Transferencias grandes (subidas/descargas/backups). | JWT + ticket de un solo uso |

Todos fuera de `/api/v1`, con política de autenticación distinta: **un token de navegador nunca sirve en endpoints de agent ni viceversa**. `HttpConnectionDispatcherOptions.CloseOnAuthenticationExpiration = true` en el hub de agents para forzar reautenticación periódica.

### 7.3 Sobre de comando firmado

El panel tiene una clave de firma ECDSA P-256 (`panel_signing_keys`). Cada comando panel → agent:

```csharp
// Pulsar.Contracts
public sealed record SignedCommand(
    string KeyId,            // clave del panel usada
    byte[] Payload,          // UTF-8 JSON de CommandEnvelope (bytes exactos firmados)
    byte[] Signature);       // ECDSA P-256 / SHA-256 sobre Payload

public sealed record CommandEnvelope(
    int ProtocolVersion,
    Guid CommandId,          // UUIDv7, idempotencia
    Guid NodeId,             // destinatario: el agent rechaza si no es él
    Guid SessionId,          // sesión emitida en /auth/token
    long Sequence,           // monotónico por sesión (anti-replay)
    DateTime IssuedAtUtc,
    DateTime ExpiresAtUtc,   // defensa secundaria; tolera clock skew configurado
    Guid? OperationId,
    Guid? ActorUserId,       // para logs/correlación en el nodo
    AgentCommand Command);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(StartServerCommand), "server.start")]
[JsonDerivedType(typeof(StopServerCommand), "server.stop")]
// …un discriminador estable por comando
public abstract record AgentCommand;
```

Verificación en el Agent (`CommandDispatcher`), en este orden, rechazando ante el primer fallo:

1. `KeyId` es una clave del panel fijada (pinned) en el enrolamiento.
2. Firma válida sobre `Payload` (se verifica **antes** de deserializar el comando).
3. `NodeId` == propio; `SessionId` == sesión actual.
4. `Sequence` no vista y dentro de la ventana anti-replay (ventana deslizante de 1024, como IPsec; tolera desorden menor sin aceptar repeticiones).
5. No expirado (con tolerancia de reloj).
6. `ProtocolVersion` compatible; discriminador conocido (si no: `UnsupportedCommand`).
7. Validación semántica del comando (IDs conocidos, paths, variables contra el manifest, tamaños).

Así, aunque alguien comprometa TLS o un reverse proxy, **no puede inyectar ni reproducir comandos**. Firmar con ECDSA P-256 cuesta microsegundos; el volumen de comandos es bajo.

Los eventos agent → panel no se firman individualmente: viajan por una sesión autenticada con prueba de posesión de la clave del nodo y su impacto es informativo (estado, consola, métricas). El panel valida igualmente que el `ServerId` de cada evento pertenezca a ese nodo.

### 7.4 Semántica: comando → ack → eventos

- El panel invoca `IAgentClient.ExecuteAsync(SignedCommand)` con *client results* y espera un `CommandAck` (timeout 10 s). El ack solo dice **aceptado/rechazado**, no "terminado".
- El trabajo largo se reporta con eventos referidos a `OperationId`.
- Los comandos son **idempotentes por `CommandId`** y por estado (p. ej. `Start` sobre un servidor ya `Running` → `Accepted` con `AlreadyInState`).

```csharp
public sealed record CommandAck(Guid CommandId, AckStatus Status, string? ErrorCode, string? Message);
public enum AckStatus { Accepted, AlreadyInState, Rejected, Busy, UnsupportedCommand, InvalidSignature }
```

### 7.5 Catálogo de mensajes v1

**Panel → Agent (comandos)**

| Comando | Contenido principal |
|---|---|
| `ProvisionServerCommand` | ServerId, ShortId, directorio relativo, `ServerRuntimeSpec`. Crea carpeta, cuenta Windows, ACLs. |
| `ApplyServerSpecCommand` | Nueva `ServerRuntimeSpec` (variables, puertos, argumentos, política de reinicio). |
| `InstallServerCommand` / `UpdateServerCommand` | ServerId, `Validate` (bool), `OperationId`. |
| `StartServerCommand` / `StopServerCommand` / `RestartServerCommand` / `KillServerCommand` | ServerId (+ timeout de parada opcional). |
| `SendConsoleInputCommand` | ServerId, `Line` (≤ 512 caracteres, sin caracteres de control). |
| `SubscribeConsoleCommand` / `UnsubscribeConsoleCommand` | ServerId, `FromSequence?` (backlog). |
| `DeleteServerCommand` | ServerId, `DeleteBackups` (bool). |
| `ListDirectoryCommand`, `ReadFileCommand`, `WriteFileCommand`, `CreateDirectoryCommand`, `RenamePathCommand`, `DeletePathsCommand` | ServerId + **ruta relativa** + límites de tamaño (texto ≤ 1 MiB inline). |
| `PrepareTransferCommand` | Ticket, dirección (upload/download), ruta relativa, tamaño máximo. |
| `CreateBackupCommand` / `RestoreBackupCommand` / `DeleteBackupCommand` | BackupId, ServerId, OperationId. |
| `CancelOperationCommand` | OperationId. |
| `RotateCredentialCommand` | — (el agent inicia la rotación). |
| `UpdatePanelKeysCommand` | Lista de claves del panel firmada por la clave actualmente fijada. |

`ServerRuntimeSpec` incluye el **manifest** (texto + SHA-256; el agent cachea por hash), los valores de variables (incluidos secretos), las asignaciones de puertos, `ExtraArguments` y la política de reinicio. El Agent **renderiza** la línea de comandos él mismo a partir del manifest: el panel **nunca envía una línea de comandos ni una ruta de ejecutable libre**.

**Agent → Panel (métodos del hub)**

```csharp
Task<HelloAccepted> Hello(AgentHello hello);          // primer mensaje tras conectar
Task ServerStateChanged(ServerStateChanged e);
Task ConsoleOutput(ServerConsoleOutput e);             // solo para servidores suscritos
Task Metrics(MetricsBatch e);                          // cada 5 s, un mensaje por nodo
Task NodeMetrics(NodeMetrics e);                       // cada 15 s
Task OperationProgress(OperationProgress e);
Task OperationCompleted(OperationCompleted e);         // reintentado desde un outbox hasta ack

public sealed record AgentHello(
    string AgentVersion, int ProtocolVersion, IReadOnlyList<string> Capabilities,
    NodeInventory Inventory,                           // hostname, OS build, CPU, RAM, discos
    IReadOnlyList<ServerSnapshot> Servers,             // estado real de cada servidor conocido
    IReadOnlyList<Guid> OperationsInFlight);

public sealed record ServerStateChanged(
    Guid ServerId, RuntimeState State, RuntimeState Previous,
    DateTime AtUtc, long StateSequence, int? ExitCode, StateChangeReason Reason);

public sealed record ServerConsoleOutput(
    Guid ServerId, long Sequence, DateTime AtUtc, string Text, bool IsGapMarker);

public sealed record ServerMetrics(
    Guid ServerId, double CpuPercent, long MemoryBytes, int ProcessCount,
    int? MainProcessId, TimeSpan Uptime);

public sealed record OperationProgress(Guid OperationId, string Stage, double? Percent, string? Message);
public sealed record OperationCompleted(Guid OperationId, OperationOutcome Outcome,
    string? ErrorCode, string? Message, JsonElement? Result);
```

`HelloAccepted` devuelve: hora del servidor (para detectar clock skew), estados deseados por servidor, suscripciones de consola activas y claves del panel vigentes. Con ello se **reconcilia** tras cualquier reconexión o reinicio del panel (ver doc 05).

`DiskUsageBytes` no va en las métricas periódicas: recorrer 60 GB de CS2 cada 5 s sería absurdo. Se calcula bajo demanda y con caché (≥ 10 min).

### 7.6 Consola: flujo y volumen

- El Agent mantiene por servidor un **ring buffer** (p. ej. 2 MiB) con `Sequence` monotónico, y escribe un log rotado en disco con un escritor en lote (canal + flush periódico, nunca un write por callback).
- Solo se envía consola al panel si hay al menos un navegador mirando (`SubscribeConsoleCommand` con conteo de referencias en el panel). 30 servidores con logs ruidosos no saturan el enlace si nadie los mira.
- El Agent coalesce salida (≤ 50 ms o 16 KiB por mensaje). Si el enlace se atasca, descarta lo más antiguo y emite un `IsGapMarker`.
- Reconexión del navegador: envía su última `Sequence`; el panel pide backlog desde ahí. Sin huecos silenciosos.
- La decodificación UTF-8 es *stateful* en el Agent (los chunks pueden cortar un carácter multibyte); en modo `pipes` la codificación se declara en el manifest.

### 7.7 Transferencias grandes

El panel no puede conectarse al agent (no hay puerto de entrada), así que:

1. Navegador → `POST /api/v1/servers/{id}/files/upload` (streaming, autorizado).
2. Panel crea un **ticket** de un solo uso (aleatorio 256 bits, TTL 60 s) y envía `PrepareTransferCommand`.
3. Agent abre `GET /agent/v1/transfers/{ticket}` y el panel **encadena** el stream del navegador hacia esa respuesta (`System.IO.Pipelines`, con backpressure, sin almacenar en disco del panel).
4. Descargas (archivos, backups): simétrico con `PUT` desde el agent.

Límites de tamaño configurables; el agent escribe en un archivo temporal dentro del directorio del servidor y lo renombra al terminar (atómico).

### 7.8 Versionado del protocolo

- Versión mayor en la ruta (`/agent/v1/…`) y en `ProtocolVersion`.
- Dentro de una versión mayor solo cambios **aditivos**: campos nuevos opcionales, comandos nuevos anunciados por `Capabilities`. El panel no envía un comando que el agent no anuncia.
- Panel N soporta agents de protocolo N y N-1 durante al menos una versión menor, para permitir actualizar nodos gradualmente.
- Incompatibilidad → el panel rechaza el `Hello` con un error explícito ("Agent 1.0 no compatible con panel 2.0; actualiza el agent"), visible en la UI del nodo.

### 7.9 Límites y robustez

- `MaximumReceiveMessageSize` acotado (p. ej. 1 MiB) en ambos hubs.
- Reconexión con backoff exponencial + jitter (máx. 60 s); reautenticación por challenge en cada reconexión.
- Keep-alive de SignalR; un nodo sin conexión > 30 s pasa a "desconectado" en la UI.
- Si el panel cae, **los game servers siguen funcionando**; el auto-restart por crash y el auto-arranque por boot son locales del agent.

## 8. Registro y autenticación de un nodo

### 8.1 Identidad

- Cada nodo tiene un **par de claves ECDSA P-256 generado en el propio nodo**. La clave privada **nunca sale del nodo**: se crea con CNG como clave de máquina **no exportable**, usando el *Microsoft Platform Crypto Provider* (TPM) si existe, o el *Software Key Storage Provider* si no.
- El panel solo conoce la clave pública (`node_credentials`). No existe una API key global; cada nodo tiene su credencial individual, revocable y rotable.
- El panel tiene su propia clave de firma; el agent la **fija** en el enrolamiento (por fingerprint) y así autentica al panel independientemente del certificado TLS (útil con proxies que terminan TLS o certificados autofirmados).

Comparación con mTLS:

| | **Prueba de posesión a nivel de aplicación (recomendada)** | mTLS |
|---|---|---|
| Detrás de reverse proxy que termina TLS | Funciona | Se rompe (o requiere reenviar el certificado por cabecera, frágil) |
| CA propia, emisión, CRL | No hace falta | Necesaria |
| Revocación inmediata | Sí (estado en BD + abortar conexión) | Requiere CRL/OCSP o lógica propia |
| Robustez frente a MITM de TLS | Comandos firmados + clave del panel fijada | Fuerte a nivel transporte |

**Recomendación:** prueba de posesión a nivel de aplicación con TLS obligatorio. La identidad (par de claves del nodo) es la misma que usaría mTLS, así que un modo mTLS opcional (el panel firma un certificado para la misma clave) puede añadirse más adelante sin re-enrolar nodos.

### 8.2 Enrolamiento

```
Admin (UI)                  Panel                                   Nodo Windows
   │ POST /api/v1/nodes ───►│ crea Node(status=pending_enrollment)
   │                        │ token = 32 bytes aleatorios (CSPRNG)
   │                        │ guarda SHA-256(token), expira en 30 min, un solo uso
   │◄── token (se muestra UNA vez) + fingerprint de la clave del panel + comando listo:
   │
   │   PulsarAgent.exe enroll --panel https://panel.ejemplo.com
   │                          --token <token> --panel-key sha256:<fingerprint>
   │                                                              │ (ejecutado como administrador)
   │                        │◄── GET /agent/v1/panel-identity ────│
   │                        │─── clave pública del panel ────────►│ verifica fingerprint == --panel-key
   │                                                              │ genera par ECDSA P-256 (CNG, no exportable, TPM si hay)
   │                        │◄── POST /agent/v1/enroll ───────────│ { token, publicKey (SPKI), keyStorage,
   │                        │                                     │   hostname, osBuild, agentVersion,
   │                        │                                     │   proof = Sign("pulsar-enroll-v1"‖token‖publicKey) }
   │                        │ valida: hash existe, no expirado, no consumido;
   │                        │ verifica proof con publicKey;
   │                        │ consumo atómico: UPDATE … SET consumed_at=now() WHERE consumed_at IS NULL
   │                        │ crea NodeCredential(active); Node → active; audita "node.enrolled"
   │                        │─── { nodeId, credentialId, panelKeys } firmado con clave del panel ──►│
   │                                                              │ verifica firma; escribe config con ACL
   │                                                              │ (SYSTEM + Administrators); instala e inicia el servicio
   │◄── la UI muestra el nodo "Conectado" + fingerprint de la clave del nodo (verificable en el nodo con `PulsarAgent.exe status`)
```

Endpoint de enrolamiento con rate limiting. Un token filtrado sirve como máximo una vez y durante 30 min; el administrador ve el hostname y fingerprint del nodo que lo consumió.

### 8.3 Autenticación en cada conexión

```
Agent                                         Panel
  │── POST /auth/challenge {nodeId, credentialId} ─►│ nonce 32 bytes, TTL 60 s, un solo uso (en memoria)
  │◄─ {challengeId, nonce, panelOrigin} ────────────│
  │── POST /auth/token {challengeId,                │
  │     signature = Sign("pulsar-agent-auth-v1" ‖ nodeId ‖ credentialId ‖ nonce ‖ panelOrigin)} ─►│
  │                                                 │ verifica firma con la clave registrada,
  │                                                 │ credencial active, nodo no revocado/deshabilitado,
  │                                                 │ consume el nonce; actualiza last_used_at
  │◄─ JWT {sub=nodeId, cid=credentialId, sid=sessionId, aud=pulsar-agent, exp=+15 min} ─│
  │── WSS /agent/v1/hub  (Authorization: Bearer …) ─►│ OnConnected: revalida credencial/nodo contra BD;
  │                                                 │ registra conexión (cierra una conexión previa del mismo nodo)
  │── Hello(AgentHello) ───────────────────────────►│ reconciliación
  │◄─ HelloAccepted ────────────────────────────────│
```

- Incluir `panelOrigin` en lo firmado impide reutilizar una respuesta contra otro panel.
- El JWT de sesión solo sirve para el hub de agents y transferencias, dura 15 min, y la conexión se cierra al expirar (el agent reautentica de forma transparente).
- Una sola conexión activa por nodo.

### 8.4 Rotación

- El agent rota su clave automáticamente cada 90 días (configurable) o cuando el admin lo pide (`RotateCredentialCommand`).
- Flujo: genera clave nueva → `POST /credentials/rotate` con JWT vigente + prueba firmada con la clave nueva → panel crea credencial `pending` → el agent se reautentica con la nueva → panel la marca `active` y la anterior `superseded`. Si algo falla, la anterior sigue válida (no se puede dejar un nodo huérfano a mitad de rotación).
- Rotación de la clave del panel: el panel publica la nueva firmada con la vigente (`UpdatePanelKeysCommand`); los agents fijan ambas durante la transición.

### 8.5 Revocación y expiración

- Revocar (UI, permiso `node.credentials.manage`): credencial → `revoked`, se aborta inmediatamente la conexión activa del nodo, cualquier challenge posterior falla. Auditado.
- Deshabilitar nodo: mismo efecto sin borrar la credencial.
- Tokens de enrolamiento: expiran (30 min por defecto). JWT de sesión: 15 min. Credenciales de nodo: rotación automática; expiración dura opcional por política (`expires_at`).
- Re-enrolar un nodo (p. ej. reinstalación de Windows) = nuevo token de enrolamiento sobre el mismo `Node`; la credencial previa queda revocada.

### 8.6 Almacenamiento local del Agent

```
%ProgramData%\Pulsar\Agent\        ACL: SYSTEM + Administrators (herencia deshabilitada)
  config\agent.json                 URL del panel, nodeId, credentialId, claves del panel fijadas
  state\servers\{id}.json           spec por servidor; secretos cifrados con DPAPI (LocalMachine)
  state\outbox\                     OperationCompleted pendientes de ack
  steamcmd\                         instalación gestionada de SteamCMD
  console-logs\{shortId}\           logs de consola rotados (fuera del directorio del servidor)
  backups\{shortId}\                backups locales
  logs\                             logs del agent (Serilog, rolling)
<ServersRoot>\{shortId}\            directorio del servidor (configurable, p. ej. D:\PulsarServers)
```

La clave privada del nodo no está en este árbol: vive en el almacén de claves CNG/TPM.
