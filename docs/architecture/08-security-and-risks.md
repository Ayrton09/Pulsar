# 08 — Security model and main risks

> Status: **proposal under review**.

## Trust boundaries

```
[Internet / players] ──UDP/TCP──► game servers (per-server account, Job)
[Browser] ──HTTPS + cookie──► Panel ──► PostgreSQL
                                 ▲
[Agent (SYSTEM)] ──outbound WSS, PoP + signed commands──┘
[Plugins / mods / uploaded files] = UNTRUSTED code inside the game server
```

Explicit assumptions:

1. **The panel is the control plane.** Whoever controls the panel with administrator permissions controls the nodes *within the supported operations*. Pulsar limits what the panel can ask for, but cannot protect a node from a fully compromised panel.
2. **A game server runs untrusted code** (SourceMod/CounterStrikeSharp plugins, GMod Lua, uploaded DLLs). Anyone who can upload files and start a server can execute code **as that server's account**. This is inherent to game hosting; the goal is that this code cannot leave its box (account + ACL + Job).
3. The node's local administrator is fully trusted.

## Controls per layer

### Users and web

- ASP.NET Core Identity: password hashing (Identity's default PBKDF2), lockout (5 failures, 15 minutes), security stamp, TOTP 2FA (enforceable per role in settings). Recovery codes.
- *Implemented in Phase 9:* two-factor authentication with any authenticator app (RFC 6238: HMAC-SHA1, 30-second steps, six digits, one step of drift either way). A code works once: the last accepted step is kept, so a code seen over someone's shoulder cannot be reused within its window. The authenticator key is encrypted with Data Protection and the ten recovery codes (about 47 random bits each) are kept as SHA-256 hashes; a recovery code works once. Wrong codes count towards the lockout like wrong passwords. A role can require it (the Administrator role too): until such a user sets it up, every endpoint but the session, logout and the setup itself answers `403 auth.two_factor_required`, and so does the live channel. Setting it up (a new authenticator key, which also ends the user's other sessions) needs the password, and turning it on needs a code from the app and the password (a stolen session alone cannot reset the key and end the owner's sessions, nor turn it on, lock the owner out and keep the recovery codes); turning it on renews the security stamp (other sessions end); turning it off needs the password and is refused while a role requires it; an administrator who may manage the user can turn it off for them (a lost phone), which ends their sessions. All of it is audited.
- Confirming the current password (changing it, setting up, turning on or off two-factor authentication, new recovery codes) counts wrong guesses towards the lockout, so a stolen session cannot be used to guess the password. Locked-out and disabled accounts take as long to answer as wrong passwords (Identity answers them before hashing; the panel hashes anyway). Residual risk: anyone can lock an account for 15 minutes with five wrong passwords (the rate limit slows it down; locking out the only administrator is possible).
- Protected initial setup: when no users exist, `/setup` requires a **single-use token printed in the panel log/console** at startup (prevents someone else on the network from creating the first admin before you). Automated installations can provide it instead with `InitialSetup:Token` (at least 32 characters; then it is never printed).
- **HTTPS by default.** Cookies are `__Host-` prefixed, `HttpOnly; Secure; SameSite=Strict`; HSTS and HTTP→HTTPS redirection are on, and API calls over plain HTTP get `400 transport.https_required`. TLS can terminate at Kestrel or at a reverse proxy: `X-Forwarded-For/Proto` are trusted from loopback and from `Security:TrustedProxies`. Plain HTTP is only possible with the explicit opt-in `Security:AllowInsecureHttp=true` (development, trusted networks): cookies lose `Secure` and the prefix, the panel logs a warning at startup and the UI shows a permanent banner.
- Antiforgery token (readable `__Host-pulsar-xsrf` cookie, `X-XSRF-TOKEN` header) on every state-changing API call.
- Headers on every response, errors included (set when the response starts, after the exception handler has replaced it): strict CSP (`default-src 'self'`, no inline scripts; inline `<style>` elements only, because the dialogs' scroll lock injects one at runtime — style attributes stay blocked, `object-src 'none'`, `base-uri 'none'`, `frame-ancestors 'none'`), `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, a restrictive `Permissions-Policy` and `Cross-Origin-Opener-Policy: same-origin`. The end-to-end suite fails on any CSP violation.
- Users change their own profile and password (`/account`); a password change ends the user's other sessions.
- Built-in rate limiting (`Microsoft.AspNetCore.RateLimiting`) on login and its second step, setup, password confirmations, enrollment and the agent challenge.
- Request bodies are limited to 2 MiB (Kestrel's default is 30 MB): the endpoints that take files (uploads, the agent's transfers) raise it for themselves.
- Resource authorization on **every** endpoint (enforced by a test) and hub method; IDs of other users' resources → 404 (in the browser hub: `null`).
- The browser hub (`/hubs/panel`) requires a signed-in user, WebSockets only, and refuses an `Origin` other than the panel's own host (cross-site WebSocket hijacking; SameSite cookies are per site, not per origin). Permissions are checked when a page starts watching. A WebSocket outlives the cookie checks of ordinary requests, so (*Phase 9*) the panel closes a user's live connections when they are disabled or deleted, their password or two-factor setup changes or their roles change, and closes every connection when a role changes; connections reopen on their own and are checked again, and a connection only opens for a session whose security stamp is still current (checked at once, not at the cookie's next one-minute check).
- RBAC anti-escalation (doc 02).
- *Phase 11:* roles given on one node or one server. The gate resolves the server or node of the route and checks the permission there, answering `404` for what the user cannot see; lists are filtered. Live notices follow the same rule: watchers with global roles share the `servers` and `nodes` groups, others join groups per node and per server they see, and the panel sends each server's and node's notices and usage to those groups only, so nobody learns the ids of servers they cannot see. Changing a user's assignments closes their live connections, which reopen with what they may see now.

### Panel ↔ Agent

- Mandatory TLS: the agent rejects `http://` panel addresses other than loopback ones (a panel on the same machine; that traffic never leaves it).
- Per-node identity with a non-exportable key (TPM when available), proof of possession through a challenge, an opaque 15-minute session token (the panel keeps only its hash), immediate revocation, rotation.
- Commands signed by the panel, pinned panel key, anti-replay (session + sequence + expiry), strong validation before execution.
- There is no shell command. The executable always comes from the manifest and the server directory.

### Node

- Agent without listening ports.
- Per-server accounts without interactive/RDP/network logon; the game runs with a restricted token without privileges (no `SeShutdownPrivilege`). The token still contains `BUILTIN\Users`, so isolation comes from ACLs, not group membership. Residual risk: files a server writes into Users-writable system locations (`C:\ProgramData`, `C:\Users\Public`) are readable by the other servers.
- ACLs: each server only sees its directory; agent directories (`%ProgramData%\Pulsar\Agent`) are SYSTEM/Administrators only.
- Jobs with UI restrictions and no breakaway.
- Explicit inherited handles (`PROC_THREAD_ATTRIBUTE_HANDLE_LIST`).
- RCON (Source servers in `logfile+rcon` mode) is a credential exposed on the game's TCP port: the agent generates and keeps a long random password per server (T18).
- SteamCMD runs as the server's own account, from the server's own copy (doc 06): it can write only where its server can.
- Processes a server's account starts outside its Job (scheduled tasks, WMI) are killed at every start, stop, install and deletion (T20).
- Left as they are, on purpose (audit WL2, CL6):
  - `Pulsar:TestGameDirectory` (the test game's files, doc 04) is honoured in every build, Release included: CI's agent-install job runs a Release production service with it, and for the service only administrators can set it (the settings file in the agent's install folder, which `ServiceBinarySecurity` checks only administrators can modify, or the machine's environment), so it gives nobody more than they already have.
  - A SteamCMD beta password (`-betapassword`) goes on SteamCMD's command line, where processes of the same account can read it: SteamCMD runs as the server's own account (doc 06), which could read a script file holding it just the same (as could administrators), so a file would protect nothing more.

### File manager (critical)

The agent's file manager runs in a SYSTEM process operating on directories **writable by untrusted code**. Defense in depth:

1. **Lexical validation** of the received relative path: reject roots, drive letters (`C:`), UNC (`\\server`), device paths (`\\?\`, `\\.\`), `..`, `:` (alternate data streams), reserved names (`CON`, `NUL`, `AUX`, `PRN`, `COM1-9`, `LPT1-9`, also with an extension), trailing dots/spaces, control characters, excessive lengths.
2. **Normalization** with `Path.GetFullPath(Path.Join(root, rel))` and a prefix check including the trailing separator (`C:\srv\a\` must not accept `C:\srv\ab`) using an ordinal case-insensitive comparison.
3. **Reparse points**: game code can create a *junction* (no privileges required) pointing at `C:\`. Every operation first opens a handle and checks with `GetFinalPathNameByHandle` that the real target is still inside the root, then operates **on that handle** (no TOCTOU between check and use). Enumeration does not follow reparse points; they are shown as non-navigable links.
4. **Impersonation**: file operations run impersonating the server account (`WindowsIdentity.RunImpersonated` with the account token). Even if layers 1–3 failed, the OS enforces that account's ACLs: it cannot read outside its directory. This also neutralizes hard links to foreign files (the ACL lives on the file, not on the link).
5. Limits: inline read/edit size (1 MiB), configurable upload size, optional disk quota.
6. Atomic writes (temp file + rename) and auditing of deletions/renames/uploads.

Path traversal tests: an extensive table of malicious inputs (more than 50 Windows-specific cases) + integration tests with real junctions, symlinks and hard links.

*Implemented in Phase 6.* Layer 1 is `ServerFilePath` (Pulsar.Contracts), checked by the panel and again by the agent: forward slashes only, no roots, drives, UNC or device paths, no `..`/`.`/empty segments, no `:`, no reserved device names (with or without an extension, `COM0-9`, `LPT0-9` and their superscript forms included), no trailing dots or spaces, no leading spaces, no control characters, wildcards or broken UTF-16, segments up to 255 characters, paths up to 1,024 (`ServerFilePathTests`: 79 refused inputs and 33 plain ones). Layers 2–4 are described in doc 06 ("File manager"); the inline limit is 1 MiB, uploads are limited by `Files:MaxUploadBytes` (1 GiB by default), writes are atomic and changes and downloads are audited. `WindowsServerFileManagerTests` plant real junctions (at the top and deeper in a path, to the outside and inside), symbolic links to files and folders, and hard links to a file outside, and check that nothing outside is ever read, written, renamed or deleted; as SYSTEM in CI, the same checks run as a server's account, where another server's file stays unreadable even through a hard link. A shared contract (`ServerFileManagerContract`) runs the same behavior tests on the Windows implementation and on the portable one the in-process tests use.

### Backups and restore

- Creation: does not follow reparse points (otherwise a junction to `C:\Users` would exfiltrate data into the archive); reads impersonating the server account.
- Restore: every archive entry goes through the same validator (anti zip-slip), no reparse points are created, writes are impersonated, only while the server is `Offline`.
- Backup downloads require `server.backups.view` and go through a transfer ticket.

*Implemented in Phase 7.* Creation walks the server directory through the file manager (links are listed, never entered) and reads every file through it, so on Windows the server's own account opens them: a hard link to another server's file is left out (counted as skipped), a junction to `C:\Users` adds nothing. The archives are written by the agent into its own directory (SYSTEM and Administrators only), out of every server's reach; each one's SHA-256 is kept by the panel. A restore first checks that SHA-256, then checks every entry before writing anything: each name must be a plain `ServerFilePath` (no `..`, roots, drives, `\`, `:` streams, device names…), outside Pulsar's own `.pulsar` folder and not repeated (ignoring case); any other archive is refused whole (`backups.corrupt`). Entries are written through the file manager as the server's account, one temporary file and one rename each, never through a link (a junction planted where a folder of the backup goes stops the restore), and only with the server stopped. *Remote copies (Phase 10):* the node uploads the archive itself (the panel never sees backup bytes) and, when it fetches one back, checks the SHA-256 the panel recorded before using it, so a copy changed in the bucket is refused. The storage's keys travel inside the signed command over the agent's TLS connection and are dropped after use; anyone with administrator rights on a node can still see them while a backup runs, so the guide recommends keys scoped to the one bucket. The agent reaches the storage over HTTPS; plain HTTP is accepted only to a loopback, private or link-local address (127/8, 10/8, 172.16/12, 192.168/16, 169.254/16, ::1, fc00::/7, fe80::/10: storage on the node or on the owner's LAN, the way AMP users keep a NAS at home), so backup bytes and signed requests never cross the internet in clear. The same rule holds on both sides: the panel refuses an `http://` endpoint whose host is a public IP literal (`backup_storage.endpoint_needs_https`) and accepts a host name, which it cannot resolve as the node would; the node resolves the name once per client, refuses it if any address is public (`RemoteFailed`, saying which), and connects only to the addresses it checked, so a later DNS answer cannot redirect it. `BackupStorageTests` and `RemoteBackupStoreTests` cover both. `BackupStoreTests` (ten hostile entry names and more), `WindowsServerFileManagerTests` (junctions to the outside, as the developer and as a server's account in CI) and `ServerBackupTests` (the panel and the agent core end to end) cover it.

### Game databases

*Phase 13.* Database hosts are panel settings (`settings.manage`): their account may create databases and users, so it is a secret of the panel's, checked by connecting before a host is saved. A server's databases are named `s{short id}_{suffix}`: the suffix is the owner's, 1 to 20 lowercase letters, digits and underscores, checked by the domain and again by the MySQL client before it is quoted into the statement (DDL takes no parameters); passwords are 24 random letters and digits. Each database gets a user of the same name with `ALL PRIVILEGES` on it alone. Creating one never adopts a database that already exists, and removes what it made when a step fails; the server's limit is checked with its row locked. Deleting a server drops its databases on their hosts when its node confirms the deletion, before the row goes; a host that cannot be reached does not hold the deletion up, and the audit log names the database left behind. Connections use TLS when the server offers it (`SslMode=Preferred`), no pooling and 15-second timeouts.

### Scheduled tasks

*Implemented in Phase 8.* Tasks run in the panel as the schedule: each action goes through the same service a user's request does, so its rules and audit apply, recorded with actor type `schedule`, the task's name and the run's id as correlation id. A task does later what its author set up, without them, so setting one up (creating, changing, running it now) needs the permission to do its action besides `server.schedules.edit` (`server.restart`, `server.backups.create`, `server.install` for updates, `server.console.write` for commands…), and changing one also needs the permission for the action it had: scheduling cannot be used to do what the user could not. Runs do not check their author again (a task belongs to its server, and whoever can edit schedules can disable it): taking a user's rights away does not stop the tasks they made. Scheduled commands are kept in clear and shown to whoever can see the server's schedules, so commands starting with one of the game's sensitive prefixes (`rcon_password`…) are refused. Due tasks are taken with `FOR UPDATE SKIP LOCKED`, so two panels never run one twice (`ServerScheduleTests` runs three passes at once).

### Notifications

*Implemented in Phase 10.* The panel posts only to Discord's own webhook addresses (`https://discord.com/api/webhooks/{id}/{token}` and its `discordapp.com`, `ptb.` and `canary.` hosts, default port, no user info), checked when saved and again before every post, and its client follows no redirects: a channel cannot be used to make the panel call a machine inside its network. Messages allow no mentions (`allowed_mentions` empty), so a server name cannot ping `@everyone`. The webhook client logs no URLs (the token is in the path). Email goes through the SMTP server an administrator sets (`settings.manage`); STARTTLS is required when chosen and certificates are checked; signing in needs STARTTLS or TLS (no encryption only for a server on the panel's own machine), and the saved password is kept only while the server and the user name stay the same (it is never sent to another server); addresses are plain addresses (no display names or lists, so nothing can add headers). Messages are written in the same transaction as the event that raised them (an outbox) and sent by the panel with retries; what they carry (server, node and task names, exit codes, an error message) is what `settings.manage` holders could see in the panel anyway.

### Manifests

Privileged import, closed steps, time-bounded regexes, typed variables, argv without a shell, per-format configuration encoders (doc 04).

### Secrets

| Secret | Where | Protection |
|---|---|---|
| User passwords | `users.password_hash` | Identity hash |
| Secret variables (GSLT, RCON) | `server_variables.protected_value` | ASP.NET Core Data Protection |
| Panel signing key | `panel_signing_keys` | Data Protection |
| Data Protection key ring | Panel disk | A certificate (`DataProtection:CertificatePath`, a PFX): what the installer sets up (`keyring\keyring.pfx`, its password in the panel's settings, both readable only by SYSTEM, Administrators and the panel's service account), so a backup restores on another machine. Without one: DPAPI (LocalMachine) on Windows, only the directory's permissions elsewhere |
| Authenticator keys / recovery codes | `user_tokens` | Data Protection / SHA-256 hashes |
| Backup storage secret keys | `backup_storages.secret_protected` | Data Protection; never returned by the API; sent to a node only inside the backup command that needs it, never stored or logged there |
| Steam account passwords | `steam_accounts.password_protected` | Data Protection; never returned; sent to a node only inside the install, update and addon-removal commands of servers that use the account, never stored there |
| Logins SteamCMD cached for a Steam account | Node: `state\steam-logins\*.bin` | DPAPI (machine scope) in the agent's state directory; lent to a server's SteamCMD for one run, taken back and deleted from the server's folder after it; the file manager refuses that folder |
| Database host passwords, game database passwords | `database_hosts.password_protected`, `server_databases.password_protected` | Data Protection; a host's password is never returned; a game database's password is returned only to holders of `server.databases.manage` (when created, rotated or asked for), never listed. The panel connects to the host itself: nodes never see either |
| Discord webhook URLs, SMTP password | `notification_channels.webhook_url_protected`, `notification_settings.smtp_password_protected` | Data Protection; the API shows a webhook's id and whether a password is set, never the values |
| Node private key | CNG/TPM key store | Non-exportable |
| Specs with secrets on the node (secret variables, RCON password) | `state\servers\*.json` | ACL (SYSTEM + Administrators) and the whole record encrypted with DPAPI (machine scope) |
| Server account passwords | Nowhere | A new random one is set right before every logon and used once (doc 06) |
| Enrollment tokens / tickets | Database | SHA-256 only |

- APIs return secrets as `"***"` (write-only). Log messages never destructure objects (no `{@…}`) and never include secrets, command lines or request bodies; request logs have the method, path, status and time, never query strings, headers or cookies. Console commands are audited with sensitive commands redacted (each `;`-separated command of a line is checked).
- Accepted limitation: a secret variable a manifest writes into a configuration file (the join password into CS2's `server.cfg`) can be read by whoever may read the server's files (`server.files.view`) or download its backups. The API keeps it write-only; the game needs it in the file.
- **Important operational consequence:** a PostgreSQL backup without the Data Protection key ring cannot decrypt secrets. `Backup-Pulsar.ps1` keeps the database dump, the key ring, its certificate, the HTTPS certificate and the settings together, and `Install-Pulsar.ps1 -RestoreFrom` restores them together ([backup guide](../guides/backup.md)).
- *Packaging (Phase 9):* the panel runs as the `PulsarPanel` service under its own virtual account (`NT SERVICE\PulsarPanel`), not as SYSTEM: it can write only its data folder (`%ProgramData%\Pulsar\Panel`: keys, key ring, logs, certificate) and read its settings, which hold the database and certificate passwords and are readable only by SYSTEM, Administrators and that account. The installer creates the panel's PostgreSQL role with a random password and never stores the superuser's. Releases are built in CI from a tag, with a provenance attestation and a SHA-256 checksum; the install guide tells users to check it.
- Accepted limitation: a secret passed on the command line (e.g. GSLT with `+sv_setsteamaccount`) is visible to the node's local administrators. If a game accepts it from a file, the manifest should prefer that.

### Audit

Append-only from the application, no secrets, with IP and actor. Failed sign-ins are recorded one by one up to `Audit:FailedLoginsPerMinute` (60) a minute; beyond that they are counted and the next recorded one says how many were left out (*Phase 9*). Minimum actions: successful/failed login, logout, 2FA changes, user/role/access created-changed-deleted, node created/enrolled/revoked/rotated, server created/deleted, start/stop/restart/kill, console command (redacted), file edited/uploaded/deleted/renamed, backup created/restored/deleted/downloaded, scheduled task created/changed and its runs, template imported, settings changed.

### Supply chain and releases

- Central Package Management + NuGet lock files, `npm ci` with a lockfile, Dependabot, CodeQL, dependency review on pull requests.
- Releases built in CI with provenance attestations (GitHub artifact attestations) and published SHA-256 checksums. Authenticode signing of Windows binaries once the project has a certificate (SmartScreen will appreciate it).
- No agent auto-update in v1: an auto-update channel is a remote code execution channel and needs its own design (package signatures verified by the agent).

## Threats (STRIDE summary)

| # | Threat | Main mitigation |
|---|---|---|
| T1 | Impersonating an agent with a stolen key | Non-exportable/TPM key, immediate revocation, visible fingerprint, rotation |
| T2 | Leaked enrollment token | Single use, 30 min, hashed in the database, node visible with hostname/fingerprint |
| T3 | MITM or compromised proxy injects commands | Signed commands + pinned panel key |
| T4 | Command replay | Session + sequence window + expiry + idempotency by CommandId |
| T5 | Argument/console command injection through variables or extra arguments | Typing, mandatory regex, no leading `-`/`+`, argv without a shell, encoders; extra start arguments may not repeat a switch or command the manifest sets (`-port`, `+rcon_password`…), set a sensitive setting or, on RCON consoles, anything about RCON or a `;` (*Phase 9*) |
| T6 | Path traversal / junctions / ADS / devices | Lexical validation + handle final path + impersonation + ACL |
| T7 | Zip slip on restore | Per-entry validator |
| T8 | Malicious plugin escalates to SYSTEM, reaches other servers or shuts the node down | Per-server account, ACL, Job, restricted token without privileges |
| T9 | Malicious manifest | Privileged import, closed steps, capability review |
| T10 | RBAC privilege escalation / IDOR | Single per-resource evaluator, anti-escalation, authorization matrix tests; node and server roles checked against the route's resource by one gate, other resources answered 404 (`ScopedAccessTests`) |
| T11 | XSS / CSRF / clickjacking | Escaped React, CSP, SameSite + antiforgery, frame-ancestors |
| T12 | Login brute force | Lockout, rate limiting, 2FA |
| T13 | Malicious escape sequences in the console | xterm without the clipboard addon (no OSC 52), http/https links only (OSC 8 too) opened with `noopener`, terminal input not wired to the game (title and query answers go nowhere) |
| T14 | DoS via console flood/uploads | Coalescing (≤ 64 KiB and ≤ 20 messages/s per server), bounded buffers (agent ~1 M characters, panel 256 K, browser 5,000 lines), output sent only while someone watches, 32 KiB limit on browser messages |
| T15 | Secret leakage in logs/audit | Redaction, write-only secrets, sensitive console prefixes |
| T16 | First admin created by a third party | Locally printed setup token |
| T17 | Repudiation | Audit with actor, IP, correlation |
| T18 | RCON brute force on the game's TCP port (`logfile+rcon` servers) | Agent-generated random password per server (32 letters and digits, never user-chosen), RCON ban settings where the game has them (srcds; CS2 has none, spike 0001), password rotated on reinstall; game ports declare UDP only, so firewall rules managed by Pulsar never open RCON's TCP side |
| T19 | A server redirects the agent's own writes and reads (installs, configuration files, its log, deletion, run as SYSTEM in its folder) with junctions, symbolic links or hard links | Files opened without following a final link and checked by the final path of the handle; writes replace files instead of writing into them; deletion removes links without following them; SteamCMD runs as the server's account (doc 06) |
| T20 | A server escapes its Job (a scheduled task or WMI starts a process as its account) to keep running after a stop or a deletion | Processes of the account are killed before every start, after its processes end, before installs and before deletion; logged as warnings. Residual: such a process runs, without Job limits, while its server runs |
| T21 | A tampered or redirected download replaces a game's server or its Java runtime (`minecraftServer` step) | HTTPS only, to Mojang's, Adoptium's and GitHub's hosts, with every redirect checked by the agent; each file must match the checksum its publisher lists in metadata fetched over that TLS; archives cannot write outside `java/`; size limits on metadata, downloads and extraction |
| T22 | A game or a player injects markup, terminal sequences or misleading text through the query answer (server name, map, player names) | Read with fixed limits (packets, sizes, 256 players); control characters, direction overrides and colour codes removed and texts cut to 128 characters by the agent and again by the panel (a node is not trusted with what browsers show); rendered as text by React |
| T23 | One server starves the others on its node (a runaway game, a mod, an owner raising the slots) | Optional CPU and memory caps per server, enforced by its Job and applied at once; set with `server.limits.edit`, which a server-scoped role cannot hold, so an owner cannot raise their own; a crash at the memory cap is reported as such; an agent that cannot enforce them is refused the caps, and a server with caps (set while its node was away) does not start on it (`server.limits_unsupported`). Residual: no network or disk I/O caps |
| T24 | A leaked API key is used against the panel, or a key is used to take over its user's account | The value is shown once and stored hashed; a key can be narrowed to some permissions and given an expiry; it never reaches sign-in, password, two-factor, the account or key management, nor the live hub; revoked by its user or all at once by an administrator, and stopped at once by a disabled user; requests are audited as the key and rate limited per key, refused ones included (unknown, expired or revoked keys per client address). Residual: a key without an expiry and with all its user's permissions is as powerful as the user's session until revoked |
| T25 | An addon download is tampered with or redirected, or a manifest points at a malicious repository, to run code as a game server | The agent installs only addons its manifest names, from GitHub's API and download hosts over HTTPS with every redirect checked; the asset must match the SHA-256 GitHub publishes; zip entries cannot leave the addon's target folder and sizes are capped; the panel sends only a key. Importing a manifest with addons trusts their repositories, like trusting its executables (`template.manage`). Residual: an addon's authors (or a compromised account of theirs) can publish a malicious release, which runs with the server's account, inside its Job |
| T26 | The Workshop lookup is used to make the panel send requests elsewhere, or a Workshop author's title misleads users | The panel asks only `api.steampowered.com` over HTTPS, without redirects, with the ids checked as numbers (at most 100 a request) and the answer's size capped; only users who can create servers or change their startup settings may ask. Titles lose control and direction characters, are cut to 128 characters and are rendered as text; no images are loaded |
| T27 | A server's owner reaches other servers' data or takes over the database host through their databases | Names and passwords never come from free text: a strict pattern, checked twice, then quoted; each user is granted its own database only; host accounts never leave the panel. Residual: whatever a host itself grants every user (MariaDB's default `test` database, for one) is the host's; the guide says to remove it. `%` lets the users connect from anywhere the host can be reached: narrow it per host (*Connections from*) or with a firewall |
| T28 | A Steam account is stolen through a server: its owner, a mod or the game reads the login SteamCMD cached, or the password | The cached login is in a server's folder only while SteamCMD runs (the game is stopped then) and is never readable through the file manager or in backups; the password reaches a node only inside the commands that need it and is blanked out of what SteamCMD prints. Who uses an account is chosen with `server.limits.edit` (node or global only). Residual: during a run, the server's account (and the node's administrators) could read the cached login or the command line; the guide says to use an account of its own for servers, never a personal one |

## Main project risks

| Risk | Prob. | Impact | Mitigation |
|---|---|---|---|
| R1. Console behavior of `cs2.exe`/`srcds.exe`/`steamcmd.exe` on Windows (stdout vs console API) | High | High | Phase 0 spike with ConPTY and pipes before designing the console UI on assumptions. Plan B: RCON for commands. |
| R2. Complexity of per-server accounts (profiles, window stations, LSA rights, Windows editions, antivirus) | Medium | High | Phase 0 spike; implementation isolated in `Agent.Windows`; integration tests on Windows Server and Windows 11. |
| R3. Restarting/updating the agent stops servers | Certain | Medium | Documented; graceful stop; auto-start; shim post-v1. |
| R4. SteamCMD fragility (output format, transient errors, bootstrap) | High | Medium | Tolerant parser, bounded retries, full logs visible. |
| R5. Friction installing PostgreSQL on Windows for home users | High | Medium | Step-by-step guide, connection check with clear messages, installer that detects a local PostgreSQL. No SQLite fallback (explicit decision). |
| R6. Single panel instance (no HA) | Certain | Medium | Accepted for v1; scheduler already multi-instance safe (`FOR UPDATE SKIP LOCKED`); a SignalR backplane would be the required change. |
| R7. Loss of the Data Protection key ring or the panel key | Low | High | `Backup-Pulsar.ps1` backs them up with the database; the key ring is encrypted with a certificate, not machine DPAPI, so the backup restores on another machine (checked in Windows Sandbox and CI: backup, panel lost, restore, nodes reconnect). Losing the panel key still forces re-enrolling nodes. |
| R8. Panel/agent version drift | Medium | Medium | Protocol versioning, capabilities, N-1 support. |
| R9. Windows Defender slows 60 GB installs | High | Low | Document exclusions; **do not** add them automatically (the administrator's security decision). |
| R10. Scope creep | High | High | Phased roadmap with a "definition of done"; nothing outside the v1 flow until it is solid. |
| R11. Windows 10 out of support (since October 2025) | Certain | Low | Best-effort support for Windows 10 22H2; focus on Windows 11 and Server 2022/2025. |
