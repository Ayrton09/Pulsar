# 02 — Entidades principales y esquema PostgreSQL

> Estado: **propuesta para revisión** · Cubre los puntos 5–6 y el modelo de permisos.

## 5. Entidades principales

### Persistentes (PostgreSQL)

| Módulo | Entidad (raíz de agregado en **negrita**) | Notas |
|---|---|---|
| Access | **User** | Gestionado con ASP.NET Core Identity (`IdentityUserContext`, sin roles de Identity). |
| Access | **Role**, RolePermission | Roles definidos por administradores; permisos = catálogo en código. |
| Access | UserRole, NodeAccess, ServerAccess | Asignación de un rol a un usuario en un *scope* (global / nodo / servidor). |
| Nodes | **Node**, NodeCredential, NodeEnrollmentToken | Identidad criptográfica por nodo. |
| Nodes | NodeAllocation | Par `ip:puerto` reservable (TCP y UDP) asignable a un servidor. |
| Templates | **GameTemplate** | Manifest validado, versionado e inmutable (`key` + `version`). |
| Servers | **Server**, ServerVariable | Estado deseado, estado observado, variables (secretas cifradas). |
| Operations | **Operation** | Trabajo largo ejecutado por un Agent (instalar, actualizar, backup…). |
| Backups | **Backup** | Metadatos; el archivo vive en el nodo. |
| Scheduling | **ScheduledTask**, ScheduledTaskRun | Acción tipada + cron + zona horaria IANA. |
| Audit | **AuditEntry** | Append-only, sin FKs (sobrevive a borrados). |
| Settings | SystemSetting, PanelSigningKey | Configuración y clave de firma del panel. |

### Volátiles (memoria del Panel, nunca en PostgreSQL)

- `NodeConnection`: nodo conectado, connectionId, sesión, versión de protocolo.
- `LiveServerMetrics` / `LiveNodeMetrics`: última muestra + ventana corta (~1 h a 1 muestra/min) para sparklines.
- `ConsoleSubscription`: cuántos navegadores miran la consola de cada servidor.

Si el panel se reinicia, todo esto se reconstruye en segundos con el `AgentHello` de cada nodo.

### Comportamiento en el dominio (ejemplos)

```csharp
public sealed class Server
{
    public ServerId Id { get; }
    public InstallState InstallState { get; private set; }
    public RuntimeState RuntimeState { get; private set; }   // último observado por el Agent
    public DesiredState DesiredState { get; private set; }
    public OperationId? ActiveOperationId { get; private set; }
    public bool IsSuspended { get; private set; }

    // Devuelve error de dominio si la transición no es válida (instalado, no suspendido,
    // sin operación exclusiva activa, estado runtime compatible).
    public Result RequestStart(TimeProvider clock) { ... }
    public Result RequestStop(...) { ... }
    public Result BeginOperation(Operation op) { ... }      // exclusividad por servidor
    public void ApplyObservedState(RuntimeState state, DateTime atUtc, int? exitCode) { ... }
}
```

## Modelo de permisos (RBAC con scopes)

### Conceptos

- **Permission**: clave estable definida en código (`Permissions.Server.Start = "server.start"`). El catálogo es código porque es el código el que la comprueba; la BD solo guarda qué roles la incluyen.
- **Role**: conjunto de permisos con un `scope` asignable: `global`, `node` o `server`.
- **Asignación**: (usuario, rol, scope concreto). `ServerAccess` y `NodeAccess` del encargo son asignaciones con scope servidor/nodo, en tablas separadas para tener FKs reales y borrado en cascada.

### Catálogo inicial

| Permiso | Scopes donde puede concederse |
|---|---|
| `server.view` `server.start` `server.stop` `server.restart` `server.kill` | server, node, global |
| `server.console.view` `server.console.write` | server, node, global |
| `server.files.view` `server.files.edit` | server, node, global |
| `server.backups.view` `server.backups.create` `server.backups.restore` `server.backups.delete` | server, node, global |
| `server.schedules.view` `server.schedules.edit` | server, node, global |
| `server.startup.edit` (variables y argumentos de arranque) | server, node, global |
| `server.settings.edit` (nombre, auto-restart, etc.) | server, node, global |
| `server.install` (instalar/actualizar/reinstalar) | server, node, global |
| `server.create` `server.delete` `server.transfer` (futuro) | node, global |
| `node.view` `node.manage` `node.credentials.manage` | node, global |
| `template.view` `template.manage` | global |
| `user.view` `user.manage` `role.manage` `audit.view` `settings.manage` | global |

Regla de herencia: **global ⊃ nodo ⊃ servidor**. Un rol de scope `node` puede contener `node.*` y `server.*` (aplica a todos los servidores del nodo). Un rol de scope `server` solo puede contener `server.*` salvo `server.create/delete`.

### Evaluación

```
Permisos efectivos(usuario, servidor S en nodo N) =
     permisos(roles globales del usuario)
   ∪ permisos(roles del usuario en NodeAccess para N)
   ∪ permisos(roles del usuario en ServerAccess para S)
```

- Sin reglas *deny* en v1 (más simple de razonar y auditar).
- `IPermissionEvaluator` cachea los grants por usuario en memoria (TTL corto + invalidación explícita al modificar roles/accesos; válido porque v1 es una sola instancia).
- Endpoints REST y métodos de hub usan el mismo evaluador. Cada endpoint declara su permiso con metadata (`.RequirePermission(Permissions.Server.Start)`); un test recorre todos los endpoints y **falla si alguno no declara permiso ni `AllowAnonymous`**.
- Los listados filtran por recursos visibles (`server.view`); pedir un recurso sin acceso devuelve **404** (no 403) para no revelar existencia.

### Reglas anti-escalada

1. Rol de sistema **Administrator** (`is_system = true`): siempre contiene todos los permisos del catálogo (se calcula; los permisos nuevos se incluyen solos). No editable ni borrable.
2. No se puede quitar la última asignación global de Administrator.
3. Quien tiene `role.manage`/`user.manage` **solo puede conceder permisos que él mismo posee** en ese scope.
4. Nadie puede modificar sus propias asignaciones.
5. Todo cambio de roles/accesos se audita con el diff de permisos.

No existe `Admin = true`: "administrador" es simplemente un rol.

## 6. Esquema inicial de PostgreSQL

Convenciones:

- Base de datos dedicada (`pulsar`). PostgreSQL **15+** soportado (recomendado 17/18). Al arrancar se verifica conexión y versión; si falla, el panel termina con un error claro (host, puerto, causa) — **sin fallback**.
- Nombres `snake_case` (paquete `EFCore.NamingConventions`).
- PK `uuid` con **UUIDv7 generado en la aplicación** (`Guid.CreateVersion7()`, ver ADR-0004).
- Fechas `timestamptz`, siempre UTC.
- Enums como `text` + `CHECK` (legible al inspeccionar la BD; añadir valores es una migración trivial, a diferencia de los enums nativos de PostgreSQL).
- Concurrencia optimista con la columna de sistema `xmin` (mapeada como row version por Npgsql) en agregados mutables (`servers`, `nodes`, `roles`, `scheduled_tasks`).
- Migraciones con EF Core. Se aplican al arrancar dentro de un advisory lock (configurable: `Database:ApplyMigrationsOnStartup`), o explícitamente con `Pulsar.Api.exe migrate`. También se publican scripts SQL idempotentes por release.

El DDL siguiente es la **referencia de diseño**; la fuente real serán las migraciones EF.

```sql
-- ===================== Access =====================
CREATE TABLE users (
    id                    uuid PRIMARY KEY,
    user_name             varchar(64)  NOT NULL,
    normalized_user_name  varchar(64)  NOT NULL UNIQUE,
    email                 varchar(256),
    normalized_email      varchar(256) UNIQUE,
    email_confirmed       boolean      NOT NULL DEFAULT false,
    password_hash         text,
    security_stamp        text         NOT NULL,
    concurrency_stamp     text         NOT NULL,
    two_factor_enabled    boolean      NOT NULL DEFAULT false,
    lockout_enabled       boolean      NOT NULL DEFAULT true,
    lockout_end           timestamptz,
    access_failed_count   integer      NOT NULL DEFAULT 0,
    display_name          varchar(128),
    is_disabled           boolean      NOT NULL DEFAULT false,
    created_at            timestamptz  NOT NULL,
    last_login_at         timestamptz
);
-- + user_tokens, user_logins, user_claims: tablas estándar de IdentityUserContext
--   (tokens TOTP/recovery codes; logins externos sin uso en v1).

CREATE TABLE roles (
    id               uuid PRIMARY KEY,
    name             varchar(64)  NOT NULL,
    normalized_name  varchar(64)  NOT NULL UNIQUE,
    description      varchar(512),
    scope            text NOT NULL CHECK (scope IN ('global','node','server')),
    is_system        boolean NOT NULL DEFAULT false,
    created_at       timestamptz NOT NULL,
    updated_at       timestamptz NOT NULL,
    UNIQUE (id, scope)                       -- permite FKs compuestas que fijan el scope
);

CREATE TABLE role_permissions (
    role_id     uuid NOT NULL REFERENCES roles(id) ON DELETE CASCADE,
    permission  varchar(64) NOT NULL,        -- validado contra el catálogo en código
    PRIMARY KEY (role_id, permission)
);

CREATE TABLE user_roles (                    -- asignaciones globales
    user_id     uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    role_id     uuid NOT NULL,
    role_scope  text NOT NULL DEFAULT 'global' CHECK (role_scope = 'global'),
    granted_by  uuid REFERENCES users(id) ON DELETE SET NULL,
    granted_at  timestamptz NOT NULL,
    PRIMARY KEY (user_id, role_id),
    FOREIGN KEY (role_id, role_scope) REFERENCES roles(id, scope) ON DELETE RESTRICT
);

-- node_access y server_access se crean tras nodes/servers (abajo).

-- ===================== Nodes =====================
CREATE TABLE nodes (
    id                  uuid PRIMARY KEY,
    name                varchar(64) NOT NULL UNIQUE,
    description         varchar(512),
    status              text NOT NULL CHECK (status IN ('pending_enrollment','active','disabled','revoked')),
    public_host         varchar(255),        -- host/IP mostrado a jugadores
    maintenance_mode    boolean NOT NULL DEFAULT false,
    -- inventario reportado por el Agent (última conexión)
    hostname            varchar(255),
    os_description      varchar(255),
    os_build            varchar(64),
    agent_version       varchar(32),
    protocol_version    integer,
    cpu_model           varchar(255),
    cpu_logical_cores   integer,
    memory_total_bytes  bigint,
    servers_root        text,
    last_seen_at        timestamptz,
    created_at          timestamptz NOT NULL,
    updated_at          timestamptz NOT NULL
);

CREATE TABLE node_credentials (
    id             uuid PRIMARY KEY,
    node_id        uuid NOT NULL REFERENCES nodes(id) ON DELETE CASCADE,
    algorithm      text NOT NULL CHECK (algorithm IN ('ecdsa-p256-sha256')),
    public_key     bytea NOT NULL,           -- SubjectPublicKeyInfo DER
    fingerprint    varchar(80) NOT NULL UNIQUE,  -- 'sha256:<base64url>'
    key_storage    text NOT NULL CHECK (key_storage IN ('tpm','software')),  -- informativo
    status         text NOT NULL CHECK (status IN ('pending','active','superseded','revoked')),
    created_at     timestamptz NOT NULL,
    activated_at   timestamptz,
    last_used_at   timestamptz,
    expires_at     timestamptz,
    revoked_at     timestamptz,
    revoked_by     uuid REFERENCES users(id) ON DELETE SET NULL,
    revoke_reason  varchar(256)
);
CREATE UNIQUE INDEX ux_node_credentials_single_active
    ON node_credentials (node_id) WHERE status = 'active';

CREATE TABLE node_enrollment_tokens (
    id                         uuid PRIMARY KEY,
    node_id                    uuid NOT NULL REFERENCES nodes(id) ON DELETE CASCADE,
    token_hash                 bytea NOT NULL UNIQUE,   -- SHA-256 de 32 bytes aleatorios
    created_by                 uuid REFERENCES users(id) ON DELETE SET NULL,
    created_at                 timestamptz NOT NULL,
    expires_at                 timestamptz NOT NULL,
    consumed_at                timestamptz,
    consumed_by_credential_id  uuid REFERENCES node_credentials(id)
);

-- ===================== Templates =====================
CREATE TABLE game_templates (
    id              uuid PRIMARY KEY,
    key             varchar(64) NOT NULL,       -- 'cs2'
    version         varchar(32) NOT NULL,       -- semver del contenido
    schema_version  integer NOT NULL,           -- versión del formato de manifest
    name            varchar(128) NOT NULL,
    source          text NOT NULL CHECK (source IN ('bundled','imported')),
    content         text NOT NULL,              -- bytes exactos del manifest (text, no jsonb: jsonb no preserva el original y rompe el hash)
    content_sha256  bytea NOT NULL,
    is_enabled      boolean NOT NULL DEFAULT true,
    imported_by     uuid REFERENCES users(id) ON DELETE SET NULL,
    imported_at     timestamptz NOT NULL,
    UNIQUE (key, version)
);

-- ===================== Servers =====================
CREATE TABLE servers (
    id                   uuid PRIMARY KEY,
    short_id             varchar(12) NOT NULL UNIQUE,  -- aleatorio base32; nombres de cuenta Windows y carpeta
    node_id              uuid NOT NULL REFERENCES nodes(id) ON DELETE RESTRICT,
    template_id          uuid NOT NULL REFERENCES game_templates(id) ON DELETE RESTRICT,
    name                 varchar(64) NOT NULL,
    description          varchar(512),
    install_state        text NOT NULL CHECK (install_state IN ('not_installed','installing','installed','failed')),
    runtime_state        text NOT NULL CHECK (runtime_state IN ('offline','starting','running','stopping','crashed')),
    desired_state        text NOT NULL CHECK (desired_state IN ('stopped','running')),
    active_operation_id  uuid,                 -- FK añadida tras crear operations
    is_suspended         boolean NOT NULL DEFAULT false,
    auto_start           boolean NOT NULL DEFAULT false,  -- arrancar cuando arranca el Agent
    restart_on_crash     boolean NOT NULL DEFAULT true,
    extra_arguments      text[] NOT NULL DEFAULT '{}',    -- solo si el manifest lo permite
    memory_limit_mb      integer,              -- reservado (límites vía Job Object, post-v1)
    last_exit_code       integer,
    last_started_at      timestamptz,
    last_stopped_at      timestamptz,
    created_at           timestamptz NOT NULL,
    updated_at           timestamptz NOT NULL
);
CREATE INDEX ix_servers_node ON servers (node_id);

CREATE TABLE server_variables (
    server_id        uuid NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
    key              varchar(64) NOT NULL,
    value            text,                     -- NULL si la variable es secreta
    protected_value  bytea,                    -- secreta, cifrada con Data Protection
    updated_at       timestamptz NOT NULL,
    updated_by       uuid REFERENCES users(id) ON DELETE SET NULL,
    PRIMARY KEY (server_id, key),
    CHECK ((value IS NULL) <> (protected_value IS NULL))
);

CREATE TABLE node_allocations (
    id          uuid PRIMARY KEY,
    node_id     uuid NOT NULL REFERENCES nodes(id) ON DELETE CASCADE,
    ip_address  inet NOT NULL,                 -- 0.0.0.0 = todas las interfaces
    port        integer NOT NULL CHECK (port BETWEEN 1 AND 65535),
    server_id   uuid REFERENCES servers(id) ON DELETE SET NULL,
    port_name   varchar(32),                   -- puerto del manifest ('game','tv'…)
    notes       varchar(128),
    UNIQUE (node_id, ip_address, port),
    UNIQUE (server_id, port_name),
    CHECK ((server_id IS NULL) = (port_name IS NULL))
);
-- El conflicto 0.0.0.0:P vs 192.168.1.10:P en el mismo nodo se valida en la aplicación.

CREATE TABLE node_access (
    user_id     uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    node_id     uuid NOT NULL REFERENCES nodes(id) ON DELETE CASCADE,
    role_id     uuid NOT NULL,
    role_scope  text NOT NULL DEFAULT 'node' CHECK (role_scope = 'node'),
    granted_by  uuid REFERENCES users(id) ON DELETE SET NULL,
    granted_at  timestamptz NOT NULL,
    PRIMARY KEY (user_id, node_id, role_id),
    FOREIGN KEY (role_id, role_scope) REFERENCES roles(id, scope) ON DELETE RESTRICT
);

CREATE TABLE server_access (
    user_id     uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    server_id   uuid NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
    role_id     uuid NOT NULL,
    role_scope  text NOT NULL DEFAULT 'server' CHECK (role_scope = 'server'),
    granted_by  uuid REFERENCES users(id) ON DELETE SET NULL,
    granted_at  timestamptz NOT NULL,
    PRIMARY KEY (user_id, server_id, role_id),
    FOREIGN KEY (role_id, role_scope) REFERENCES roles(id, scope) ON DELETE RESTRICT
);

-- ===================== Operations =====================
CREATE TABLE operations (
    id                   uuid PRIMARY KEY,
    type                 text NOT NULL CHECK (type IN (
                             'server_provision','server_install','server_update','server_reinstall',
                             'server_delete','backup_create','backup_restore','backup_delete')),
    status               text NOT NULL CHECK (status IN (
                             'queued','running','succeeded','failed','cancelled','interrupted')),
    node_id              uuid NOT NULL REFERENCES nodes(id) ON DELETE CASCADE,
    server_id            uuid REFERENCES servers(id) ON DELETE SET NULL,
    requested_by         uuid REFERENCES users(id) ON DELETE SET NULL,
    requested_via        text NOT NULL CHECK (requested_via IN ('user','schedule','system')),
    scheduled_task_id    uuid,
    progress_percent     numeric(5,2),
    progress_stage       varchar(64),
    cancel_requested_at  timestamptz,
    error_code           varchar(64),
    error_message        varchar(1024),
    created_at           timestamptz NOT NULL,
    started_at           timestamptz,
    completed_at         timestamptz
);
CREATE INDEX ix_operations_server ON operations (server_id, created_at DESC);
CREATE INDEX ix_operations_active ON operations (node_id) WHERE status IN ('queued','running');
ALTER TABLE servers ADD FOREIGN KEY (active_operation_id) REFERENCES operations(id) ON DELETE SET NULL;
-- El progreso se emite en tiempo real por SignalR; a la BD solo se escribe con throttling
-- (cambio de etapa o cada ~15 s), nunca en cada línea de SteamCMD.

-- ===================== Backups =====================
CREATE TABLE backups (
    id             uuid PRIMARY KEY,
    server_id      uuid NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
    node_id        uuid NOT NULL REFERENCES nodes(id) ON DELETE CASCADE,
    name           varchar(128) NOT NULL,
    status         text NOT NULL CHECK (status IN ('pending','creating','completed','failed','cancelled','deleting')),
    storage        text NOT NULL DEFAULT 'node_local' CHECK (storage IN ('node_local')),
    size_bytes     bigint,
    file_count     integer,
    sha256         bytea,
    is_locked      boolean NOT NULL DEFAULT false,   -- excluido de la rotación automática
    created_by     uuid REFERENCES users(id) ON DELETE SET NULL,
    created_via    text NOT NULL CHECK (created_via IN ('user','schedule','system')),
    operation_id   uuid REFERENCES operations(id) ON DELETE SET NULL,
    started_at     timestamptz,
    completed_at   timestamptz,                      -- duración = completed_at - started_at
    error_message  varchar(1024),
    created_at     timestamptz NOT NULL
);
CREATE INDEX ix_backups_server ON backups (server_id, created_at DESC);

-- ===================== Scheduling =====================
CREATE TABLE scheduled_tasks (
    id               uuid PRIMARY KEY,
    server_id        uuid NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
    name             varchar(128) NOT NULL,
    cron_expression  varchar(128) NOT NULL,          -- 5 campos, parseado con Cronos
    time_zone        varchar(64)  NOT NULL,          -- IANA, p.ej. 'America/Argentina/Buenos_Aires'
    action_type      text NOT NULL CHECK (action_type IN ('start','stop','restart','backup','update','console_command')),
    action_payload   jsonb NOT NULL DEFAULT '{}',    -- tipado por acción y validado en la aplicación
    only_if_running  boolean NOT NULL DEFAULT false,
    is_enabled       boolean NOT NULL DEFAULT true,
    next_run_at      timestamptz,                    -- UTC
    last_run_at      timestamptz,
    created_by       uuid REFERENCES users(id) ON DELETE SET NULL,
    created_at       timestamptz NOT NULL,
    updated_at       timestamptz NOT NULL
);
CREATE INDEX ix_scheduled_tasks_due ON scheduled_tasks (next_run_at) WHERE is_enabled;

CREATE TABLE scheduled_task_runs (
    id            uuid PRIMARY KEY,
    task_id       uuid NOT NULL REFERENCES scheduled_tasks(id) ON DELETE CASCADE,
    scheduled_for timestamptz NOT NULL,
    started_at    timestamptz NOT NULL,
    finished_at   timestamptz,
    status        text NOT NULL CHECK (status IN ('running','succeeded','failed','skipped')),
    operation_id  uuid REFERENCES operations(id) ON DELETE SET NULL,
    message       varchar(1024)
);
CREATE INDEX ix_task_runs_task ON scheduled_task_runs (task_id, started_at DESC);

-- ===================== Audit =====================
CREATE TABLE audit_logs (
    id              uuid PRIMARY KEY,
    occurred_at     timestamptz NOT NULL,
    action          varchar(64) NOT NULL,          -- catálogo en código: 'server.start', 'auth.login_failed'…
    outcome         text NOT NULL CHECK (outcome IN ('success','failure','denied')),
    actor_type      text NOT NULL CHECK (actor_type IN ('user','node','schedule','system','anonymous')),
    actor_user_id   uuid,                          -- sin FK a propósito
    actor_display   varchar(128),                  -- nombre en el momento del evento
    node_id         uuid,
    server_id       uuid,
    target_type     varchar(32),
    target_id       varchar(512),                  -- uuid o ruta relativa de archivo
    ip_address      inet,
    user_agent      varchar(512),
    correlation_id  varchar(64),
    metadata        jsonb NOT NULL DEFAULT '{}'    -- estructurado y saneado: nunca secretos
);
CREATE INDEX ix_audit_time   ON audit_logs (occurred_at DESC);
CREATE INDEX ix_audit_server ON audit_logs (server_id, occurred_at DESC) WHERE server_id IS NOT NULL;
CREATE INDEX ix_audit_actor  ON audit_logs (actor_user_id, occurred_at DESC) WHERE actor_user_id IS NOT NULL;
-- Retención configurable (por defecto 365 días). Particionado mensual si el volumen lo exige (post-v1).

-- ===================== Settings =====================
CREATE TABLE system_settings (
    key         varchar(128) PRIMARY KEY,
    value       jsonb NOT NULL,
    updated_at  timestamptz NOT NULL,
    updated_by  uuid REFERENCES users(id) ON DELETE SET NULL
);

CREATE TABLE panel_signing_keys (
    id                     uuid PRIMARY KEY,
    algorithm              text NOT NULL CHECK (algorithm IN ('ecdsa-p256-sha256')),
    public_key             bytea NOT NULL,
    protected_private_key  bytea NOT NULL,        -- cifrada con Data Protection
    status                 text NOT NULL CHECK (status IN ('active','retiring','retired')),
    created_at             timestamptz NOT NULL,
    retired_at             timestamptz
);
```

### Lo que deliberadamente NO está en PostgreSQL

- Métricas por segundo (solo memoria; histórico agregado es post-v1).
- Salida de consola (vive en ring buffers y archivos de log rotados en el nodo).
- Archivos de backups (en el nodo).
- Bases MySQL/MariaDB de los juegos: módulo futuro independiente (`GameDatabases`) con sus propias tablas de *metadatos* (host, usuario, servidor asignado); los datos de los juegos jamás tocan la BD de Pulsar.
