# 02 — Main entities and PostgreSQL schema

> Status: **proposal under review** · Includes the permission model.

## Main entities

### Persistent (PostgreSQL)

| Module | Entity (aggregate root in **bold**) | Notes |
|---|---|---|
| Access | **User** | Managed with ASP.NET Core Identity (`IdentityUserContext`, no Identity roles). |
| Access | **Role**, RolePermission | Roles defined by administrators; permissions come from a catalog in code. |
| Access | UserRole, NodeAccess, ServerAccess | A role granted to a user in a *scope* (global / node / server). |
| Nodes | **Node**, NodeCredential, NodeEnrollmentToken, ReusableEnrollmentToken | Cryptographic identity per node; reusable tokens enroll many machines (Phase 14). |
| Nodes | NodeAllocation | Reservable `ip:port` pair (TCP and UDP) assignable to a server. |
| Templates | **GameTemplate** | Validated, versioned, immutable manifest (`key` + `version`). |
| Servers | **Server**, ServerVariable, ServerAddon, ServerMod | Desired state, observed state, variables (secrets encrypted), installed addons (Phase 12), mods from a catalog and the game version (1.0.9). |
| Operations | **Operation** | Long-running work executed by an agent (install, update, backup…). |
| Backups | **Backup**, BackupStorage | Metadata; the archive lives on the node, and a copy in S3-compatible storage when its server chose one (Phase 10). |
| Steam accounts | **SteamAccount** | Accounts SteamCMD signs in with for servers whose game needs one (Phase 13); a server points at one or none. |
| Game databases | **DatabaseHost**, ServerDatabase | MySQL/MariaDB servers the panel creates databases on, and each server's databases (Phase 13). Game data never touches Pulsar's database. |
| Scheduling | **ScheduledTask**, ScheduledTaskRun, **ServerSequence** | Typed action + cron + IANA time zone; sequences of steps the panel moves on (1.0.9). |
| Game updates | **GameUpdateWatch** | How a server follows its game's updates and what the last check found (1.0.9), and the newer versions of its game's template (1.0.10). |
| Notifications | **NotificationChannel**, NotificationSettings, NotificationDelivery | Where messages go (Discord webhook, email), the SMTP server, and an outbox of messages with their attempts (Phase 10). |
| Audit | **AuditEntry** | Append-only, no foreign keys (survives deletions). |
| Metric history | ServerMetricBucket, NodeMetricBucket | Usage per minute and per hour, written by the panel from the samples agents send (Phase 13). |
| Settings | SystemSetting, PanelSigningKey | Configuration and the panel's signing key. |

### Volatile (panel memory, never in PostgreSQL)

- `NodeConnection`: connected node, connection id, session, protocol version.
- `LiveServerMetrics` / `LiveNodeMetrics`: the last sample of each running server and connected node. Longer windows come from the metric history.
- The metric history's minute in progress (written when the minute is over, or when the panel stops).
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
| `server.players.manage` (kick, ban and whitelist players; making one the game's admin also needs `server.console.write`; 1.0.11) | server, node, global |
| `server.files.view` `server.files.edit` | server, node, global |
| `server.backups.view` `server.backups.create` `server.backups.restore` `server.backups.delete` | server, node, global |
| `server.schedules.view` `server.schedules.edit` | server, node, global |
| `server.databases.view` `server.databases.manage` (create, delete, rotate and read passwords; Phase 13) | server, node, global |
| `server.startup.edit` (startup variables and arguments) | server, node, global |
| `server.settings.edit` (name, auto-restart, …) | server, node, global |
| `server.install` (install/update/reinstall) | server, node, global |
| `server.limits.edit` (CPU and memory caps, Phase 12; the database limit and the Steam account, Phase 13) | node, global |
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
- Listings filter by visible resources (`server.view`); requesting a resource without access returns **404** (not 403) so its existence is not revealed. A node's allocation list holds all of them, each saying whether it is in use (`inUse`); one held by a server the user cannot see leaves out that server's id, name and port name, so whoever manages the node or creates servers there knows every taken port without learning another server's id or name.
- Caps on what users add (25 API keys per user, 50 scheduled tasks and the database limit per server, 20 backup storages, Steam accounts and database hosts) are counted with the parent row locked (the user's or the server's) or, for panel-wide ones, with their creations taking turns (a PostgreSQL advisory lock): requests made at the same moment cannot all take the last place.
- *Implemented in Phase 11 (node and server assignments):* `node_access` and `server_access` as below; the evaluator loads them with the global roles. The endpoint gate checks routes under `/servers/{id}` and `/nodes/{id}` against that server (with its node) or node: a role on one server opens nothing on another, and a server or node the user cannot see at all (`server.view`; for nodes `node.view`, `node.manage` or `server.create`) answers `404` like a missing one; one they see but may not act on answers `403`. A user with no such permission anywhere gets `403` for every such route, existing or not. Lists and the create dialog's catalogs pass the gate with the permission held anywhere and show only what it applies to (servers on the user's nodes and their own servers; the nodes they may see or create servers on); creating a server checks `server.create` on its node. A node listed only because the user may create servers on it (no `node.view` or `node.manage` there or globally) comes with its name and state alone (id, name, status, connected, created): its description, public host, machine, usage, last contact, credentials and panel key trust (`unknown`) are left out, as are the notes on its allocations, and the live channel tells such users that it changed but never pushes its usage. Assignments are given and taken one by one (`PUT`/`DELETE /users/{id}/nodes/{node}/roles/{role}`, same for servers) under the same anti-escalation rules as global ones, audited, and close the user's live connections. The session carries the permissions per node and per server, so pages show only the actions the user has there. Roles count a user once however many places they hold it, and cannot be deleted while assigned anywhere.

### Anti-escalation rules

1. System role **Administrator** (`is_system = true`): always holds every permission in the catalog (computed, so new permissions are included automatically). Not editable or deletable.
2. The last global Administrator assignment cannot be removed, nor the last enabled user who holds one disabled (two
   administrators disabling each other at once leave one).
3. Holders of `role.manage`/`user.manage` **can only grant permissions they hold themselves** in that scope.
4. Nobody can change their own assignments.
5. Every role/access change is audited with the permission diff.
6. `user.manage` can only change users (edit, disable, delete, reset the password or two-factor, revoke API keys, delete SSH keys, change their global, node or server roles) who hold no permission the actor lacks where they hold it: every global one globally, every node one on that node, every server one on that server (with its node), and never themselves. A user manager can neither lock out an administrator nor reset the password of someone with a role on a node or server and sign in as them; nor strip such a user of a role both of them hold.

There is no `Admin = true`: "administrator" is just a role.

### API keys (Phase 12)

A user creates keys for scripts and tools from their account (`/api/v1/account/api-keys`, session cookie only) and sends one as `Authorization: Bearer pulsar_…`.

- A key acts as its user, with the user's permissions as they are at each request (a removed role takes effect at once), optionally narrowed to a list of permissions (`EffectivePermissions.Restrict`: the evaluator narrows the request user's permissions, so every check, list and anti-escalation rule of the request sees the smaller set). It has a name, an optional expiry, and records its last use (at most once a minute).
- Stored as the SHA-256 of its value (32 random bytes), found by that hash; the value is shown once. The first 13 characters are kept to tell keys apart. At most 25 working keys per user.
- Authentication: a policy scheme sends requests with `Bearer pulsar_…` to the key handler and all others to the cookie; the two are never merged (a cookie sent with a key is ignored). Only endpoints guarded by a permission accept keys; sign-in, the account, two-factor, the keys themselves (`RequireSignedInUser`) and the live hub take the cookie only, so a key never creates keys or changes its user's password. A key from a user who is disabled or deleted stops at once; keys of a user whose role requires two-factor authentication work only while the user has it on.
- No antiforgery token for key requests (no cookie a browser adds by itself), a rate limit per key (600 requests a minute, `RateLimiting:Auth:ApiKeyPermitLimit`; counted before authorization, so refused requests count too) checked before the key is looked up: a key that worked within the last hour counts against its own budget, any other value (a guess, a revoked or expired key, a key's first request) against its client address's budget of lookups (60 a minute, `RateLimiting:Auth:ApiKeyLookupPermitLimit`), and a request over either is refused without reading the database. Clients behind one address share only the lookups of keys unused for an hour: keys in use keep their own budgets whatever is guessed from their address, audited with the actor type `api_key` (the user's id, "user · key name").
- Revoked by their user, or all at once by an administrator (`DELETE /api/v1/users/{id}/api-keys`, `user.manage`).

### SSH keys (Phase 17)

A user adds public keys for SFTP on their account (`/api/v1/account/ssh-keys`, session cookie only, like API keys;
docs/guides/sftp.md, "SSH keys").

- A key alone signs its user in to SFTP, a two-factor user too (the key is the second factor), so adding one takes the
  current password and, with two-factor authentication on, the authenticator code (not a recovery code). Wrong ones count
  towards the lockout; the key itself is checked first.
- Stored as its blob in SSH's wire format, its type and its SHA-256 fingerprint as OpenSSH prints it, with a name (the
  key's comment when none is given), when it was added and when it last signed in. At most 10 per user, and a key on one account only (its fingerprint is unique: someone who added
  another person's public key to their own account would have that person's client sign in there). Ed25519, ECDSA
  P-256/384/521 and RSA of 2048 bits or more (signed with SHA-2); read strictly (`SshPublicKey`).
- Deleted by their user (the SFTP sessions the key signed in end) or all at once by an administrator
  (`DELETE /api/v1/users/{id}/ssh-keys`, `user.manage`), and with their user. A new password or a two-factor reset keeps
  them, as with API keys; a disabled user's keys do not sign in.

### Template upgrades (Phase 12)

A server keeps the template version it was created with (templates never change). When a newer version of its game's template exists (same key, a higher semantic version, a pre-release before its release, pre-releases by SemVer's rules: `beta.2` before `beta.10`), the server's details say so and `PUT /api/v1/servers/{id}/template` (`server.install`) moves a stopped server, with nothing else running on it, to that version: values of variables the new version no longer has are dropped, the others (and the new defaults) must still be valid (a value kept in clear for a variable the new version makes secret is encrypted by the upgrade), new required ports get an allocation, and the server is provisioned and installed again with the new manifest, its files kept (install steps such as SteamCMD only check them). Audited as `server.template_changed`. Never back to an older version.

*Template updates (1.0.10).* A server's game updates settings also say what a newer version of its game's template does (`template_policy`): nothing, shown and notified (the default), or moved to by itself. The panel knows its templates, so nothing is asked of the node: each pass of the game updates (doc 05) compares every followed, installed server's template with the newest version of its game the panel offers (the test game's only where it is enabled). A newer one is notified once per version (on the *game updates* events), and, when it is one a Pulsar update brought (bundled, not a pre-release: an imported version is the administrator's to review, and servers are moved to it by hand), moved to by itself as a sequence of origin `template_update`, once the move's own checks pass on a copy nothing saves (a move they refuse is not tried by itself again, and is notified): the game updates' warnings, stop, the move (`upgrade_template`, a step the panel's own: scheduled tasks cannot have it), start again if it ran. The move is this section's upgrade, provisioning then installing again, and the step is over once the server is installed; if that fails, the server is left as a failed install says. Each version is moved to by itself once, and automatic moves share the game updates' hour (one automatic update of either kind an hour). Setting it needs what the game updates' installing does (`server.install`, `server.stop`, `server.start`, and `server.console.write` to warn); a user can skip a move before it starts, or cancel one that runs (once the server installs again, that goes on to its end).

### Metric history (Phase 13)

The panel keeps how servers and nodes did over time without changing the agent or the protocol. `MetricHistory` (a singleton) folds every sample the agents already send (servers every 5 s, nodes every 15 s) into one-minute buckets in memory, keyed by the **panel's** clock (a node's clock is not trusted, and buckets must line up across nodes). `MetricHistoryService` writes the buckets whose minute is over every 20 s (servers' and nodes' in one transaction), recomputes the hour buckets of the last three hours (the current one included) every 5 minutes, and prunes once an hour; stopping writes the minute in progress and rolls the last hours up, so a crash loses at most that minute. The first roll-up after a start reaches back to every hour whose minutes are all still kept (from the hour after the minute retention's cutoff), so minutes written before the panel stopped or crashed reach their hour however long it was down, within the minute retention; an hour some of whose minutes were already pruned is never recomputed from the rest. A write that fails keeps its buckets for the next one for up to 30 minutes, and is retried whole (the transaction kept none of it), so nothing is counted twice. A sample's memory is capped at 64 TiB and a minute keeps at most 600 samples per server or node (agents send one every 5 or 15 s): a buggy or hostile agent cannot overflow the sums and stop every write.

Buckets hold **sums and counts** (samples, CPU sum and peak, memory sum and peak, the samples that carried a query answer and their players' sum and peak), never averages: a minute written twice (a panel restarting within it) merges by adding (`ON CONFLICT … DO UPDATE`), and the roll-up is a plain `GROUP BY`. Players are averaged over the samples that had a query answer only, so a game that does not answer leaves them empty instead of zero. Nodes keep CPU and memory used (and the machine's memory at the last sample); disks are not kept.

Retention (`MetricHistory:MinuteRetention`, `MetricHistory:HourRetention`; at least a day each): minute buckets 2 days, hour buckets 90 days. With one row per server per minute that is about 2,900 rows per server for the minutes and 2,200 for the hours. Deleting a server or a node deletes its history (cascade); samples of a server deleted meanwhile are dropped when written.

Charts (`GET /servers/{id}/metrics?range=` with `server.view`, `GET /nodes/{id}/metrics?range=` with `node.view` or `node.manage`): `hour` reads minutes one by one, `day` minutes in groups of five, `week` hours, `month` hours in groups of four, grouped by PostgreSQL with `date_bin` from a UTC origin (never the session time zone). A missing point means nothing was recorded then (the server stopped, the node was away). A server or node that does not exist answers 404 with `server.not_found` or `node.not_found`, as its other routes do.

### Addons (Phase 12)

The addons a server's game supports come from its template (doc 04, "Addons"); the panel keeps only which are installed and which release (`server_addons`), from the results of the operations that install, update and remove them (`addon_install`, `addon_remove`: the server installed and stopped, nothing else running; installing can be cancelled). A game update that reinstalls addons reports their new releases the same way. Only those three operations change the rows, and only for addons of the server's template with a non-empty release (an addon named twice: the last one counts). The installed keys go in the server's spec, so the agent keeps their lines in the game's files at every start. `GET /api/v1/servers/{id}/addons` (`server.view`), `POST` and `DELETE /api/v1/servers/{id}/addons/{key}` (`server.install`: they change the game's files); requests are audited (`server.addon_install_requested`, `server.addon_remove_requested`).

Mods from a catalog (1.0.9; doc 04, "Mods") are rows of `server_mods`, one per file the panel installed: the catalog's project and version, its name and version number, the file's path, checksum (SHA-512 from Modrinth, SHA-1 from CurseForge) and size, whether it came in as another's dependency, and the projects it requires (so a mod others need is not removed). A change writes its rows when it starts, tied to its `mods_change` operation (`installing`, `removing`), and the operation's end settles them (doc 03, "Mods"). `servers.game_version` is the game version the last install or update reported, which the catalog is asked for. A modpack is a row of `server_modpacks`: its project and version, name and version number, the server type, loader and game version it runs on, and the loader's version once the node reported it; the one an operation installs is `installing` (tied to it) and becomes the server's `installed` pack when it succeeds, replacing the previous one, together with the server's type, game version and loader version variables.

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
    status              text NOT NULL CHECK (status IN ('pending_enrollment','pending_approval','active','disabled')),
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
    enrollment_token_id uuid REFERENCES reusable_enrollment_tokens(id) ON DELETE SET NULL,  -- the reusable token that created it
    approval_required   boolean NOT NULL DEFAULT false,  -- waits for an administrator before its agent may connect
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
    created_at     timestamptz NOT NULL,         -- a pending key expires 24 h after it
    activated_at   timestamptz,
    last_used_at   timestamptz,
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

-- Phase 14: one token, many machines; each use creates a new node (docs/architecture/03, "Bulk enrollment").
CREATE TABLE reusable_enrollment_tokens (
    id                uuid PRIMARY KEY,
    name              varchar(64) NOT NULL,
    token_hash        bytea NOT NULL UNIQUE,        -- SHA-256 of 'pulsar_enroll_' + 32 random bytes
    token_prefix      varchar(32) NOT NULL,         -- the first characters, to recognize it
    max_uses          integer NOT NULL CHECK (max_uses BETWEEN 1 AND 1000),
    uses              integer NOT NULL CHECK (uses >= 0 AND uses <= max_uses),
    require_approval  boolean NOT NULL,
    created_by        uuid REFERENCES users(id) ON DELETE SET NULL,
    created_at        timestamptz NOT NULL,
    expires_at        timestamptz NOT NULL,         -- 1 hour to 90 days after creation
    last_used_at      timestamptz,
    revoked_at        timestamptz
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
    backup_limit         integer CHECK (backup_limit BETWEEN 1 AND 1000),  -- retention (7 for a new server); NULL keeps every backup
    cpu_limit_percent    integer CHECK (cpu_limit_percent BETWEEN 1 AND 100),  -- Phase 12: share of the node; NULL: no cap
    memory_limit_mb      integer CHECK (memory_limit_mb BETWEEN 128 AND 1048576),  -- Phase 12: committed memory; NULL: no cap
    backup_storage_id    uuid REFERENCES backup_storages(id) ON DELETE RESTRICT,  -- where new backups are copied; NULL: nowhere
    is_suspended         boolean NOT NULL DEFAULT false,
    auto_start           boolean NOT NULL DEFAULT false,  -- start when the agent starts
    restart_on_crash     boolean NOT NULL DEFAULT true,
    extra_arguments      text[] NOT NULL DEFAULT '{}',    -- only if the manifest allows it
    memory_limit_mb      integer,              -- reserved (Job Object limits, post-v1)
    last_exit_code       integer,
    game_version         varchar(64),          -- 1.0.9: what the last install or update reported
    last_started_at      timestamptz,
    last_stopped_at      timestamptz,
    created_at           timestamptz NOT NULL,
    updated_at           timestamptz NOT NULL
);
CREATE INDEX ix_servers_node ON servers (node_id);

CREATE TABLE server_addons (  -- Phase 12
    server_id     uuid NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
    key           varchar(32) NOT NULL,          -- the manifest's addon key
    release       varchar(128) NOT NULL,         -- the GitHub release installed
    installed_at  timestamptz NOT NULL,
    updated_at    timestamptz NOT NULL,
    PRIMARY KEY (server_id, key)
);

CREATE TABLE server_mods (  -- 1.0.9
    id              uuid PRIMARY KEY,
    server_id       uuid NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
    source          text NOT NULL CHECK (source IN ('modrinth','curse_forge')),
    project_id      varchar(64) NOT NULL,          -- the catalog's ids
    version_id      varchar(64) NOT NULL,
    name            varchar(128) NOT NULL,
    version_number  varchar(64) NOT NULL,
    path            varchar(200) NOT NULL,         -- a .jar right in a mod folder of the manifest
    hash            varchar(128) NOT NULL,         -- the catalog's checksum: SHA-512 (Modrinth) or SHA-1 (CurseForge), hex
    size            bigint NOT NULL,
    dependency      boolean NOT NULL,              -- installed because another mod requires it
    requires        jsonb NOT NULL,                -- the projects its version requires
    state           text NOT NULL CHECK (state IN ('installing','installed','removing')),
    operation_id    uuid REFERENCES operations(id) ON DELETE SET NULL,  -- the change on its way
    installed_at    timestamptz NOT NULL,
    updated_at      timestamptz NOT NULL,
    xmin            xid                            -- row version
);
CREATE INDEX ix_server_mods_server_project ON server_mods (server_id, project_id);

CREATE TABLE server_modpacks (  -- 1.0.9
    id              uuid PRIMARY KEY,
    server_id       uuid NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
    source          text NOT NULL CHECK (source IN ('modrinth','curse_forge')),
    project_id      varchar(64) NOT NULL,
    version_id      varchar(64) NOT NULL,
    name            varchar(128) NOT NULL,
    version_number  varchar(64) NOT NULL,
    server_type     varchar(64) NOT NULL,          -- an option of the manifest's type variable
    loader          varchar(64) NOT NULL,          -- the catalog's loader (fabric)
    game_version    varchar(64) NOT NULL,
    loader_version  varchar(64),                   -- as the pack's index names it, once reported
    state           text NOT NULL CHECK (state IN ('installing','installed')),  -- one of each per server at most
    operation_id    uuid REFERENCES operations(id) ON DELETE SET NULL,
    installed_at    timestamptz NOT NULL,
    updated_at      timestamptz NOT NULL,
    xmin            xid
);
CREATE INDEX ix_server_modpacks_server ON server_modpacks (server_id);

CREATE TABLE mod_catalog_settings (  -- 1.0.9, one row
    id               integer PRIMARY KEY CHECK (id = 1),
    curse_forge_key  bytea,                        -- the administrator's CurseForge API key, Data Protection; never returned
    updated_at       timestamptz NOT NULL,
    xmin             xid
);

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
-- Conflicts between 0.0.0.0:P and 192.168.1.10:P on the same node are validated in the application, with the node's row
-- locked (FOR UPDATE), so two requests at the same moment take turns.

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

CREATE TABLE ssh_keys (  -- Phase 17
    id            uuid PRIMARY KEY,
    user_id       uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    name          varchar(64) NOT NULL,
    type          varchar(19) NOT NULL,         -- ssh-ed25519, ecdsa-sha2-nistp256/384/521, ssh-rsa
    public_key    bytea NOT NULL,               -- the key's blob, SSH wire format (at most 4096 bytes)
    fingerprint   varchar(50) NOT NULL UNIQUE,  -- SHA256:… as OpenSSH prints it; a key on one account only
    created_at    timestamptz NOT NULL,
    last_used_at  timestamptz
);
CREATE INDEX ON ssh_keys (user_id);

-- ===================== Updates (Phase 19) =====================
CREATE TABLE update_state (   -- one row
    id                     int PRIMARY KEY CHECK (id = 1),
    check_automatically    boolean NOT NULL,         -- the daily look at GitHub's releases
    checked_at             timestamptz,
    check_error            varchar(500),
    latest_version         varchar(32),
    latest_manifest        varchar(65536),            -- the newest release's pulsar-update.json, as signed and verified
    latest_manifest_sha256 varchar(64),               -- what a node is told to install, and checks GitHub's file against
    updated_at             timestamptz NOT NULL
);

CREATE TABLE node_updates (   -- the last update asked of each node, as far as it went
    node_id       uuid PRIMARY KEY REFERENCES nodes(id) ON DELETE CASCADE,
    status        varchar(16) NOT NULL,   -- requested, downloading, installing, succeeded, rolled_back, failed
    from_version  varchar(32),
    to_version    varchar(32),
    error         varchar(2000),          -- what the node reported (plain text)
    requested_by  uuid REFERENCES users(id) ON DELETE SET NULL,
    requested_at  timestamptz NOT NULL,
    updated_at    timestamptz NOT NULL
);

-- ===================== Operations =====================
CREATE TABLE operations (
    id                   uuid PRIMARY KEY,
    type                 text NOT NULL CHECK (type IN (
                             'server_provision','server_install','server_update',
                             'server_delete','backup_create','backup_restore',
                             'addon_install','addon_remove','mods_change','modpack_install',
                             'files_compress','files_extract')),
                             -- a reinstall is provision + install; deleting a backup is a plain command;
                             -- the file manager's zips (1.0.11) run beside the game, as a backup does
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
    copy_mode      text CHECK (copy_mode IN ('stopped','snapshot','live')), -- how the node read the files (Phase 15; null before)
    snapshot_error varchar(64),                      -- live copy of a running game: why no snapshot (snapshot.*)
    game_save      text CHECK (game_save IN ('confirmed','waited','unconfirmed')), -- the game asked to save first
    created_at     timestamptz NOT NULL
);
CREATE INDEX ix_backups_server ON backups (server_id, created_at DESC);

-- S3-compatible storage backups are copied to (Phase 10); settings.manage.
CREATE TABLE backup_storages (
    id                 uuid PRIMARY KEY,
    name               varchar(64) NOT NULL,
    endpoint           varchar(255),                 -- https://… (http://… only to a private address); NULL for AWS S3
    region             varchar(64) NOT NULL,         -- 'auto' for R2
    bucket             varchar(63) NOT NULL,
    prefix             varchar(200) NOT NULL,        -- '' or 'folder/'
    access_key_id      varchar(128) NOT NULL,
    secret_protected   bytea NOT NULL,               -- Data Protection; sent to nodes only inside backup commands
    path_style         boolean NOT NULL,
    created_at         timestamptz NOT NULL,
    updated_at         timestamptz NOT NULL
);
-- A storage that servers use or backups have copies in cannot be deleted, and while backups have copies in it its
-- endpoint and bucket stay (backups keep only their copy's key, prefix included): credentials, region, path style and
-- the prefix of new copies may change (backup_storage.holds_copies otherwise).

-- ===================== Steam accounts (Phase 13) =====================
CREATE TABLE steam_accounts (
    id                  uuid PRIMARY KEY,
    name                varchar(64) NOT NULL UNIQUE,
    user_name           varchar(64) NOT NULL,        -- the Steam account name
    password_protected  bytea NOT NULL,              -- Data Protection; sent to nodes only inside install/update commands
    created_at          timestamptz NOT NULL,
    updated_at          timestamptz NOT NULL
);
-- servers.steam_account_id uuid REFERENCES steam_accounts(id) ON DELETE RESTRICT: NULL signs in anonymously.

-- ===================== Game databases (Phase 13) =====================
CREATE TABLE database_hosts (
    id                  uuid PRIMARY KEY,
    name                varchar(64) NOT NULL UNIQUE,
    host                varchar(255) NOT NULL,        -- where the panel connects
    port                integer NOT NULL CHECK (port BETWEEN 1 AND 65535),
    user_name           varchar(80) NOT NULL,         -- may create databases and users
    password_protected  bytea NOT NULL,               -- Data Protection; never returned
    server_address      varchar(255),                 -- where game servers connect, when not host
    server_port         integer CHECK (server_port BETWEEN 1 AND 65535),
    user_host           varchar(255) NOT NULL,        -- the users' host part: '%' or an address/pattern
    node_ids            uuid[] NOT NULL,              -- nodes whose servers may use it; empty: all
    require_tls         boolean NOT NULL,             -- refuse to connect without TLS (else TLS when offered)
    created_at          timestamptz NOT NULL,
    updated_at          timestamptz NOT NULL
);
CREATE TABLE server_databases (
    id                   uuid PRIMARY KEY,
    server_id            uuid NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
    host_id              uuid NOT NULL REFERENCES database_hosts(id) ON DELETE RESTRICT,
    name                 varchar(64) NOT NULL,        -- s{short id}_{suffix}; also its user's name
    user_host            varchar(255) NOT NULL,       -- the host's setting when it was created
    password_protected   bytea NOT NULL,
    created_at           timestamptz NOT NULL,
    password_changed_at  timestamptz NOT NULL,
    UNIQUE (host_id, name)
);
-- servers.database_limit integer NOT NULL DEFAULT 2 CHECK (database_limit BETWEEN 0 AND 50).
-- A row is written (with the server's row locked) before the database is created on its host, and removed again if that fails.
-- A server's databases are dropped on their hosts when its node confirms its deletion, before its row goes. The
-- server's row stays locked (one transaction) from listing its databases until the row is gone, so a database whose
-- host was still creating it is dropped again by its creation and none is missed. The price: while a host does not
-- answer (up to 15 s per host, each tried once), that server's state events and database creations wait. Only the
-- server being deleted is held, and its node already deleted it.
-- A backup row is written with its operation; the operation's result completes it (an interrupted one fails it).
-- Deleting a server deletes its rows (and the node its archives).

-- ===================== Scheduling =====================
CREATE TABLE scheduled_tasks (
    id               uuid PRIMARY KEY,
    server_id        uuid NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
    name             varchar(128) NOT NULL,
    cron_expression  varchar(128) NOT NULL,          -- 5 fields, parsed with Cronos
    time_zone        varchar(64)  NOT NULL,          -- IANA, e.g. 'America/Argentina/Buenos_Aires'
    action_type      text NOT NULL CHECK (action_type IN ('start','stop','restart','backup','update','console_command','sequence')),
    action_payload   jsonb NOT NULL DEFAULT '{}',    -- {"command": "say …"} for console_command, {"validate": true} for update,
                                                     -- {"steps": [{"kind": "broadcast", "text": "…"}, {"kind": "wait", "seconds": 240}, …]} for sequence (1.0.9)
    only_if_running  boolean NOT NULL DEFAULT false, -- restarts, backups and sequences (console commands always need it)
    only_if_empty    boolean NOT NULL DEFAULT false, -- 1.0.9: nobody plays, as the game's query says (not for start, console_command)
    only_if_update_available boolean NOT NULL DEFAULT false, -- 1.0.9: the game update checks found one (update, sequence)
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
    sequence_id   uuid REFERENCES server_sequences(id) ON DELETE SET NULL, -- 1.0.9, a sequence: running until its steps end
    occurrences   integer NOT NULL CHECK (occurrences >= 1), -- runs missed while the panel was down share one row
    error_code    varchar(64),                      -- schedules.missed, schedules.not_running, schedules.not_empty, server.busy…
    message       varchar(1024)
);
CREATE INDEX ix_task_runs_task ON scheduled_task_runs (task_id, started_at DESC);
CREATE INDEX ix_task_runs_running ON scheduled_task_runs (started_at) WHERE status = 'running';
-- The last 100 runs of each task are kept.

-- 1.0.9: steps done one after the other on a server (an automatic game update, a scheduled task of several steps; doc 05).
CREATE TABLE server_sequences (
    id                      uuid PRIMARY KEY,
    server_id               uuid NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
    origin                  text NOT NULL CHECK (origin IN ('schedule','game_update','template_update')),
    name                    varchar(128) NOT NULL,      -- who acts in the audit log
    steps                   jsonb NOT NULL,             -- [{"kind":"broadcast","text":"…"},{"kind":"wait","seconds":50},{"kind":"stop"},…]
    step_index              integer NOT NULL,
    step_begun              boolean NOT NULL,           -- the step acted and waits for its outcome
    step_started_at         timestamptz,
    wake_at                 timestamptz NOT NULL,       -- when the runner looks at it again
    operation_id            uuid REFERENCES operations(id) ON DELETE SET NULL,
    observed_state_sequence bigint,                     -- the server's state as the step began
    observed_state_at       timestamptz,
    was_running             boolean NOT NULL,
    status                  text NOT NULL CHECK (status IN ('running','succeeded','failed','cancelled')),
    error_code              varchar(64),
    message                 varchar(1024),
    created_at              timestamptz NOT NULL,
    cancelled_at            timestamptz,                -- a user cancelled it: the steps that always run still do
    finished_at             timestamptz
);
CREATE INDEX ix_server_sequences_due ON server_sequences (wake_at) WHERE status = 'running';
CREATE UNIQUE INDEX ix_server_sequences_running ON server_sequences (server_id) WHERE status = 'running';
-- The last 20 that ended are kept per server.

-- ===================== Game updates (1.0.9) =====================
CREATE TABLE game_update_watches (
    server_id          uuid PRIMARY KEY REFERENCES servers(id) ON DELETE CASCADE,
    policy             text NOT NULL CHECK (policy IN ('off','notify','install')),
    template_policy    text NOT NULL CHECK (template_policy IN ('off','notify','install')),  -- 1.0.10: newer template versions
    when_empty         boolean NOT NULL,
    warning_minutes    integer NOT NULL CHECK (warning_minutes BETWEEN 0 AND 60),
    warning_message    varchar(100),                -- with {time}; NULL: the default
    installed          varchar(64),                 -- a Steam build id, a Minecraft version
    latest             varchar(64),
    available          boolean,
    checked_at         timestamptz,
    check_error_code   varchar(64),
    check_error        varchar(300),
    check_requested_at timestamptz,                 -- stored before the node is asked: an answer nobody asked for is dropped
    next_check_at      timestamptz NOT NULL,
    notified_latest    varchar(64),
    notified_at        timestamptz,                 -- at most one notification an hour
    attempted_latest   varchar(64),                 -- a build is installed by itself once
    notified_template  varchar(64),                 -- 1.0.10: each newer template version is notified once
    attempted_template varchar(64),                 -- and moved to by itself once
    sequence_id        uuid REFERENCES server_sequences(id) ON DELETE SET NULL,  -- a game update or a move to a newer template
    auto_updated_at    timestamptz,                 -- automatic updates start at most once an hour
    updated_at         timestamptz NOT NULL
);
-- Made the first time a server is installed (or its settings saved); checked and notified by default.

-- ===================== Notifications (Phase 10) =====================
CREATE TABLE notification_channels (
    id                    uuid PRIMARY KEY,
    name                  varchar(64) NOT NULL,
    kind                  text NOT NULL CHECK (kind IN ('discord','email')),
    is_enabled            boolean NOT NULL,
    events                text[] NOT NULL,          -- server_crashed, server_start_failed, backup_failed, scheduled_task_failed, node_offline, node_disk_low
    server_ids            uuid[],                   -- server events only for these; NULL = every server
    language              varchar(8) NOT NULL,      -- en | es | pt | fr | it | de | ja | ko | zh
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
-- A sent or failed delivery stays so: a sender whose 2-minute lease ran out does not save its outcome over the one of
-- the sender that took it next (the row version, xmin). Delivery is at least once: a send slower than the lease can be
-- repeated by the next sender, so the message may arrive twice.

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

-- ===================== Metric history (Phase 13) =====================
CREATE TABLE server_metrics (
    server_id       uuid NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
    resolution      smallint NOT NULL CHECK (resolution IN (1, 60)),   -- minutes per bucket
    at              timestamptz NOT NULL,                              -- start of the bucket (UTC)
    samples         integer NOT NULL,
    cpu_sum         double precision NOT NULL,
    cpu_max         double precision NOT NULL,
    memory_sum      bigint NOT NULL,
    memory_max      bigint NOT NULL,
    player_samples  integer NOT NULL,                                  -- samples with a query answer
    players_sum     bigint NOT NULL,
    players_max     integer,
    PRIMARY KEY (server_id, resolution, at),
    CHECK (samples > 0 AND player_samples BETWEEN 0 AND samples)
);
CREATE INDEX ix_server_metrics_resolution_at ON server_metrics (resolution, at);  -- pruning
-- node_metrics: the same per node, with memory_used_sum/max and memory_total instead of memory and players.

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

- Individual samples (memory only; the metric history keeps one-minute and one-hour aggregates).
- Console output (ring buffers and rotated log files on the node).
- Backup archives (on the node).
- The game databases themselves (on their MySQL/MariaDB hosts): Pulsar keeps only their names, hosts and encrypted passwords.
