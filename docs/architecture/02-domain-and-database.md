# 02 — Main entities and PostgreSQL schema

> Status: **proposal under review** · Includes the permission model.

## Main entities

### Persistent (PostgreSQL)

| Module | Entity (aggregate root in **bold**) | Notes |
|---|---|---|
| Access | **User** | Managed with ASP.NET Core Identity (`IdentityUserContext`, no Identity roles). |
| Access | **Role**, RolePermission | Roles defined by administrators; permissions come from a catalog in code. |
| Access | UserRole, NodeAccess, ServerAccess | A role granted to a user in a *scope* (global / node / server). |
| Nodes | **Node**, NodeCredential, NodeEnrollmentToken | Cryptographic identity per node. |
| Nodes | NodeAllocation | Reservable `ip:port` pair (TCP and UDP) assignable to a server. |
| Templates | **GameTemplate** | Validated, versioned, immutable manifest (`key` + `version`). |
| Servers | **Server**, ServerVariable | Desired state, observed state, variables (secrets encrypted). |
| Operations | **Operation** | Long-running work executed by an agent (install, update, backup…). |
| Backups | **Backup**, BackupStorage | Metadata; the archive lives on the node, and a copy in S3-compatible storage when its server chose one (Phase 10). |
| Scheduling | **ScheduledTask**, ScheduledTaskRun | Typed action + cron + IANA time zone. |
| Notifications | **NotificationChannel**, NotificationSettings, NotificationDelivery | Where messages go (Discord webhook, email), the SMTP server, and an outbox of messages with their attempts (Phase 10). |
| Audit | **AuditEntry** | Append-only, no foreign keys (survives deletions). |
| Settings | SystemSetting, PanelSigningKey | Configuration and the panel's signing key. |

### Volatile (panel memory, never in PostgreSQL)

- `NodeConnection`: connected node, connection id, session, protocol version.
- `LiveServerMetrics` / `LiveNodeMetrics`: last sample + a short window (~1 h at one sample per minute) for sparklines. (Phase 5 keeps the last sample only; the window arrives with sparklines.)
- `ConsoleSubscription`: how many browsers watch each server's console.

When the panel restarts, all of this is rebuilt within seconds from each node's `AgentHello`.

### Behavior in the domain (example)

```csharp
public sealed class Server
{
    public ServerId Id { get; }
    public InstallState InstallState { get; private set; }
    public RuntimeState RuntimeState { get; private set; }   // last state observed by the agent
    public DesiredState DesiredState { get; private set; }
    public OperationId? ActiveOperationId { get; private set; }
    public bool IsSuspended { get; private set; }

    // Returns a domain error if the transition is not valid (installed, not suspended,
    // no exclusive operation running, compatible runtime state).
    public Result RequestStart(TimeProvider clock) { ... }
    public Result RequestStop(...) { ... }
    public Result BeginOperation(Operation op) { ... }      // per-server exclusivity
    public void ApplyObservedState(RuntimeState state, DateTime atUtc, int? exitCode) { ... }
}
```

## Permission model (scoped RBAC)

### Concepts

- **Permission**: stable key defined in code (`Permissions.Server.Start = "server.start"`). The catalog is code because code checks it; the database only stores which roles include it.
- **Role**: set of permissions with an assignable `scope`: `global`, `node` or `server`.
- **Assignment**: (user, role, concrete scope). `ServerAccess` and `NodeAccess` are assignments with a server/node scope, stored in separate tables to get real foreign keys and cascading deletes.

### Initial catalog

| Permission | Scopes where it can be granted |
|---|---|
| `server.view` `server.start` `server.stop` `server.restart` `server.kill` | server, node, global |
| `server.console.view` `server.console.write` | server, node, global |
| `server.files.view` `server.files.edit` | server, node, global |
| `server.backups.view` `server.backups.create` `server.backups.restore` `server.backups.delete` | server, node, global |
| `server.schedules.view` `server.schedules.edit` | server, node, global |
| `server.startup.edit` (startup variables and arguments) | server, node, global |
| `server.settings.edit` (name, auto-restart, …) | server, node, global |
| `server.install` (install/update/reinstall) | server, node, global |
| `server.limits.edit` (CPU and memory caps, Phase 12) | node, global |
| `server.create` `server.delete` `server.transfer` (future) | node, global |
| `node.view` `node.manage` `node.credentials.manage` | node, global |
| `template.view` `template.manage` | global |
| `user.view` `user.manage` `role.manage` `audit.view` `settings.manage` | global |

Inheritance: **global ⊃ node ⊃ server**. A `node`-scoped role may contain `node.*` and `server.*` (applies to every server on the node). A `server`-scoped role may only contain `server.*`, except `server.create/delete` and `server.limits.edit` (whoever hosts a server sets its caps, not whoever runs it).

### Evaluation

```
Effective permissions(user, server S on node N) =
     permissions(user's global roles)
   ∪ permissions(user's roles in NodeAccess for N)
   ∪ permissions(user's roles in ServerAccess for S)
```

- No *deny* rules in v1 (simpler to reason about and audit).
- `IPermissionEvaluator` caches grants per user in memory (short TTL + explicit invalidation when roles/access change; valid because v1 is a single instance).
- REST endpoints and hub methods use the same evaluator. Every endpoint declares its permission as metadata (`.RequirePermission(Permissions.Server.Start)`); a test enumerates all endpoints and **fails if one declares neither a permission nor `AllowAnonymous`**.
- Listings filter by visible resources (`server.view`); requesting a resource without access returns **404** (not 403) so its existence is not revealed.
- *Implemented in Phase 11 (node and server assignments):* `node_access` and `server_access` as below; the evaluator loads them with the global roles. The endpoint gate checks routes under `/servers/{id}` and `/nodes/{id}` against that server (with its node) or node: a role on one server opens nothing on another, and a server or node the user cannot see at all (`server.view`; for nodes `node.view`, `node.manage` or `server.create`) answers `404` like a missing one; one they see but may not act on answers `403`. A user with no such permission anywhere gets `403` for every such route, existing or not. Lists and the create dialog's catalogs pass the gate with the permission held anywhere and show only what it applies to (servers on the user's nodes and their own servers; the nodes they may see or create servers on); creating a server checks `server.create` on its node. Assignments are given and taken one by one (`PUT`/`DELETE /users/{id}/nodes/{node}/roles/{role}`, same for servers) under the same anti-escalation rules as global ones, audited, and close the user's live connections. The session carries the permissions per node and per server, so pages show only the actions the user has there. Roles count a user once however many places they hold it, and cannot be deleted while assigned anywhere.

### Anti-escalation rules

1. System role **Administrator** (`is_system = true`): always holds every permission in the catalog (computed, so new permissions are included automatically). Not editable or deletable.
2. The last global Administrator assignment cannot be removed.
3. Holders of `role.manage`/`user.manage` **can only grant permissions they hold themselves** in that scope.
4. Nobody can change their own assignments.
5. Every role/access change is audited with the permission diff.
6. `user.manage` can only disable, delete or reset the password of users whose global permissions the actor also holds, and never of themselves (a user manager cannot lock out an administrator).

There is no `Admin = true`: "administrator" is just a role.

### API keys (Phase 12)

A user creates keys for scripts and tools from their account (`/api/v1/account/api-keys`, session cookie only) and sends one as `Authorization: Bearer pulsar_…`.

- A key acts as its user, with the user's permissions as they are at each request (a removed role takes effect at once), optionally narrowed to a list of permissions (`EffectivePermissions.Restrict`: the evaluator narrows the request user's permissions, so every check, list and anti-escalation rule of the request sees the smaller set). It has a name, an optional expiry, and records its last use (at most once a minute).
- Stored as the SHA-256 of its value (32 random bytes), found by that hash; the value is shown once. The first 13 characters are kept to tell keys apart. At most 25 working keys per user.
- Authentication: a policy scheme sends requests with `Bearer pulsar_…` to the key handler and all others to the cookie; the two are never merged (a cookie sent with a key is ignored). Only endpoints guarded by a permission accept keys; sign-in, the account, two-factor, the keys themselves (`RequireSignedInUser`) and the live hub take the cookie only, so a key never creates keys or changes its user's password. A key from a user who is disabled or deleted stops at once; keys of a user whose role requires two-factor authentication work only while the user has it on.
- No antiforgery token for key requests (no cookie a browser adds by itself), a rate limit per key (600 requests a minute, `RateLimiting:Auth:ApiKeyPermitLimit`), audited with the actor type `api_key` (the user's id, "user · key name").
- Revoked by their user, or all at once by an administrator (`DELETE /api/v1/users/{id}/api-keys`, `user.manage`).

## PostgreSQL schema

Conventions:

- Dedicated database (`pulsar`). PostgreSQL **15+** supported (17/18 recommended). At startup the panel checks connectivity and version; on failure it exits with a clear error (host, port, cause) — **no fallback**.
- `snake_case` names (`EFCore.NamingConventions`).
- `uuid` primary keys with **UUIDv7 generated by the application** (`Guid.CreateVersion7()`, see ADR-0004).
- Timestamps as `timestamptz`, always UTC.
- Enums as `text` + `CHECK` (readable when inspecting the database; adding a value is a trivial migration, unlike native PostgreSQL enums).
- Optimistic concurrency with the `xmin` system column (mapped as a row version by Npgsql) on mutable aggregates (`servers`, `nodes`, `roles`, `scheduled_tasks`).
- EF Core migrations, applied at startup (configurable: `Database:ApplyMigrationsOnStartup`, default true; when false the panel refuses to start with pending migrations) or explicitly with `Pulsar.Api.exe migrate`. EF Core takes its own database-wide migration lock, so concurrent starts are safe without an extra advisory lock. Idempotent SQL scripts are published with each release.

The DDL below is the **design reference**; the real source will be the EF migrations.

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
-- + user_tokens, user_logins, user_claims: standard IdentityUserContext tables
--   (external logins unused in v1). Two-factor rows in user_tokens (login_provider '[Pulsar]'): the authenticator key
--   encrypted with Data Protection, the recovery codes as SHA-256 hashes, and the last accepted TOTP step (a code
--   works once).

CREATE TABLE roles (
    id               uuid PRIMARY KEY,
    name             varchar(64)  NOT NULL,
    normalized_name  varchar(64)  NOT NULL UNIQUE,
    description      varchar(512),
    scope            text NOT NULL CHECK (scope IN ('global','node','server')),
    is_system        boolean NOT NULL DEFAULT false,
    requires_two_factor boolean NOT NULL DEFAULT false, -- its users must sign in with 2FA (the system role too)
    created_at       timestamptz NOT NULL,
    updated_at       timestamptz NOT NULL,
    UNIQUE (id, scope)                       -- enables composite FKs that pin the role scope
);

CREATE TABLE role_permissions (
    role_id     uuid NOT NULL REFERENCES roles(id) ON DELETE CASCADE,
    permission  varchar(64) NOT NULL,        -- validated against the catalog in code
    PRIMARY KEY (role_id, permission)
);

CREATE TABLE user_roles (                    -- global assignments
    user_id     uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    role_id     uuid NOT NULL,
    role_scope  text NOT NULL DEFAULT 'global' CHECK (role_scope = 'global'),
    granted_by  uuid REFERENCES users(id) ON DELETE SET NULL,
    granted_at  timestamptz NOT NULL,
    PRIMARY KEY (user_id, role_id),
    FOREIGN KEY (role_id, role_scope) REFERENCES roles(id, scope) ON DELETE RESTRICT
);

-- node_access and server_access are created after nodes/servers (below).

-- ===================== Nodes =====================
CREATE TABLE nodes (
    id                  uuid PRIMARY KEY,
    name                varchar(64) NOT NULL UNIQUE,
    description         varchar(512),
    status              text NOT NULL CHECK (status IN ('pending_enrollment','active','disabled','revoked')),
    public_host         varchar(255),        -- host/IP shown to players
    maintenance_mode    boolean NOT NULL DEFAULT false,
    -- inventory reported by the agent (last connection)
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
    offline_notified_at timestamptz,         -- told offline (notifications); cleared, and told, when it is back
    disk_low_notified_at timestamptz,        -- told a drive is low; cleared when every drive has room again
    created_at          timestamptz NOT NULL,
    updated_at          timestamptz NOT NULL
);

CREATE TABLE node_credentials (
    id             uuid PRIMARY KEY,
    node_id        uuid NOT NULL REFERENCES nodes(id) ON DELETE CASCADE,
    algorithm      text NOT NULL CHECK (algorithm IN ('ecdsa-p256-sha256')),
    public_key     bytea NOT NULL,               -- SubjectPublicKeyInfo DER
    fingerprint    varchar(80) NOT NULL UNIQUE,  -- 'sha256:<base64url>'
    key_storage    text NOT NULL CHECK (key_storage IN ('tpm','software')),  -- informational
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
    token_hash                 bytea NOT NULL UNIQUE,   -- SHA-256 of 32 random bytes
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
    version         varchar(32) NOT NULL,       -- semver of the content
    schema_version  integer NOT NULL,           -- manifest format version
    name            varchar(128) NOT NULL,
    source          text NOT NULL CHECK (source IN ('bundled','imported')),
    content         text NOT NULL,              -- exact manifest bytes (text, not jsonb: jsonb does not preserve the original and breaks the hash)
    content_sha256  bytea NOT NULL,
    is_enabled      boolean NOT NULL DEFAULT true,
    imported_by     uuid REFERENCES users(id) ON DELETE SET NULL,
    imported_at     timestamptz NOT NULL,
    UNIQUE (key, version)
);

-- ===================== Servers =====================
CREATE TABLE servers (
    id                   uuid PRIMARY KEY,
    short_id             varchar(12) NOT NULL UNIQUE,  -- random base32; Windows account and folder names
    node_id              uuid NOT NULL REFERENCES nodes(id) ON DELETE RESTRICT,
    template_id          uuid NOT NULL REFERENCES game_templates(id) ON DELETE RESTRICT,
    name                 varchar(64) NOT NULL,
    description          varchar(512),
    install_state        text NOT NULL CHECK (install_state IN ('not_installed','installing','installed','failed')),
    runtime_state        text NOT NULL CHECK (runtime_state IN ('offline','starting','running','stopping','crashed')),
    desired_state        text NOT NULL CHECK (desired_state IN ('stopped','running')),
    active_operation_id  uuid,                 -- FK added after operations is created
    active_operation_type text,                -- its type: a backup leaves the server free to start (doc 05)
    backup_limit         integer CHECK (backup_limit BETWEEN 1 AND 1000),  -- retention; NULL keeps every backup
    cpu_limit_percent    integer CHECK (cpu_limit_percent BETWEEN 1 AND 100),  -- Phase 12: share of the node; NULL: no cap
    memory_limit_mb      integer CHECK (memory_limit_mb BETWEEN 128 AND 1048576),  -- Phase 12: committed memory; NULL: no cap
    backup_storage_id    uuid REFERENCES backup_storages(id) ON DELETE RESTRICT,  -- where new backups are copied; NULL: nowhere
    is_suspended         boolean NOT NULL DEFAULT false,
    auto_start           boolean NOT NULL DEFAULT false,  -- start when the agent starts
    restart_on_crash     boolean NOT NULL DEFAULT true,
    extra_arguments      text[] NOT NULL DEFAULT '{}',    -- only if the manifest allows it
    memory_limit_mb      integer,              -- reserved (Job Object limits, post-v1)
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
    value            text,                     -- NULL when the variable is secret
    protected_value  bytea,                    -- secret, encrypted with Data Protection
    updated_at       timestamptz NOT NULL,
    updated_by       uuid REFERENCES users(id) ON DELETE SET NULL,
    PRIMARY KEY (server_id, key),
    CHECK ((value IS NULL) <> (protected_value IS NULL))
);

CREATE TABLE node_allocations (
    id          uuid PRIMARY KEY,
    node_id     uuid NOT NULL REFERENCES nodes(id) ON DELETE CASCADE,
    ip_address  varchar(15) NOT NULL,          -- canonical IPv4, 0.0.0.0 = all interfaces (Phase 4: text, not inet)
    port        integer NOT NULL CHECK (port BETWEEN 1 AND 65535),
    server_id   uuid REFERENCES servers(id) ON DELETE SET NULL,
    port_name   varchar(32),                   -- manifest port name ('game', 'tv'…)
    notes       varchar(128),
    UNIQUE (node_id, ip_address, port),
    UNIQUE (server_id, port_name),
    CHECK ((server_id IS NULL) = (port_name IS NULL))
);
-- Conflicts between 0.0.0.0:P and 192.168.1.10:P on the same node are validated in the application.

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

CREATE TABLE api_keys (  -- Phase 12
    id            uuid PRIMARY KEY,
    user_id       uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    name          varchar(64) NOT NULL,
    prefix        varchar(13) NOT NULL,         -- shown to tell keys apart
    token_hash    bytea NOT NULL UNIQUE,        -- SHA-256 of the value
    permissions   text[],                       -- NULL: all of the user's
    created_at    timestamptz NOT NULL,
    expires_at    timestamptz,
    last_used_at  timestamptz,
    revoked_at    timestamptz
);

-- ===================== Operations =====================
CREATE TABLE operations (
    id                   uuid PRIMARY KEY,
    type                 text NOT NULL CHECK (type IN (
                             'server_provision','server_install','server_update',
                             'server_delete','backup_create','backup_restore')),
                             -- a reinstall is provision + install; deleting a backup is a plain command
    status               text NOT NULL CHECK (status IN (
                             'queued','running','succeeded','failed','cancelled','interrupted')),
    node_id              uuid NOT NULL REFERENCES nodes(id) ON DELETE CASCADE,
    server_id            uuid REFERENCES servers(id) ON DELETE SET NULL,
    requested_by         uuid REFERENCES users(id) ON DELETE SET NULL,
    requested_via        text NOT NULL CHECK (requested_via IN ('user','schedule','system')),
                         -- a scheduled run points to the operation it started (scheduled_task_runs.operation_id)
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
-- Progress is pushed in real time over SignalR; the database is only written with throttling
-- (stage change or every ~15 s), never on every SteamCMD line.

-- ===================== Backups =====================
CREATE TABLE backups (
    id             uuid PRIMARY KEY,
    server_id      uuid NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
    node_id        uuid NOT NULL REFERENCES nodes(id) ON DELETE CASCADE,
    name           varchar(128) NOT NULL,
    status         text NOT NULL CHECK (status IN ('creating','completed','failed','cancelled')),
    size_bytes     bigint,
    file_count     integer,
    skipped_files  integer,                          -- files the game held open while it ran
    sha256         bytea,                            -- of the archive: a restore checks it first
    is_locked      boolean NOT NULL DEFAULT false,   -- excluded from automatic rotation
    created_by     uuid REFERENCES users(id) ON DELETE SET NULL,
    created_via    text NOT NULL CHECK (created_via IN ('user','schedule','system')),
    operation_id   uuid REFERENCES operations(id) ON DELETE SET NULL,
    started_at     timestamptz,
    completed_at   timestamptz,                      -- duration = completed_at - started_at
    error_message  varchar(1024),
    storage_id     uuid REFERENCES backup_storages(id) ON DELETE RESTRICT, -- its copy's storage, while it has one
    remote_key     varchar(512),                     -- {prefix}{server}/{backup}.zip
    remote_status  text NOT NULL DEFAULT 'none' CHECK (remote_status IN ('none','pending','copied','failed')),
    remote_error   varchar(1024),                    -- a failed copy: the backup is on the node only
    created_at     timestamptz NOT NULL
);
CREATE INDEX ix_backups_server ON backups (server_id, created_at DESC);

-- S3-compatible storage backups are copied to (Phase 10); settings.manage.
CREATE TABLE backup_storages (
    id                 uuid PRIMARY KEY,
    name               varchar(64) NOT NULL,
    endpoint           varchar(255),                 -- https://…; NULL for AWS S3
    region             varchar(64) NOT NULL,         -- 'auto' for R2
    bucket             varchar(63) NOT NULL,
    prefix             varchar(200) NOT NULL,        -- '' or 'folder/'
    access_key_id      varchar(128) NOT NULL,
    secret_protected   bytea NOT NULL,               -- Data Protection; sent to nodes only inside backup commands
    path_style         boolean NOT NULL,
    created_at         timestamptz NOT NULL,
    updated_at         timestamptz NOT NULL
);
-- A storage that servers use or backups have copies in cannot be deleted.
-- A backup row is written with its operation; the operation's result completes it (an interrupted one fails it).
-- Deleting a server deletes its rows (and the node its archives).

-- ===================== Scheduling =====================
CREATE TABLE scheduled_tasks (
    id               uuid PRIMARY KEY,
    server_id        uuid NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
    name             varchar(128) NOT NULL,
    cron_expression  varchar(128) NOT NULL,          -- 5 fields, parsed with Cronos
    time_zone        varchar(64)  NOT NULL,          -- IANA, e.g. 'America/Argentina/Buenos_Aires'
    action_type      text NOT NULL CHECK (action_type IN ('start','stop','restart','backup','update','console_command')),
    action_payload   jsonb NOT NULL DEFAULT '{}',    -- {"command": "say …"} for console_command, {"validate": true} for update
    only_if_running  boolean NOT NULL DEFAULT false, -- restarts and backups only (console commands always need it)
    is_enabled       boolean NOT NULL DEFAULT true,
    next_run_at      timestamptz,                    -- UTC
    last_run_at      timestamptz,
    created_by       uuid REFERENCES users(id) ON DELETE SET NULL,
    created_at       timestamptz NOT NULL,
    updated_at       timestamptz NOT NULL
);
CREATE INDEX ix_scheduled_tasks_due ON scheduled_tasks (next_run_at) WHERE is_enabled;
-- next_run_at is NULL while disabled; enabling or changing a task computes it again from now (no burst).
-- At most 50 tasks per server.

CREATE TABLE scheduled_task_runs (
    id            uuid PRIMARY KEY,
    task_id       uuid NOT NULL REFERENCES scheduled_tasks(id) ON DELETE CASCADE,
    server_id     uuid NOT NULL,                    -- the task's (the live channel refreshes the server's watchers)
    scheduled_for timestamptz NOT NULL,             -- the first one, for missed runs
    started_at    timestamptz NOT NULL,
    finished_at   timestamptz,
    status        text NOT NULL CHECK (status IN ('running','succeeded','failed','skipped')),
    operation_id  uuid REFERENCES operations(id) ON DELETE SET NULL, -- a backup or update: it has the outcome
    occurrences   integer NOT NULL CHECK (occurrences >= 1), -- runs missed while the panel was down share one row
    error_code    varchar(64),                      -- schedules.missed, schedules.not_running, server.busy…
    message       varchar(1024)
);
CREATE INDEX ix_task_runs_task ON scheduled_task_runs (task_id, started_at DESC);
CREATE INDEX ix_task_runs_running ON scheduled_task_runs (started_at) WHERE status = 'running';
-- The last 100 runs of each task are kept.

-- ===================== Notifications (Phase 10) =====================
CREATE TABLE notification_channels (
    id                    uuid PRIMARY KEY,
    name                  varchar(64) NOT NULL,
    kind                  text NOT NULL CHECK (kind IN ('discord','email')),
    is_enabled            boolean NOT NULL,
    events                text[] NOT NULL,          -- server_crashed, server_start_failed, backup_failed, scheduled_task_failed, node_offline, node_disk_low
    server_ids            uuid[],                   -- server events only for these; NULL = every server
    language              varchar(8) NOT NULL,      -- en | es
    webhook_url_protected bytea,                    -- Discord: the webhook URL, Data Protection
    recipients            text[] NOT NULL,          -- email: 1..10 addresses
    created_at            timestamptz NOT NULL,
    updated_at            timestamptz NOT NULL,
    CHECK ((kind = 'discord' AND webhook_url_protected IS NOT NULL) OR (kind = 'email' AND cardinality(recipients) > 0))
);

CREATE TABLE notification_settings (            -- one row (id = 1)
    id                      integer PRIMARY KEY CHECK (id = 1),
    panel_url               varchar(255),         -- for links in messages
    smtp_host               varchar(255),         -- NULL: email off
    smtp_port               integer NOT NULL,
    smtp_security           text NOT NULL CHECK (smtp_security IN ('start_tls','tls','none')),
    smtp_user_name          varchar(255),
    smtp_password_protected bytea,                -- Data Protection
    from_address            varchar(254),
    from_name               varchar(64),
    disk_low_percent        integer NOT NULL,     -- 1..50, default 10
    node_offline_minutes    integer NOT NULL,     -- 1..1440, default 3
    updated_at              timestamptz NOT NULL
);

CREATE TABLE notification_deliveries (           -- the outbox: written in the same transaction as what raised it
    id              uuid PRIMARY KEY,
    channel_id      uuid NOT NULL REFERENCES notification_channels(id) ON DELETE CASCADE,
    event           text NOT NULL,
    subject         varchar(128) NOT NULL,        -- server:{id}, node:{id}:offline…: repeats within 10 min are dropped
    message         jsonb NOT NULL,               -- title, text, severity, fields, link, already in the channel's language
    status          text NOT NULL CHECK (status IN ('pending','sent','failed')),
    attempts        integer NOT NULL,
    next_attempt_at timestamptz,                  -- pending: when a sender may take it (a lease while one sends it)
    last_error      varchar(512),
    created_at      timestamptz NOT NULL,
    sent_at         timestamptz
);
CREATE INDEX ix_notification_deliveries_due ON notification_deliveries (next_attempt_at) WHERE status = 'pending';
CREATE INDEX ix_notification_deliveries_subject ON notification_deliveries (channel_id, event, subject, created_at);
-- Retries after 30 s, 2 min, 10 min, 30 min and 1 h, then failed; Discord's 429 waits as long as it asks; other
-- refusals (a deleted webhook, a rejected address) fail at once. Sent and failed deliveries are kept 30 days.

-- ===================== Audit =====================
CREATE TABLE audit_logs (
    id              uuid PRIMARY KEY,
    occurred_at     timestamptz NOT NULL,
    action          varchar(64) NOT NULL,          -- catalog in code: 'server.start', 'auth.login_failed'…
    outcome         text NOT NULL CHECK (outcome IN ('success','failure','denied')),
    actor_type      text NOT NULL CHECK (actor_type IN ('user','node','schedule','system','anonymous')),
    actor_user_id   uuid,                          -- no FK on purpose
    actor_display   varchar(128),                  -- name at the time of the event
    node_id         uuid,
    server_id       uuid,
    target_type     varchar(32),
    target_id       varchar(512),                  -- uuid or relative file path
    ip_address      inet,
    user_agent      varchar(512),
    correlation_id  varchar(64),
    metadata        jsonb NOT NULL DEFAULT '{}'    -- structured and sanitized: never secrets
);
CREATE INDEX ix_audit_time   ON audit_logs (occurred_at DESC);
CREATE INDEX ix_audit_server ON audit_logs (server_id, occurred_at DESC) WHERE server_id IS NOT NULL;
CREATE INDEX ix_audit_actor  ON audit_logs (actor_user_id, occurred_at DESC) WHERE actor_user_id IS NOT NULL;
-- Configurable retention (365 days by default). Monthly partitioning if volume requires it (post-v1).

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
    protected_private_key  bytea NOT NULL,        -- encrypted with Data Protection
    status                 text NOT NULL CHECK (status IN ('active','retiring','retired')),
    created_at             timestamptz NOT NULL,
    retired_at             timestamptz
);
```

### What is deliberately NOT in PostgreSQL

- Per-second metrics (memory only; aggregated history is post-v1).
- Console output (ring buffers and rotated log files on the node).
- Backup archives (on the node).
- MySQL/MariaDB databases for games: a future, independent module (`GameDatabases`) with its own *metadata* tables (host, user, assigned server). Game data never touches Pulsar's database.
