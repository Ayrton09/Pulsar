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

- ASP.NET Core Identity: password hashing (Identity's default PBKDF2), lockout, security stamp, TOTP 2FA (enforceable per role in settings). Recovery codes.
- Protected initial setup: when no users exist, `/setup` requires a **single-use token printed in the panel log/console** at startup (prevents someone else on the network from creating the first admin before you). Automated installations can provide it instead with `InitialSetup:Token` (at least 32 characters; then it is never printed).
- **HTTPS by default.** Cookies are `__Host-` prefixed, `HttpOnly; Secure; SameSite=Strict`; HSTS and HTTP→HTTPS redirection are on, and API calls over plain HTTP get `400 transport.https_required`. TLS can terminate at Kestrel or at a reverse proxy: `X-Forwarded-For/Proto` are trusted from loopback and from `Security:TrustedProxies`. Plain HTTP is only possible with the explicit opt-in `Security:AllowInsecureHttp=true` (development, trusted networks): cookies lose `Secure` and the prefix, the panel logs a warning at startup and the UI shows a permanent banner.
- Antiforgery token (readable `__Host-pulsar-xsrf` cookie, `X-XSRF-TOKEN` header) on every state-changing API call.
- Headers on every response: strict CSP (`default-src 'self'`, no inline scripts; inline `<style>` elements only, because the dialogs' scroll lock injects one at runtime — style attributes stay blocked, `object-src 'none'`, `base-uri 'none'`, `frame-ancestors 'none'`), `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, a restrictive `Permissions-Policy` and `Cross-Origin-Opener-Policy: same-origin`. The end-to-end suite fails on any CSP violation.
- Users change their own profile and password (`/account`); a password change ends the user's other sessions.
- Built-in rate limiting (`Microsoft.AspNetCore.RateLimiting`) on login, setup, enrollment and the agent challenge.
- Resource authorization on **every** endpoint and hub method (enforced by a test); IDs of other users' resources → 404.
- RBAC anti-escalation (doc 02).

### Panel ↔ Agent

- Mandatory TLS (the agent rejects `http://` except `localhost` in development mode).
- Per-node identity with a non-exportable key (TPM when available), proof of possession through a challenge, 15-minute JWT, immediate revocation, rotation.
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

### File manager (critical)

The agent's file manager runs in a SYSTEM process operating on directories **writable by untrusted code**. Defense in depth:

1. **Lexical validation** of the received relative path: reject roots, drive letters (`C:`), UNC (`\\server`), device paths (`\\?\`, `\\.\`), `..`, `:` (alternate data streams), reserved names (`CON`, `NUL`, `AUX`, `PRN`, `COM1-9`, `LPT1-9`, also with an extension), trailing dots/spaces, control characters, excessive lengths.
2. **Normalization** with `Path.GetFullPath(Path.Join(root, rel))` and a prefix check including the trailing separator (`C:\srv\a\` must not accept `C:\srv\ab`) using an ordinal case-insensitive comparison.
3. **Reparse points**: game code can create a *junction* (no privileges required) pointing at `C:\`. Every operation first opens a handle and checks with `GetFinalPathNameByHandle` that the real target is still inside the root, then operates **on that handle** (no TOCTOU between check and use). Enumeration does not follow reparse points; they are shown as non-navigable links.
4. **Impersonation**: file operations run impersonating the server account (`WindowsIdentity.RunImpersonated` with the account token). Even if layers 1–3 failed, the OS enforces that account's ACLs: it cannot read outside its directory. This also neutralizes hard links to foreign files (the ACL lives on the file, not on the link).
5. Limits: inline read/edit size (1 MiB), configurable upload size, optional disk quota.
6. Atomic writes (temp file + rename) and auditing of deletions/renames/uploads.

Path traversal tests: an extensive table of malicious inputs (more than 50 Windows-specific cases) + integration tests with real junctions, symlinks and hard links.

### Backups and restore

- Creation: does not follow reparse points (otherwise a junction to `C:\Users` would exfiltrate data into the archive); reads impersonating the server account.
- Restore: every archive entry goes through the same validator (anti zip-slip), no reparse points are created, writes are impersonated, only while the server is `Offline`.
- Backup downloads require `server.backups.view` and go through a transfer ticket.

### Manifests

Privileged import, closed steps, time-bounded regexes, typed variables, argv without a shell, per-format configuration encoders (doc 04).

### Secrets

| Secret | Where | Protection |
|---|---|---|
| User passwords | `users.password_hash` | Identity hash |
| Secret variables (GSLT, RCON) | `server_variables.protected_value` | ASP.NET Core Data Protection |
| Panel signing key | `panel_signing_keys` | Data Protection |
| Data Protection key ring | Panel disk | DPAPI (LocalMachine) on Windows; certificate on Linux or for portability |
| Node private key | CNG/TPM key store | Non-exportable |
| Specs with secrets on the node (secret variables, RCON password) | `state\servers\*.json` | ACL (SYSTEM + Administrators) and the whole record encrypted with DPAPI (machine scope) |
| Server account passwords | Nowhere | A new random one is set right before every logon and used once (doc 06) |
| Enrollment tokens / tickets | Database | SHA-256 only |

- APIs return secrets as `"***"` (write-only). Serilog filters redact known properties and never destructure whole DTOs. Neither `Authorization` nor cookies appear in request logs.
- **Important operational consequence:** a PostgreSQL backup without the Data Protection key ring cannot decrypt secrets. The panel backup guide will say so.
- Accepted limitation: a secret passed on the command line (e.g. GSLT with `+sv_setsteamaccount`) is visible to the node's local administrators. If a game accepts it from a file, the manifest should prefer that.

### Audit

Append-only from the application, no secrets, with IP and actor. Minimum actions: successful/failed login (aggregated per window to avoid flooding), logout, 2FA changes, user/role/access created-changed-deleted, node created/enrolled/revoked/rotated, server created/deleted, start/stop/restart/kill, console command (redacted), file edited/uploaded/deleted/renamed, backup created/restored/deleted/downloaded, scheduled task created/changed and its runs, template imported, settings changed.

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
| T5 | Argument/console command injection through variables | Typing, mandatory regex, no leading `-`/`+`, argv without a shell, encoders |
| T6 | Path traversal / junctions / ADS / devices | Lexical validation + handle final path + impersonation + ACL |
| T7 | Zip slip on restore | Per-entry validator |
| T8 | Malicious plugin escalates to SYSTEM, reaches other servers or shuts the node down | Per-server account, ACL, Job, restricted token without privileges |
| T9 | Malicious manifest | Privileged import, closed steps, capability review |
| T10 | RBAC privilege escalation / IDOR | Single per-resource evaluator, anti-escalation, authorization matrix tests |
| T11 | XSS / CSRF / clickjacking | Escaped React, CSP, SameSite + antiforgery, frame-ancestors |
| T12 | Login brute force | Lockout, rate limiting, 2FA |
| T13 | Malicious escape sequences in the console | xterm without the clipboard addon, http/https links only |
| T14 | DoS via console flood/uploads | Coalescing, bounded ring buffers, message and size limits |
| T15 | Secret leakage in logs/audit | Redaction, write-only secrets, sensitive console prefixes |
| T16 | First admin created by a third party | Locally printed setup token |
| T17 | Repudiation | Audit with actor, IP, correlation |
| T18 | RCON brute force on the game's TCP port (`logfile+rcon` servers) | Agent-generated random password per server (32 letters and digits, never user-chosen), RCON ban settings where the game has them (srcds; CS2 has none, spike 0001), password rotated on reinstall; game ports declare UDP only, so firewall rules managed by Pulsar never open RCON's TCP side |
| T19 | A server redirects the agent's own writes and reads (installs, configuration files, its log, deletion, run as SYSTEM in its folder) with junctions, symbolic links or hard links | Files opened without following a final link and checked by the final path of the handle; writes replace files instead of writing into them; deletion removes links without following them; SteamCMD runs as the server's account (doc 06) |
| T20 | A server escapes its Job (a scheduled task or WMI starts a process as its account) to keep running after a stop or a deletion | Processes of the account are killed before every start, after its processes end, before installs and before deletion; logged as warnings. Residual: such a process runs, without Job limits, while its server runs |

## Main project risks

| Risk | Prob. | Impact | Mitigation |
|---|---|---|---|
| R1. Console behavior of `cs2.exe`/`srcds.exe`/`steamcmd.exe` on Windows (stdout vs console API) | High | High | Phase 0 spike with ConPTY and pipes before designing the console UI on assumptions. Plan B: RCON for commands. |
| R2. Complexity of per-server accounts (profiles, window stations, LSA rights, Windows editions, antivirus) | Medium | High | Phase 0 spike; implementation isolated in `Agent.Windows`; integration tests on Windows Server and Windows 11. |
| R3. Restarting/updating the agent stops servers | Certain | Medium | Documented; graceful stop; auto-start; shim post-v1. |
| R4. SteamCMD fragility (output format, transient errors, bootstrap) | High | Medium | Tolerant parser, bounded retries, full logs visible. |
| R5. Friction installing PostgreSQL on Windows for home users | High | Medium | Step-by-step guide, connection check with clear messages, installer that detects a local PostgreSQL. No SQLite fallback (explicit decision). |
| R6. Single panel instance (no HA) | Certain | Medium | Accepted for v1; scheduler already multi-instance safe (`FOR UPDATE SKIP LOCKED`); a SignalR backplane would be the required change. |
| R7. Loss of the Data Protection key ring or the panel key | Low | High | Document backups; losing the panel key forces re-enrolling nodes. |
| R8. Panel/agent version drift | Medium | Medium | Protocol versioning, capabilities, N-1 support. |
| R9. Windows Defender slows 60 GB installs | High | Low | Document exclusions; **do not** add them automatically (the administrator's security decision). |
| R10. Scope creep | High | High | Phased roadmap with a "definition of done"; nothing outside the v1 flow until it is solid. |
| R11. Windows 10 out of support (since October 2025) | Certain | Low | Best-effort support for Windows 10 22H2; focus on Windows 11 and Server 2022/2025. |
