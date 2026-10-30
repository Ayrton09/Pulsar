# 09 — Technical roadmap

> Status: **proposal under review**.
> Every phase ends with a clean build, green tests and a verifiable demo. A phase does not start before the previous one is closed.

## Phase 0 — Foundations and spikes (de-risking) · *done* (CI pending a remote)

**Repository** (done): `.slnx` solution, `Directory.Build.props`, `Directory.Packages.props`, `global.json`, `.editorconfig`, projects with the references of doc 01, architecture tests, minimal panel (health endpoint, SPA hosting, 404 for unknown backend routes), minimal agent host (Windows service ready), Vite + React + Tailwind + i18n skeleton with Vitest and Playwright, CI (Windows + Linux, `dotnet format --verify-no-changes`, tests, web lint/test/build, E2E), CodeQL, Dependabot, open source files (README, LICENSE AGPL-3.0, CONTRIBUTING with DCO, SECURITY, CODE_OF_CONDUCT), issue/PR templates, English documentation.

**Spikes** (throwaway code in `spikes/`, findings in `docs/spikes/`):

1. ConPTY vs pipes with `cs2.exe`, `srcds.exe` (CS:S, GMod) and `steamcmd.exe`: capture, colors, input, Ctrl+C, stop. *Done for SteamCMD, CS:S and CS2 ([spike 0001](../spikes/0001-console-capture.md)); GMod pending.*
2. `CreateProcessAsUser` + dedicated account + ConPTY + `JOB_LIST` from a service in session 0 (window station/desktop, `LoadUserProfile`). Run in Windows Sandbox or a VM, never on a developer's main machine. *Done for a console process ([spike 0002](../spikes/0002-isolated-launch.md)); a real game as a dedicated account is checked in Phase 3.*
3. SteamCMD: bootstrap, progress format, exit codes, executable signature. *Done (spike 0001).*
4. Real readiness patterns for CS2/CS:S/GMod. *CS:S and CS2 done (spike 0001); the bundled manifests use RCON readiness.*

**Done when:** CI is green on both OSes; spike reports confirm or correct docs 04 and 06 and ADR-0006/0007.

## Phase 1 — Panel core · *done*

- PostgreSQL connection with a clear failure, migrations, health checks (`/health/live`, `/health/ready`).
- Identity, login/logout, initial setup with a token, cookies, antiforgery, rate limiting.
- Permission catalog, roles, assignments, `IPermissionEvaluator`, per-endpoint metadata + coverage test.
- Audit infrastructure.
- OpenAPI + generated TypeScript client.
- Frontend: router, shell, login/setup, user and role management.
- ProblemDetails conventions, `/api/v1` versioning.

**Done when:** an admin installs the panel, connects PostgreSQL, creates their user, creates another user with a limited role, and the actions are audited. Authorization matrix tests.

## Phase 2 — Nodes and protocol · *done*

- `Pulsar.Contracts` v1, `AgentHub`, enrollment, challenge/token, signed commands, anti-replay, revocation, rotation. *Done, including node key rotation (every 90 days or on request) and panel key rotation (doc 03, "Rotation").*
- Agent: Windows service host, `enroll`/`status`/`run` CLI, connection with reconnect, `Hello`/inventory, node metrics, file logging. *Done, plus `unenroll`, `service` and session hand-over before expiry (doc 03, "Agent implementation").*
- UI: node creation with the enrollment command, node list and node page (overview/system). *Done.*

**Done when:** an agent pairs, appears online with its inventory, survives network cuts, and revoking it disconnects it immediately. Tests: invalid signature, replay, revoked node, reused/expired token. *Met in development mode: `e2e/agent.spec.ts` drives the real `PulsarAgent.exe` from the UI (Windows CI leg), `RealAgentTests` cover reconnection, renewal and revocation, and the verifier tests cover signatures and replay. The elevated install is checked on a clean Windows by `scripts/sandbox/agent-install` (Windows Sandbox, panel and PostgreSQL inside it): refusal to install the service from a user-writable folder, machine key with its DACL, LocalSystem service with restart on failure, online, signed ping, service restart, revocation and `unenroll` all pass. The panel runs over HTTPS there (a certificate trusted machine-wide on the disposable machine), and the run also rotates the machine key as SYSTEM and fails on unexpected errors in the panel or agent logs. The same script runs in CI ("Agent install" job, windows-latest). A TPM-backed **machine** key could not be checked there (neither Windows Sandbox nor the GitHub runner has a TPM; TPM keys are covered in the user's store by `Pulsar.Agent.Tests`); it was checked in Phase 9 on a Hyper-V VM with a virtual TPM (`scripts/hyperv`).*

## Phase 3 — Windows process management · *done*

- `IGameProcessManager` + `WindowsGameProcessManager` (Job, ConPTY/pipes, per-server accounts, ACL, desktop). *Done (doc 06, "Implementation (Phase 3)"): account passwords are set right before each logon instead of being stored.*
- `ServerSupervisor` with a mailbox, state machine, stepwise stop, crash + backoff. *Done, with the agent's `ServerHost` (stored specs, operations with an outbox) and the panel side: servers, templates, operations, hub events, reconciliation and the UI (doc 05, "Implementation (Phase 3)").*
- `Pulsar.TestGameServer` and the Windows integration suite. *Done (`Pulsar.Agent.Windows.Tests`; the account tests run as SYSTEM in CI).*
- `_test` manifest (no SteamCMD) to exercise the whole lifecycle. *Done: `games/_test` (key `pulsar-test`) with the runtime subset of `Pulsar.Manifests` (doc 04, "Implementation status").*

**Done when:** with the test game, a server is created, started, stopped, restarted and killed from the panel; no orphans in any test scenario; the process runs as its own account. *Met: `e2e/servers.spec.ts` drives the real `PulsarAgent.exe` and the test game server from the UI (create, start, restart, stop, kill, delete; the real processes come and go; Windows CI leg); `Pulsar.Api.Tests/Servers` covers the lifecycle, crashes, a game's own quit, resuming with the agent, reconciliation and a result that arrives before its ack, with the real agent core; the production install check (Windows Sandbox and CI) runs the test game server as its own account from the SYSTEM service (batch logon only, private folder, process owner, resume after a service restart, kill, deletion removes the account); killing the agent killed its servers (no orphan); and a real Counter-Strike: Source server ran as its account from SYSTEM (`scripts/sandbox/real-game`). Not covered yet: CS2 and GMod (with their manifests, Phase 4).*

## Phase 4 — Manifests, SteamCMD and server creation

- `Pulsar.Manifests` (parser, schema, validator, renderer, encoders), published JSON Schema. *Done (doc 04): the v1 format, `schemas/manifest.v1.schema.json` checked against the bundled manifests and the C# model.*
- Bundled templates and import; CS2 manifest. *Done: `games/cs2` and `games/css`, written after running both games (spike 0001, F7); import with a preview of what a manifest runs, downloads and writes.*
- Port allocations, optional firewall management. *Done: node allocations (IPv4, ranges, no overlaps), firewall rules managed by the agent when its administrator enables it (UDP game ports only, bound to the executable).*
- `SteamCmdService` (queue, progress, retries, cancellation); install/update operations. *Done as `SteamCmdRunner`: a SteamCMD copy per server, run as the server's account (doc 06 explains why not a shared account), Valve's signature checked, live progress, retries, stall detection, cancellation; install, update and cancel operations.*
- UI: creation wizard, Startup and Network tabs, live install progress. *Done (Settings and Network tabs), with update, cancel, node allocations and a Games page.*
- Also: Source games through their log file and RCON (`logfile+rcon`), configuration files written before every start, an agent-generated RCON password, agent state encrypted with DPAPI, the agent's link-safe file access, and processes escaping a server's Job killed.

**Done when:** a CS2 server is created, installs through SteamCMD with live progress, and starts with the configured map/slots/port/arguments. *Met: from the panel's UI (a developer machine, development agent), a CS2 server was created with the wizard (Deathmatch, de_inferno, 12 slots, a port from the node's allocations); its install showed SteamCMD's live progress (Valve's bootstrapper verified, "downloading 4 %"), was cancelled from the UI and, with the 74 GB already on disk, reinstalled in seconds ("already up to date"); it started with `-ip 127.0.0.1 -port 27015 -maxplayers 12 +game_type 1 +game_mode 2 +map de_inferno`, became ready over RCON, picked up new settings (`server.cfg`) at the next start, and stopped over RCON with exit code 0. The production install check (Windows Sandbox and the CI job on Windows Server 2025) does the full download with a Counter-Strike: Source server: SteamCMD as the server's account with live progress, RCON readiness and stop, `server.cfg`, a UDP-only firewall rule for srcds, and a clean deletion. CS2's 74 GB download itself runs on a developer machine only; GMod's manifest is still to be written.*

## Phase 5 — Real-time console and metrics

- Ring buffer, rotated logs, subscriptions, backlog by sequence, audited command input. *Done (doc 03, "Implementation (Phase 5)"): sequences that survive agent restarts, the panel's deduplicated copy resubscribed at every Hello, RCON answers added unless the game logs them (`rcon.logsCommands`), sensitive commands audited without their arguments.*
- Job-based metrics, `PanelHub`, in-memory cache. *Done: usage from each server's Job every 5 s; the browser hub pushes server and node changes (an EF Core interceptor after every save), progress and usage; polling is gone.*
- UI: xterm console, server header with CPU/RAM/uptime, dashboard v1. *Done; the dashboard has no low-disk alert until the agent reports disks.*

**Done when:** live CS2 console, commands, reconnect without gaps; CPU/RAM/uptime updating without polling. (CS2's console is its log file plus RCON answers, plain text: colors come only from games on a pseudo console, spike 0001.) *Met on a developer machine through the real UI (Spanish), with a CS2 server created by the wizard: its console streamed live while it booted; `status` typed in the console ran over RCON and its answer appeared once (CS2 logs it; the agent added nothing); the panel was killed during a restart of the game and started again 11 s later: the browser asked for what followed its last entry and got exactly the next 179 lines, the same characters as the game's own log for that span, with no gap and nothing twice; CPU, memory (the Job's working set: `cs2.exe` and its `conhost.exe`, matching Windows) and uptime updated every 5 s with no API request from the page; stopping it showed the game's last lines; no error in the panel or agent logs. The end-to-end suite runs the console in a real browser with the test game server (CI, Windows). Found on the way: SteamCMD cannot update an install whose depot manifests it does not have (anonymous logins only get the current build's), so files copied from another install need the old manifests in the server's SteamCMD depot cache; recovery from the resulting "state 0x6" is a follow-up.*

## Phase 6 — File manager

- Agent file operations with the four layers of defense; transfers through tickets. *Done (docs 03, 06 and 08): one set of path rules on both sides; handle-based operations as the server's account on Windows; small operations answered in the command's ack (additive `CommandAck.Result`), content piped between the browser's request and the agent's with single-use tickets, atomic writes, versions for the editor, audited changes and downloads.*
- UI: file browser, text editor, upload/download. *Done (doc 07): CodeMirror, loaded on demand, with Source configuration highlighting; saves against the version opened.*

**Done when:** the path traversal suite (lexical + real junctions/symlinks/hard links) is green and editing `server.cfg` works. *Met: the suite is green locally and as SYSTEM in CI (where it also runs as a server's account: another server's file stays unreadable through a planted hard link, and deleting that link is refused). On a developer machine, through the real UI in Spanish with a development agent and a CS2 server: `game/csgo/cfg/server.cfg` opened in the editor, two lines added and saved with Ctrl+S (the bytes on disk as typed, no temporary file left); the server started and the agent's patch of `hostname` kept the added lines; the editor, with an unsaved change, refused to overwrite that newer version and offered to reload; `hostname` edited while the game ran, then `exec server.cfg` over RCON, and `status` answered the new name. An 841 MB map downloaded and uploaded back through the panel with identical SHA-256 (2.3 s and 4.7 s locally); everything audited; no error in the panel or agent logs. The end-to-end suite edits a file in the real editor, uploads, downloads, renames and deletes (CI, Windows). Found on the way: CS2 logs the RCON command but not the answer to a cvar query (`sv_tags`), so with `rcon.logsCommands` the console shows nothing for it (follow-up).*

## Phase 7 — Backups

- Creation with manifest globs, streaming zip, progress, cancellation, anti zip-slip restore, deletion, download, per-server retention. *Done (docs 03, 05, 07 and 08; ADR-0012 accepted): backups and restores are operations on the node, made through the file manager (never through a link, as the server's account), kept out of the servers' reach and checked by SHA-256 before a restore; a backup does not block the server; downloads stream through transfer tickets; retention per server, with locks; everything audited.*

**Done when:** creating, downloading and restoring a CS2 backup works, with duration/size recorded and without blocking the agent. *Met on a developer machine through the real UI in Spanish, with a development agent and a CS2 server created by the panel: a backup made while the game ran held its configuration (32 files, 48.5 KB: `cfg/**` and `gameinfo.gi`, none of its 60 GB of binaries), with its size, file count, hash and duration (0.6 s) recorded; downloaded through the panel with the SHA-256 the node and the panel had; after `server.cfg` was overwritten, a configuration file deleted and another added, Restore was disabled until the server stopped, then put all 32 files back byte for byte and left the added one. With 1.5 GB of add-ons the backup took 3 s while the game ran: the panel answered in 7 ms or less throughout, a `status` sent to the console halfway was answered by CS2, and progress showed live. Cancelling a backup halfway left nothing on the node; retention with one backup to keep deleted the oldest unlocked one and kept the locked one (audited as the system's); deleting the server deleted its backups from the node; no error or warning in the panel or agent logs. The end-to-end suite makes, downloads and restores a backup on the real agent (CI, Windows). Found on the way: deflating incompressible files made them 5.5% larger and six times slower; such files are now stored (ADR-0012).*

## Phase 8 — Scheduler

- Panel `BackgroundService`: tasks with `next_run_at`, selection with `FOR UPDATE SKIP LOCKED`, waits until the next run (no aggressive polling; woken when tasks change), occurrences computed with **Cronos** honoring time zones and DST.
- Typed actions: start, stop, restart, backup, update, console_command. Run history. *Done (docs 02, 05, 07 and 08; ADR-0010 accepted): tasks per server with a cron expression in an IANA time zone, run by the panel as the schedule through the same requests a user makes (so the same rules apply and every action is audited with the task's name); scheduling an action needs the permission to do it; runs missed while the panel is down are recorded together as one skipped run; history with the outcome of the operations tasks start; Schedules tab with presets, cron, a preview of the next runs and Run now.*

**Done when:** a daily scheduled restart runs at the correct local time and is audited; runs missed while the panel was down are marked `skipped` (not executed in a burst). *Met on a developer machine (clock in Buenos Aires, UTC-3) through the real UI in Spanish, with a development agent and a CS2 server created by the panel: a task to restart it every day at 14:20 in `Europe/Madrid` (UTC+2 then) previewed its next runs at 14:20 Madrid time and showed its next run at 09:20 local; the agent got the restart at 09:20:00 and CS2 was running again at 09:20:09; the audit log has `server.restart` by the schedule, named after the task, and the task showed Done and its next run the next day. A second daily task at 09:24 in the browser's zone (which the browser names `America/Buenos_Aires`) was due while the panel was stopped (killed at 09:21, started at 09:26): at start the panel recorded one skipped run, "missed: the panel was not running", restarted nothing, and moved the task to the next day. No error in the panel or agent logs. The end-to-end suite saves a task every minute through the UI and waits for the scheduler to run it on its own against the real agent (CI, Windows). Found on the way: Cronos gives occurrences in the zone's offset and PostgreSQL stores UTC only (now converted); browsers list some zones by their older names and leave UTC out (both handled).*

## Phase 9 — Hardening and v1.0 release

- Full security review against doc 08, enforceable 2FA, headers, limits. *Done (doc 08): TOTP two-factor authentication (codes work once, keys encrypted, recovery codes hashed, enforceable per role, Administrator role included); a review of the code against every control of doc 08 found and fixed: extra start arguments could override what the manifest sets (`+rcon_password`, `-port`) or store a secret in clear; live connections outlived disabled users, password changes and role changes; headers missing on error responses; no request size limit (now 2 MiB); redaction bypass with `;`; timing differences for locked and disabled accounts; wrong current passwords not counted towards the lockout; failed sign-ins could flood the audit log; no certificate option for the key ring.*
- Agent install check (`scripts/sandbox/agent-install`) on a Hyper-V VM with a virtual TPM: the node key must be a TPM machine key, and it must survive a rotation. *Done: `scripts/hyperv/run.ps1` builds Windows Server 2022 VMs from Microsoft's evaluation ISO (generation 2, Secure Boot, a virtual TPM, no network) and runs the agent install check and the release install check in them. With the TPM ready, the node key was created in the TPM as a non-exportable machine key and the rotated key too (38 checks pass), and the release ZIP installs, backs up, restores, updates and uninstalls there (31 checks pass).*
- Playwright E2E of the complete v1 flow (with the `_test` game in CI; real CS2 in a manual/nightly job because of its size). *Done: `e2e/` covers setup, roles and users, two-factor authentication required by a role, nodes with the real agent, and a test game server created, run, its console, files, backups and schedules, deleted (CI, Windows); real CS2 is run by hand at every phase (CS2 needs about 85 GB, more than a CI runner has).*
- CS:S and GMod manifests. *CS:S has been bundled since Phase 4 and is run for real in CI at every push (Agent install job: installed from Valve with SteamCMD, started and stopped over RCON as its own Windows account). GMod was dropped from v1 (maintainer's decision); [docs/guides/manifests.md](../guides/manifests.md) shows how to add it or any other game.*
- Packaging: panel and agent as Windows services (`install-service`), installation/update/backup guides; an "all-in-one" installer for a single PC (panel + agent paired automatically). *Done: a release ZIP (self-contained win-x64; built, attested and drafted by CI from a `v*` tag) with `Install-Pulsar.ps1` (all-in-one, panel, agent, update, uninstall, restore) and `Backup-Pulsar.ps1`; guides in [docs/guides](../guides/install.md). Checked from the ZIP on a clean Windows 11 (Windows Sandbox) and on Windows Server 2022 (CI): install, sign-in, node online, backup, panel lost and restored with its node reconnecting, update, uninstall.*
- Manifest contribution documentation. *Done: [docs/guides/manifests.md](../guides/manifests.md).*

**Done when:** the full v1 flow works on a clean Windows Server 2022 and Windows 11 installation following only the documentation. *Met for installation from the release ZIP, following [docs/guides/install.md](../guides/install.md), on a clean Windows 11 (Windows Sandbox, `scripts/sandbox/install`: PostgreSQL as a service, then the guide's steps) and on Windows Server 2022 (the "Install from the release ZIP" CI job, where PostgreSQL 17 is installed with its own installer as the guide says, next to the runner's PostgreSQL 14, which the installer rightly passes over): panel and agent services, sign-in, node online, signed ping, backup, the panel lost and restored with its node reconnecting by itself, update and uninstall, 32 checks each, no error in the panel's log. The rest of the v1 flow runs on every push: the end-to-end suite through the real UI (setup, roles, two-factor authentication, nodes, a server with its console, files, backups and schedules) and the agent install job (the agent as a service on a clean Windows, a real Counter-Strike: Source installed from Valve and run as its own account). Found on the way: the panel's database migrations ran inside the service start, which Windows limits to 30 seconds (now done by the installer first), and the installer and the backup script took the first PostgreSQL on a machine with several (now the newest; older than 15 refused). Both checks also ran on Hyper-V VMs with a virtual TPM (`scripts/hyperv`): on Windows Server 2022 as its original image installs it (build 20348.169), .NET 10 programs do not start ("Your Windows doesn't fully support CET"); Pulsar keeps CET on, the installer now checks this before changing anything and asks for Windows updates, and the guide lists them as a requirement. With the September 2026 cumulative update (build 20348.5622) both checks pass, the node key in the TPM. Still open: tagging v1.0.0.*

## Phase 10 — Notifications, remote backups and more games

What most changes the experience after v1: knowing when something goes wrong without opening the panel, backups that survive the loss of their node, and the games people look for first.

- Notifications: Discord webhooks and email (SMTP), per event and per server: a server crashed or failed to start, a backup or a scheduled task failed, a node went offline, a node's disk is low (the agent reports its disks; today it does not). Secrets (webhook URLs, SMTP password) encrypted like the other secrets; sending retried and audited; a test button. *Done (docs 02, 07, 08): channels, settings and an outbox sent by the panel with retries; repeats about the same thing within 10 minutes dropped; messages in English or Spanish; the agent reports the drives of its servers and backups folders. Checked with a published panel, a real agent and a local SMTP server (Mailpit): the test message; a server whose process was killed (one email, and none for the second crash minutes later); the agent stopped (offline after the 1-minute grace, back online 11 s after it reconnected); a drive under the threshold; the page in Spanish with no console errors. Discord is checked by the tests' transport only.*
- Remote backups to S3-compatible storage (AWS S3, Backblaze B2, Cloudflare R2, MinIO): uploaded by the agent after the local archive is written, with the same retention; restore downloads from there when the local copy is gone. Credentials never reach the browser. *Done (docs 02, 03, 07, 08; backup guide): storages in the panel (`settings.manage`, the secret key encrypted), a choice per server, the agent uploading with the AWS SDK (Apache-2.0) and fetching back on restore and download, deleted copies with their backups, failed copies notified. Checked with a published panel, a real agent and a local SeaweedFS (MinIO no longer publishes binaries): a backup copied (the object in the bucket, the same size), the node's archive deleted and the backup restored from the copy (the file back as it was, the archive back on the node), deleted again and downloaded straight from the bucket (same SHA-256), deleting the backup deleting its copy, and a wrong secret leaving the backup on the node with `SignatureDoesNotMatch` and an email. The agent's S3 tests run in CI against SeaweedFS in Docker.*
- Games: Valheim, Rust, Palworld, SCP: Secret Laboratory and Minecraft (Java edition), each run for real before its manifest is written (as CS2 and CS:S were). Minecraft needs what the SteamCMD games do not: the server downloaded from Mojang (checked against Mojang's own hash), a Java runtime managed by the agent, and the Minecraft EULA accepted by whoever creates the server. *Done (doc 04, manifests guide, each game's README): what these games needed became manifest features rather than special cases: a port that follows another (Valheim), RCON for commands next to a console's output (Palworld), Unreal Engine ini settings, the server's absolute folder as a placeholder (SCP:SL), an `else` in conditions (Rust), and the `minecraftServer` install step (Mojang's server and the Temurin runtime its version asks for, each checked against its publisher's checksum, from an allowlist of hosts). Each game was installed, started, reported ready and stopped from a published panel with a real agent: Valheim (Ctrl+C, A2S when listed), Rust (RCON, A2S, a 1000 m map in under a minute), Palworld (RCON, its ini), SCP: Secret Laboratory (its configuration in the server folder) and Minecraft 26.3 with Java 25 (a Server List Ping answering with the settings).*

**Done when:** a crashed server and a failed backup each send a Discord message and an email; a backup is uploaded to S3-compatible storage, the node's copy is deleted, and it is restored from the remote copy; each new game is installed, started, reported ready (and answering A2S or RCON where the game has them) and stopped from the panel.

## Phase 11 — Players, access per server and known gaps · *done*

What a server's owner sees and who else can manage it: the players on a server without joining it, and access to one server or one node for someone who is not an administrator. Along the way, the gaps found in earlier phases.

- Query: a manifest `query` section (`a2s` for Source and Steam games, `minecraft` for the Server List Ping) on a named port; the agent asks running servers for their name, map, players and slots and sends it with the metrics; the panel shows it with the list of players. A `query` readiness. Player names are untrusted text. *Done (docs 03, 04, 07, 08 T22): read with fixed limits and cleaned twice (agent and panel); a server on every interface is asked where it answers, since srcds ignores queries from loopback (found with CS:S).*
- Access per server and per node: a role given to a user on one server or one node (the domain has supported it since Phase 1; the database, the API and the pages did not). Lists, pages and live updates show each user only what they may see; permission changes reach live watches without reconnecting. *Done (docs 02, 07, 08): `node_access` and `server_access`; one gate checks routes under `/servers/{id}` and `/nodes/{id}` against their resource and answers 404 for what the user cannot see; filtered lists; live notices per node and server; the user dialog gives and takes access. A changed assignment closes the user's live connections, which reopen at once with what they may see now.*
- SteamCMD installs stuck in state `0x6` recover by themselves (the app manifest reset, retried with `validate`). *Done (doc 06).*
- RCON answers the game does not log (CS2 cvar queries such as `sv_tags`) are shown in the console, without a second copy of those it does. *Done (docs 03, 04): an answer whose first line the log does not show within 2 seconds is added.*
- Atomic file replace on Windows tolerates other programs holding the old file (it failed once under heavy parallel test load: `AHardLinkIsReplacedOrRemovedNeverWrittenInto`, `ReplacingAFileFollowsTheRules`). *Done (doc 06): renames with POSIX semantics, as deletes; a test holds the old file open as a virus scanner would.*

**Done when:** CS:S, CS2, Valheim and Rust show their players and map in the panel through A2S, and Minecraft through the Server List Ping; a user given a role on one server sees and manages only that server, and loses it live when the role is taken away; a CS:S install put into state `0x6` recovers on its own; `sv_tags` over RCON shows its answer in the CS2 console once. *Met on 2026-10-05 with a published panel and a real agent, each game installed for the check and deleted after it: CS:S `4 / 16 (4 bots)`, de_dust2 and the four bots with score and time; CS2 `2 / 10 (2 bots)` and de_dust2 (CS2 sends player names empty); Valheim `0 / 10` when listed; Rust `0 / 50` on its procedural map; Minecraft 26.3 `0 / 10` with its message of the day. A user given a server role on the Rust server saw only it in the panel, with Start, Stop and the console, was told CS2 does not exist, and 4 seconds after the role was taken away the page said it was no longer available and the navigation lost Servers, without reloading. A CS:S install given an app manifest of a build whose manifests SteamCMD lacks failed with `state is 0x6` and recovered by itself in 51 s, back on the current build, and started. In CS2, `sv_tags` showed its answer once (the agent's copy, 2 s later) and `status` once (the game's own). The E2E suite passed (8 tests), with no errors in the panel or agent logs.*

## Phase 12 — Limits, API keys, mods and three more games · *done*

What hosts and server owners ask for once the basics work: a server that cannot take the whole machine, automation without a browser session, mods installed from the panel, and three more of the games people run.

- Resource limits per server: a CPU cap (a share of the whole machine, as the usage is shown) and a memory cap, enforced by the Job each server already runs in (`JobObjectCpuRateControlInformation` hard cap, `JOB_OBJECT_LIMIT_JOB_MEMORY`), applied to a running server at once. Set with a new permission, `server.limits.edit`, grantable on a node or globally only, so a server's owner cannot raise their own limits. The usage shows the limit next to it, and a server stopped by its memory limit says so. *Done (docs 02, 06, 08 T23): the Job's CPU hard cap is always set (100 % without a limit, as Windows will not turn it on later for a Job that never had it); a memory notification from the Job marks the exit as `memoryLimit`.*
- API keys: a user creates keys for scripts (`Authorization: Bearer pulsar_…`), each with a name, an optional expiry and optionally a subset of the user's permissions. A key acts as its user with live permissions (a disabled user, a removed role or an expired or revoked key stops working at once), is stored as a hash, is shown once, and never reaches its own management, the sign-in, password and two-factor endpoints, or the live hub. Requests with a key skip the antiforgery check (no cookie is involved), are rate limited per key and audited as the key. *Done (docs 02, 07, 08 T24).*
- Template upgrades: a server moves to a newer version of its game's template (the panel's bundled manifests get new versions in this phase), with its settings checked against the new version, while it is stopped. *Done (docs 02, 07): `PUT /servers/{id}/template`; the server is provisioned and installed again with its files kept. The bundled manifests stay at 1.0.0 (the panel is private and always 1.0.0); upgrades were checked with copies of new versions.*
- Mods: a manifest `addons` section describes the mods a game supports, downloaded by the agent from a GitHub release (the asset checked against the SHA-256 GitHub publishes for it; refused without one), unpacked into the server with the archive rules of restores, and removed by the list of files they added (game files they replaced are restored by a validating update). Addons can require or exclude each other, be reinstalled after every game update (Oxide), and keep a line in a game file after the game's updates overwrite it (Metamod's line in CS2's `gameinfo.gi`). First addons: Metamod:Source and CounterStrikeSharp for CS2, Oxide and Carbon for Rust. *Done (docs 02, 03, 04, 05, 07, 08 T25; manifests guide): a Mods tab; removing an addon also removes the folders it leaves empty; importing a template shows which repositories its addons come from.*
- Steam Workshop: a manifest variable marked as a list of Workshop items gets an editor that shows each item's title, checks that it belongs to the game and expands collections (the panel asks Steam's public `GetPublishedFileDetails`, no key); the game server downloads the items itself (CS2 Workshop maps, Project Zomboid). *Done (docs 04, 07, 08 T26) for Project Zomboid's `workshopItems`: the lookup goes through the panel (`POST /api/v1/workshop/lookup`), so the browser makes no third-party requests. CS2 Workshop maps need a Steam Web API key for collections and are left for later.*
- Games: ARK: Survival Ascended (its log and RCON; it answers no A2S; non-commercial use only without a licence from its publisher, said in its README), 7 Days to Die (its log and the telnet console; `serverconfig.xml` settings) and Project Zomboid (its Java server run directly, its console; settings in its `.ini`; Workshop mods), each run for real before its manifest is written. *Done (doc 04, manifests guide, each game's README): new manifest features: a telnet console (7 Days to Die), `xml-properties` and `key-value` settings, a `copyFileIfMissing` install step, internal ports that are never opened in the firewall, and a longer RCON answer timeout (ARK answers in 2 to 4 s).*

**Done when:** a server limited to 10 % CPU and 512 MB uses no more than that, and one that needs more memory stops and says why; a key with only `server.start` starts a server with `curl` and gets 403 on stop, and stops working the moment it is revoked; Metamod and CounterStrikeSharp install into a CS2 server and keep their `gameinfo.gi` line across a game update, and Oxide is reinstalled after a Rust update (with real downloads from GitHub; CS2 and Rust themselves when their downloads are approved again); a Workshop collection shows its items' titles; ARK: Survival Ascended, 7 Days to Die and Project Zomboid are installed, started, reported ready and stopped from the panel with their consoles working. *Met on 2026-10-06 with a published panel and a real agent, except where said. Limits were checked with ARK rather than the numbers above: a 4096 MiB memory cap stopped it after 102 s, reported as out of memory, and a CPU cap lowered live from 25 % to 3 % held it at 1.3–1.9 % (the tests cap the test game at 512 MB). A key with only `server.start` started a server and got 403 on stop and 401 once revoked (with the agent core in the API tests); with `curl` on the published panel a key created allocations and servers and ran commands without a cookie or an antiforgery token. The four addons were downloaded from GitHub (Metamod 2.0.0.1473, CounterStrikeSharp v1.0.376, Oxide 2.0.7801, Carbon production_build), their SHA-256 matching what GitHub publishes, unpacked where their games expect them and removed without a trace; from the Mods tab, a throwaway template got Metamod, CounterStrikeSharp and Oxide installed, Carbon refused next to Oxide, Metamod refused while CounterStrikeSharp needed it, and all three removed. CS2 and Rust themselves were not downloaded again: Metamod's line coming back after an update and Oxide reinstalled by an update are checked by the API tests with the agent core and scripted SteamCMD. Project Zomboid's Workshop editor showed a real collection of 84 items and then the titles, sizes and dates of all 85 items from Steam. ARK: Survival Ascended was ready in about 45 s (open to players about 2.5 min after starting) and stopped with `SaveWorld` and `DoExit` in 21–28 s; 7 Days to Die answered telnet commands once and stopped in 6.5 s; Project Zomboid was ready in 182 s, took console commands and answered A2S. The E2E suite passed (8 tests), with no errors in the panel or agent logs.*

## Phase 13 — Metric history, game databases and Steam accounts · *done*

The first three post-v1 items: how a server and a node did over the last hours and days, the MySQL/MariaDB databases plugins expect, and the dedicated servers that only download with a Steam account that owns them.

- Metric history: the panel folds the samples agents already send (servers every 5 s, nodes every 15 s) into one-minute buckets in memory (average and peak CPU, memory and players; a node's CPU and memory) and writes them to PostgreSQL every minute; once an hour they are rolled up into one-hour buckets. Minute buckets are kept 2 days and hour buckets 90 days (configurable). Nothing changes on the agent or the protocol. The server and node pages chart CPU, memory and players over 1 hour, 24 hours, 7 days and 30 days, drawn by the web itself (SVG, no chart library), fetched again every minute for the short ranges. Reading history needs the same permission as the live usage. *Done (docs 02, 07): minutes and hours are written in one transaction each; stopping flushes and rolls up, and the first roll-up after a start reaches back as far as minutes are kept; memory is capped at 64 TiB and a minute at 600 samples, so a broken agent cannot overflow the sums.*
- Game databases: an administrator adds database hosts (MySQL or MariaDB the panel can reach, with an account that may create databases and users; optionally tied to nodes, and with the address game servers should use when it differs). A server owner creates databases for their server on a host its node may use (a limit per server set like the resource limits), sees the name, user, address and password, rotates the password and deletes them; deleting the server deletes them. The panel creates `s<shortId>_<name>` and a user of the same name restricted to that database, with a random password, through MySqlConnector (MIT); identifiers are generated, never taken from input. Passwords are stored with Data Protection, as backup storage secrets are. New permissions `server.databases.view` and `server.databases.manage`, audited. *Done (docs 02, 07, 08 T27; databases guide): the grant escapes `_` (a wildcard in a grant's database name, found by the review: a user could otherwise create `s<id>Xmain`); reading a password is audited; hosts may require TLS (on by default for new hosts outside the local network); a server's databases are dropped when its node confirms the deletion, each unreachable host tried once; server managers get a generic message when a host fails.*
- Steam accounts: a manifest SteamCMD step may say `login: "account"`. An administrator adds Steam accounts (user name and password, stored with Data Protection, write-only), and whoever may change a server's limits picks the account a server uses. The account travels with each install and update command, never in the server's spec. Steam Guard: when Steam asks for a code (by mail or the mobile app), the operation fails with `steamcmd.guard_required` and the panel asks for the code and retries with it. The login SteamCMD caches is kept by the agent, encrypted with DPAPI per node and account, and lent to the server's SteamCMD only while an install or update runs with the server stopped, then taken back: the game process, its mods and the file manager never see it (`.pulsar/steamcmd/config` is refused by the file manager). A dedicated Steam account is recommended in the guide. *Done (docs 02, 03, 04, 06, 07, 08 T28): a run with an account rebuilds the server's SteamCMD copy from the verified bootstrapper first (a copy the server could replace never sees the password, found by the review); the cached login is tied to a hash of the account's name and password and forgotten when Steam refuses it; the file manager refuses changes under `.pulsar` and any access to the lent login's folder by where the opened handle is (8.3 aliases and junctions included).*

**Done when:** after a few hours with a server running, its page charts CPU, memory and players that match what the live usage showed, a panel restart leaves at most the minute in progress missing, and the 7- and 30-day views read hour buckets; a database host on a real MariaDB gets a database for a server, the game server's own account connects with the credentials the panel shows and sees no other database, rotating the password locks the old one out, and deleting the server drops the database and its user; a server installs with a real (secondary) Steam account, asking for the Steam Guard code once, a second update logs in from the cached login without a code, and the cached login is in no server file after the operation. *Met on 2026-10-09 with a published panel and a real agent, except where said. History: a test server capped at 10 % CPU burning for 90 s charted a 10.3 % peak and the 200 MB it allocated, minute by minute, matching the live usage; a panel killed without stopping lost only its minute; the 7- and 30-day views and the roll-up are checked by the API tests (not over hours of real running). Databases, on a portable MariaDB 12.3.3 with a dedicated `pulsar` account: the host was checked by connecting, a database created from the Spanish UI, its user (connecting with the shown credentials from the node) saw only its own database and was refused `mysql`, a changed password locked the old one out with the data kept, and deleting the server dropped the database and its user, audited (`dropped: true`). Steam accounts: the real sign-in with a Steam account was skipped at the user's request; Steam Guard, the cached login, a refused cached login, an account-only game and the rebuild of SteamCMD are checked with scripted SteamCMD in the agent and API tests, and SteamCMD's login messages come from forum reports, not from a run. The phase was reviewed per area (23 findings, 2 high, all fixed) and the fixes reviewed again (4 findings, 1 high, fixed). The E2E suite passed (8 tests).*

## Phase 14 — Server hosts and bulk enrollment · *done*

The next two post-v1 items: game servers that keep running while the agent restarts or is updated, and many machines enrolled at once.

- Server hosts (the shim of ADR-0006): each game server runs under a host process of its own (`PulsarAgent host`), which owns its Job, its console and its exit; the agent talks to it over a named pipe only the agent's account may open. A service stop, restart, update or crash of the agent leaves the games running; the next run takes them over before its first Hello and their consoles continue where it stopped reading. Windows shutting down still stops them gracefully, as do `unenroll` and `service stop --stop-servers`. Hosts run from a copy of the agent's program per version, so an update can replace the installed files. *Done (docs 03, 05, 06, 08): see doc 06, "Server hosts".*
- Bulk enrollment: reusable enrollment tokens (a name, a number of uses, an expiry, revocable) enroll each machine as a new node named after it; each token decides whether its nodes wait for an administrator's approval (on by default), approved one by one or many at once. *Done (docs 02, 03, 07, 08).*
- An agent-only MSI for Intune and group policies, built with Windows' own Installer API (no third-party tool): a silent install with the panel's values enrolls the machine and starts the service, a newer package upgrades it keeping the enrollment (and the games running), and uninstalling unenrolls it. *Done (install guide).*

**Done when:** a test game server keeps the same process through a restart of the agent service, a crash of the agent and an upgrade of the agent MSI, and its console goes on without a gap; Windows Sandbox installs the agent MSI silently with a reusable token, the node waits for approval and connects once approved, and uninstalling the MSI stops the game and removes the enrollment. *Met on 2026-10-09.*

- *Development agent with the manual panel:*
  - The agent was killed with a test server running. The game (same process id) and its host lived on, and the next run reported it running.
  - Its console went on with no line repeated, once a second had passed since the last output; that is the checkpoint interval.
  - A game killed while the agent was away was reported as a crash when it came back, and the crash policy restarted it in a new host.
  - A host uses about 12 MB of private memory.
- *Windows Sandbox (`scripts/sandbox/agent-install`), the service as SYSTEM, a server account and a test game:* 69 checks, all passed.
  - The game kept its process through a service restart and through a crash of the agent (Windows restarted the service).
  - `service stop --stop-servers` stopped it, and it started again with the service.
  - With the agent MSI:
    - It was refused where an agent installed from the ZIP runs. A refused token failed the install and left nothing.
    - A silent install with a reusable token created a node named after the machine. The node waited for approval, then connected once approved.
    - A rebuild of the same version and an upgrade to 1.0.1 kept the enrollment and the game's process.
    - The ZIP installer refused to touch the MSI agent, and a downgrade was refused.
    - Uninstalling stopped the game gracefully and left no service, enrollment, product or host. A `SERVERSROOT` with a trailing backslash worked.
    - The token appeared in no log.
- *Browser, in Spanish:* a token created from **Nodes** showed both commands once. The approval card started unchecked and showed the machine, the token and the key fingerprint. An approved node was online within a minute, and revoking the token worked.
- *Review:*
  - The phase was reviewed per area: 43 findings, 1 high (a logon lock a game's account could squat), all fixed.
  - The fixes were reviewed again: 5 findings, all fixed.
  - The Windows Sandbox logs showed Windows Installer's Restart Manager stopping the service before the uninstall's unenroll could stop the games gracefully. It is disabled in the package.
- *Tests:* the .NET suite (1343 tests; one older file-manager test failed once under full parallel load and passed in every rerun), vitest (196) and the E2E suite (8) passed.

## Phase 15 — Backups of running games · *done*

The next post-v1 item: a backup made while the game runs that holds every file as it was at one moment, and a restore that can leave nothing newer behind.

- Game saves: a running game whose manifest has `backup.save` is asked to save through its console first; the backup waits for the line that confirms it, or a fixed time for games that print none, and records how the game answered. Minecraft pauses its autosave around the snapshot (`save-off`, `save-all flush`, `save-on`); an agent that stops in between resumes it at its next start. Bundled for Minecraft, Rust, ARK, Palworld, 7 Days to Die and Project Zomboid. *Done (doc 04, "Backups").*
- Snapshots: the agent reads a running game's files from a Volume Shadow Copy snapshot of their volume (no writers, deleted after the backup), files the game holds open included; without one (a development agent, a FAT or network volume, no shadow storage, turned off) it reads them live as before and the backup says why. *Done (docs 03, 05, 06, 08).*
- Clean restores: a restore can first delete the files the backup's rules select that it does not have; refused for a backup that left files out. *Done (docs 02, 05, 07, backup guide).*

**Done when:** a backup of a running test game, made by the agent service as SYSTEM, reads a snapshot (a file the game holds open with no sharing is in it), the game confirmed its save first, and no snapshot is left afterwards; a clean restore deletes a file made after the backup; a development agent that cannot take snapshots reads the files live and says why. *Met on 2026-10-10.*

- *Windows Sandbox (`scripts/sandbox/agent-install`), the service as SYSTEM, a server account and the test game:* 78 checks, all passed, before and after the review's fixes.
  - The game locked a file with no sharing; the backup came from a snapshot after the game confirmed its save, held that file, and left no snapshot (none listed by `Win32_ShadowCopy`), no paused save, and the agent's log showed the snapshot taken and deleted.
  - A clean restore, as the server's account, deleted a file made after the backup and brought the saved world back.
  - The first run showed `PrepareForBackup` answering `VSS_E_BAD_STATE` in a snapshot without writers: the requester skips it, as Microsoft's vshadow does.
- *End-to-end run (a development agent, not an administrator):* a backup of the running test game confirmed its save through its pseudo console and said it was copied live because the agent is not running as an administrator; a clean restore from the restore dialog deleted the newer file.
- *Browser, in Spanish:* the create dialog's warning, the note under a backup ("Copiado con el juego encendido, sin instantánea (el agente no corre como administrador) · el juego guardó antes") and the restore dialog with its clean option.
- *Review:*
  - Three reviewers, one per area: 17 findings (0 high, 5 medium, 12 low), all fixed. Among them: an agent stopping during a backup now cancels it first so the game saves again, a backup that began stopped reports a live copy when the game starts meanwhile, and a clean restore deletes by the rules the backup was made with (recorded in the archive).
  - The fixes were reviewed again: 1 medium finding, a flaky test that showed state files could not be read and replaced at the same time (also possible in production), fixed.
  - The last full run showed a Phase 14 host flaky under load: a client that connected to a host's pipe and left at once left it broken, and the host ended with its game. Fixed, with a test that reproduced it every time.
- *Tests:* the .NET suite (1414 tests, 13 skipped: the ones that need an elevated, disposable machine), vitest (198) and the E2E suite (8) passed.

## Full audit after Phase 15 · *done* (2026-10-11)

A read of the whole codebase, not just a phase's changes, in eight areas (agent servers and hosts; the rest of the agent; its Windows layer and the install scripts; the API and infrastructure; the application layer; domain, contracts and manifests; the web's server pages; the rest of the web).

- *Found and fixed:* 90 findings (2 high, 19 medium, 69 low). The high ones: a database host's saved password was sent to a new address when only the address changed; and a game that stopped reading its console could hold a console command, and with it every stop, kill and the agent's own stop for that server, forever. Among the rest: a locked backup deleted by retention racing the lock, allocation lists naming other tenants' servers, the panel's settings (and its first-run token) readable while the installer wrote them, the self-signed panel certificate able to sign others once trusted, a servers root that could be a junction, unenroll leaving server accounts and secrets, unsaved file edits lost when changing tabs, and refresh failures replacing pages.
- *Reviewed again:* seven reviewers on the fixes found 21 more (0 high, 7 medium, 14 low): 20 fixed, and one (a command arriving before Hello's answer blocking the SignalR client) shown not to happen by a test that now guards it; among them a start the node had already given up being wanted again after a save conflict, a refused start killing an install's processes, and a command the agent had refused still reaching a game that thawed. Two flaky tests turned out to be a real ordering bug (an operation counted as ended before its result reached the outbox) and a timed test (now counted).
- *Checks:* the .NET suite (1575 tests, 13 skipped), vitest (256), the E2E suite (8), and both Windows Sandbox runs (the panel installer, and the agent installed as a service with its MSI).

## UI pass after the audit · *done* (2026-10-12)

From screenshots of every page, in dark and light and at phone width:
- On a phone, the navigation is a menu panel opened from a top bar. The server's tabs scroll sideways, and no page is wider than a 320 px screen.
- A button that cannot be used loses its fill. The sidebar stays in view, with icons (lucide-react). The server's state sits next to its name. The dashboard draws CPU and memory as bars. The server tabs follow the WAI-ARIA tabs pattern.
- The panel is dark only now, at the user's request.
- Reviewed: 3 findings (1 medium: the tabs' arrow keys counted from the open tab, not the focused one), all fixed.
- *Checks:* vitest (264) and the E2E suite (8).

## Phase 16 — Client certificates for agents · *done* (2026-10-12)

The optional mTLS mode, on the node key every agent already has: no CA, no new enrollment (docs 03 "Client certificates", ADR-0003's addendum).
- An agents' endpoint (Kestrel `Agents`, the installer's `-AgentsPort`). It asks every client for a certificate and serves only the agent protocol, so browsers never see a certificate request.
- Each node presents a certificate it signs itself on its key, and the panel checks the key against the node's credential. A session opened there is bound to that key: its token is useless without it.
- Agents move there on their own: told the port by a signed command, they put it on the host they already reach the panel by. They keep the old address as a fallback for when the port stops answering.
- A switch on the Nodes page, off by default, requires certificates. It ends the sessions without one and sends agents to the agents' address with a refusal signed for the node and the nonce of its request. The page first lists the nodes still connecting without a certificate and whether their agent can move.

**Done when:** an agent installed as a service moves to the agents' port on its own, its machine key presented in the TLS handshake (in the TPM when the machine has one), and keeps working with certificates required; a session token alone is refused there; a closed agents' port sends it back to its fallback. *Met on 2026-10-12.*

- *Spike:* a client certificate on a persisted CNG key reaches Kestrel's `RequireCertificate` through SChannel over HTTP/1.1, HTTP/2 and WebSockets.
- *Tests:*
  - The panel's checks run through TestServer with a simulated endpoint.
  - The real agent core moves, falls back, follows a signed refusal, enrolls and rotates its key on the agents' address.
  - Real Kestrel with real TLS, the real agent transport included.
  - A real CNG key signs a TLS handshake through SChannel.
- *Windows Sandbox (`scripts/sandbox/agent-install`):*
  - The service, with its software machine key, moved to the agents' port; then the panel required certificates for the rest of the run: key rotation, games, backups, the MSI's re-enrollments (which followed the signed refusal) and `Install-Pulsar.ps1 -Mode Agent`.
  - 88 checks, all passed, before and after the review's fixes (82 before this phase, and 6 new: the move, a signed ping there, a client without a certificate turned away, the switch, still online, `status --check` through the agents' address).
- *TPM:* a Hyper-V VM with a virtual TPM (Windows Server 2022, `scripts/hyperv`), 91 checks.
  - The service's machine key was created in the TPM, non-exportable, and the agent moved to the agents' port with it as its client certificate. A signed ping went through there, and the rotated key was in the TPM too.
  - The run found that `enroll` reported the service as running before it was, since with a TPM key it starts seconds later. It now waits for the service.
- *Review:* two reviewers, one per side.
  - 13 findings (1 high, 4 medium, 8 low), all fixed. The high one: without `Agents:PublicUrl` the panel signed an address built from the request's `Host` header, which a proxy in front of the panel could choose to move nodes to a host of its own. Now only the port (or the operator's address) is signed, the agent supplies the host, and refusals carry the agent's nonce.
  - Among the rest: an agent that cannot make a certificate no longer says it can move; a signed address is kept only after signing in there; a 404, 5xx or a hub that does not open also sends the agent to its fallback.
  - The fixes were reviewed again: one more low finding (a fallback equal to the address), fixed.
  - A new test found that the agent read a refusal's body twice.
- *Checks:* the .NET suite (1626 tests, 13 skipped), vitest (268) and the E2E suite (8).

## Second full audit after Phase 16 · *done* (2026-10-13)

A second read of the whole codebase in ten areas, the UI pass and Phase 16 included: the agent's servers; its processes, backups, files and Steam; its connection and host; its Windows layer and the scripts; the API and infrastructure; the application's server side and its node and access side; domain, contracts and manifests; the web's server pages and the rest of the web.
- *Found and fixed:* 115 findings (5 high, 22 medium, 88 low).
  - The high ones:
    - A global `user.manage` holder could reset the password and second factor of a user with only node or server roles, and take over that user's powers. Now a user manager must hold every grant of the user it changes, at that node or server.
    - Revoking a node's key left a key it was rotating to usable.
    - The agent sent a game's RCON or telnet password to whatever listened on its port, such as another server's plugin. Now the process at the other end must run as the server's account.
    - Upload "Replace" wrote into the folder open at that moment.
    - The editor saved Windows line endings as Unix ones.
  - Among the rest:
    - ARK's `?ServerAdminPassword=` passed the extra-argument check.
    - A database host moved to a public address kept plain connections.
    - Refused agent sign-ins went unaudited.
    - A role saved before the permission catalog loaded lost every permission.
    - A host that could not be opened was taken for gone, so its game could start twice.
    - S3 restores had no size cap.
    - Two files differing only by case made a backup unrestorable.
    - The enrollment token was on the installer's command line.
  - Not fixed in code: two are documented instead. The edit-version check races the rename by microseconds, and NTFS has no compare-and-swap. The MSI's enroll action must get the token on its command line.
- *Reviewed again:* five reviewers on the fixes found 30 more (1 high, 8 medium, 21 low), all fixed.
  - The high one: the first fix for misleading file names refused format characters in every name, which left such files out of backups. Names are free again, and the web shows hidden characters as their code (`[U+202E]`).
  - Also: a deadlock between an agent's sign-in and a key revocation, proven with PostgreSQL's 40P01 and fixed by locking the node first everywhere; and a host left stuck after a reboot, now judged by its named pipe.
- *Checks:*
  - The .NET suite: 1880 tests, 13 skipped.
  - vitest: 325.
  - The E2E suite: 8.
  - Windows Sandbox, the panel installer: 35 checks.
  - Windows Sandbox, the agent installed as a service with a real Counter-Strike: Source server: 100 checks. srcds became ready over RCON and stopped over RCON while it ran as its own account, so the RCON peer check holds in production. The browsers' port asks for no certificate.
  - The Hyper-V VM with a virtual TPM: 91 checks (see Phase 16).

## Phase 17 — SFTP access to server files · *in progress* (essential, at the user's request)

Like AMP, TCAdmin or Pterodactyl, every server's files can be reached with an SFTP client (WinSCP, FileZilla, Windows' own `sftp`), not only through the web file manager. Uploading many maps or mods and syncing folders needs this.

**Decided with the user (2026-10-13):**
- Users sign in with their own panel user name and password.
- They see only their own servers: those where they hold `server.files.view`.
- No plain FTP: it sends passwords in clear.
- (2026-10-16) SFTP is on by default (the installer opens its port; Settings can turn it off); SSH public keys come in a later phase; names with invisible format characters (such as U+202E) are left out of SFTP listings and counted, since an SFTP client cannot show them marked as the web does.

**Implemented so far** (steps A to C, on the branch `feat/phase17-sftp`):
- The agent protocol and the panel's file API for SFTP (paged listings, stat, downloads from an offset, streamed uploads with append, replacing rename, times).
- The SFTP server in the panel (`src/Pulsar.Api/Sftp`, docs/guides/sftp.md): password and two-factor sign-in with the browser's rules, own servers only, audit, limits, the host key kept encrypted in the database, settings (on or off, port, public host) at runtime.
- The web: an SFTP access section on the account page (address, copy buttons, host key fingerprint, two-factor note), a connect hint in a server's Files tab, and the administrator's settings in Nodes → SFTP (confirmation before turning it off or changing the port).
- The installer: `-SftpPort` (2022 by default) with its own firewall rule `Pulsar-Panel-SFTP`, removed at an uninstall; it prints the host key fingerprint at the end.
- SSH public keys (2026-10-19, the user's decisions: a key alone signs in, a two-factor user too, the key being the second factor; adding one takes the password and the code; Ed25519 through BouncyCastle, ECDSA and RSA with SHA-2 only): added and deleted on the account page, deleted all at once by an administrator, audited; the SFTP server takes the `publickey` method with the password's account checks, never counting wrong keys towards the lockout (docs/guides/sftp.md, "SSH keys"; doc 08, T43).
- Still to do: the SSH.NET tests against the real agent and the Sandbox run with the SFTP port open (Done when 4 to 9 are verified by them), and WinSCP and FileZilla with keys.

**Design** (the spike, docs/spikes/0003-sftp-server.md, confirmed `Microsoft.DevTunnels.Ssh` with an SFTP v3 handler of our own: Windows `sftp`, OpenSSH and SSH.NET sign in by keyboard-interactive, one prompt per round, password then the two-factor code; shell, exec and forwarding are refused):
- *Where it runs: the panel, one port for every node.*
  - Nodes accept no inbound connections in Pulsar's design (the agent connects out; nodes may be behind NAT), and the panel already pipes file bytes between browsers and agents through transfers (doc 03, "Large transfers").
  - So the panel hosts the SFTP server and each operation becomes the file commands and transfers the web file manager uses. The agent still checks every path and acts as the server's Windows account, the panel still checks permissions and audits, and no new trust is given to anything.
  - Port 2022 by default (22 is usually the machine's own SSH), configurable, with its own firewall rule from the installer (`-SftpPort`) and a switch to turn SFTP off.
  - A direct per-node SFTP, as Pterodactyl's Wings does, is left for later, for heavy traffic.
- *Sign-in.*
  - User name and password go through the same checks as the browser sign-in: Identity's lockout, disabled users, the per-address rate limit, and the "failed sign-in" audit. A user who must use two-factor authentication (their own choice or a role's) is asked for the code with keyboard-interactive authentication after the password; WinSCP, FileZilla and OpenSSH support it. SFTP must never be a way around two-factor authentication (2026-10-19: an SSH key the user added is the second factor itself, and adding one takes the password and the code).
  - SSH public keys added on the account page, like API keys (done: a key alone signs in, also for a two-factor user, and adding one takes the password and the code).
  - User name `name` shows every server of the user; `name.<shortId>` opens that one server as the root, which is handy for a bookmark per server.
- *What the user sees.*
  - The root lists one folder per server the user may see, named after the server (made safe for a file name) with its short id.
  - Write operations need `server.files.edit` on that server.
  - Permissions are checked at every operation, and a change of roles or a disabled user closes their SFTP sessions, as it does their browser connections.
  - The same rules as the web file manager apply: `.pulsar` and the Steam login folder hidden, links listed but never followed, changes refused while an install, update or restore runs (`server.busy`), and a server on a node that is offline answers with an error, not an empty folder.
- *SSH library.*
  - The candidates:
    - `Microsoft.DevTunnels.Ssh` (MIT, Microsoft): a maintained SSH2 client and server over any stream, with modern algorithms (ECDH and DH-group14/16 key exchange, ECDSA and RSA-SHA2 host keys, AES-GCM). It has no SFTP, and its server-side password and keyboard-interactive hooks must be confirmed.
    - An SFTP version 3 server over the SSH channel. Either our own, with a small protocol and full control over each request, or `SFTPServer` (MIT, small and little maintained, with an `ISFTPHandler` for a virtual file system), to be judged on its code.
  - Fallbacks: a Go sidecar on `golang.org/x/crypto/ssh` and `pkg/sftp`, which is what Wings uses; a commercial library such as Rebex does not fit an AGPL project.
  - SSH.NET (MIT, client only) drives the automated tests.
- *What the agent protocol needs (additive, behind an agent capability; older agents' servers say SFTP needs an agent update):*
  - listings by pages, since SFTP must list every entry and the web listing stops at 2,000;
  - a `stat` of one path;
  - downloads from an offset, for resumed downloads and clients that read in parallel (requests within a window are reordered, and a seek back opens a new transfer);
  - uploads of unknown length up to a limit, committed when the handle closes (the temporary file and single rename are kept), plus an append mode for resumed uploads (FileZilla writes at the end of the existing file; WinSCP uploads to `.filepart` and renames);
  - a rename that may replace (`posix-rename@openssh.com`);
  - setting a file's modification time (clients preserve times; permissions are accepted and ignored, Windows has none to set).
  - Refused: symbolic links, and every channel that is not the `sftp` subsystem (no shell, exec, port or agent forwarding).
- *Security (doc 08 gets a threat entry):*
  - The host key is generated at install, kept with the panel's keys, and its SHA-256 fingerprint is shown on the account page and by the installer, so users can check it at their first connection.
  - Only modern algorithms are offered.
  - Bounded sessions per user and per address, open handles per session and idle time.
  - The same upload limit as the web.
  - Audit as in the web file manager (uploads, deletions, renames, folders, downloads, sign-ins), with the actor's address.
- *Web:* the account page and the Files tab show how to connect (`sftp://name@host:2022/<server folder>`, a copy button, the host key's fingerprint). The panel's settings let an administrator turn SFTP on or off and change its port and public host.

**Done when:**
1. WinSCP, FileZilla and Windows' `sftp` sign in with a panel user's name and password and see only that user's servers.
2. A user who may only look downloads but cannot change anything.
3. A two-factor user is asked for the code.
4. A 2 GB map uploads and downloads, and both resume after a dropped connection.
5. `server.cfg` edited in WinSCP's editor is saved over the original.
6. A folder sync works.
7. Every change is in the audit log.
8. Shell, exec and port forwarding are refused, and repeated wrong passwords lock the account like the browser sign-in.
9. The tests drive it with SSH.NET against the real agent, and the Sandbox installs the panel with its SFTP port open.

## Phase 18 — Easy install · *next* (at the user's request)

Installing Pulsar should be as easy as AMP's: download one program, click through a few pages, and finish in the browser. Today it takes a separate PostgreSQL install, an administrator PowerShell, console passwords, and copying the panel's certificate to every node.

**Decided with the user (2026-10-16):**
- One `PulsarSetup.exe` with a wizard, and a `PulsarAgentSetup.exe` for nodes on other machines. Nodes join with the panel's address and a code from **Add node**.
- The setup downloads the latest version of what Pulsar needs at install time, rather than carrying copies that age:
  - **PostgreSQL:** the newest release of major version 18;
  - **.NET:** the newest .NET 10 and ASP.NET Core runtimes, which Microsoft Update then keeps patched;
  - **Pulsar itself:** stays inside the setup while the repository is private, since GitHub only serves a private repository's releases to signed-in users. Once the repository is public, the setup can download the latest Pulsar too.
- Inno Setup 7 builds both setups. Its license allows any use; its site asks commercial users above USD 5,000 of revenue to buy a license.

**Design:**
- *Wizard.*
  - Pages, in English and Spanish:
    1. What to install: the panel and this PC as a node, the panel only, or a node only.
    2. The game servers' folder.
    3. *Advanced*, closed by default: ports, host name, a certificate of your own, public networks in the firewall, an existing PostgreSQL.
  - Underneath it runs `Install-Pulsar.ps1` without prompts, so every check the script makes still applies, and the ZIP stays published for automation.
  - Uninstalling goes through Windows' *Apps*. A newer setup run on an installed machine updates it.
- *Downloads.* Only over HTTPS. A file that cannot be checked is not run, and the setup changes nothing.
  - **PostgreSQL.**
    - Latest version: read from postgresql.org's list of versions. The setup never moves to a newer major version on its own, because that needs a data upgrade.
    - What is downloaded: EDB's Windows installer, which is Authenticode-signed by EnterpriseDB. The setup checks that signature before running it. EDB's binaries ZIP is not used: almost none of its files are signed.
    - How it is installed: unattended, without pgAdmin or StackBuilder, listening on this machine only, with a random superuser password given in an options file (never on a command line, which other programs can read). The password is used once and not kept.
    - No internet: the setup offers an existing PostgreSQL instead.
  - **.NET.** The runtimes' installers are checked against the SHA-512 in Microsoft's release metadata (`releases.json`) and against Microsoft's signature. Pulsar's panel and agent become framework-dependent.
- *First administrator.*
  - A wizard page asks for the administrator's name and password (twice). The script needs that session to add this PC as a node and to apply the SFTP port.
  - The password reaches the script outside any command line: through the elevated process's environment or a temporary file only SYSTEM and Administrators can read, deleted at once.
  - At the end the setup shows the panel's sign-in page to open (it runs as administrator, so it starts no browser).
- *Nodes on other machines.*
  - **Add node** shows a code and a link to `PulsarAgentSetup.exe`.
  - The code carries the enrollment token, the panel key's fingerprint and the SHA-256 of the panel's TLS certificate.
  - The agent setup asks for the panel's address and the code. It reads the certificate the panel presents, checks it against that hash (not a CA, server authentication only, the host is in its names), and the agent pins it: it accepts that certificate and no other, and the machine's certificate stores are not changed. Nobody copies `panel.cer` by hand anymore.
- *Address changes.*
  - **`Install-Pulsar.ps1 -RenewCertificate`** (elevated, on the panel's PC) makes the self-signed certificate again for the PC's names and current addresses, with the install's own functions, and swaps it in. The old files are kept until the panel answers with the new certificate on its ports; the trust the install gave the old certificate on that PC moves to the new one (a certificate the install did not trust is left alone); any failure puts the old certificate back.
  - It refuses a panel that is not an install of the script, and a certificate that is not the installer's own self-signed one (exit codes 2, 3, 6).
  - Nodes that pinned the old certificate stay offline until pinned again, and the script prints the steps. A Let's Encrypt public IP certificate is unaffected: the panel serves it per connection to the internet only, and the installer's certificate stays what the loopback, the local network and the agents' port get (doc 08, T40).
- *Not yet:* code signing for the setups. Until it is in place, SmartScreen asks for **Run anyway**.

**Done when:**
1. On a clean Windows (Sandbox), `PulsarSetup.exe` with the defaults ends showing the address of the panel's sign-in page (it opens nothing itself), which, copied into a browser, opens it.
   - It downloaded the latest PostgreSQL 18 and .NET 10 and checked them.
   - PostgreSQL listens only on this machine.
   - No password appears in any process's command line.
2. The new administrator signs in, and this PC shows as an online node.
3. `PulsarAgentSetup.exe` on a second machine joins with the address and the code, with no certificate copied by hand.
4. Without internet, the setup says so and changes nothing (or uses an existing PostgreSQL when given one).
5. Uninstalling from *Apps* removes Pulsar's services, firewall rules and trust, and keeps the data unless asked.
6. A tampered or wrongly signed download is refused.
7. After the panel's PC gets a new address, `-RenewCertificate` makes a certificate that lists it, the panel answers with it, and a failure at any point leaves the old certificate in place and answering.

## Phase 19 — One-click updates · *in progress* (2026-10-20, at the user's request)

The panel tells its administrators when a new version of Pulsar is out and, with one button, updates the panel's machine and
the other nodes, without anyone carrying a setup to each PC, and without breaking anything.

**Decided with the user (2026-10-20):**
- Updates come from the project's own GitHub releases (`github.com/Ayrton09/Pulsar`) and nowhere else: the source is fixed in
  the code. The repository is made public with the first release, so installations download anonymously (no token in any
  panel).
- Each release carries a manifest signed with an update key that lives on the maintainer's PC (never on GitHub); its public
  key is built into Pulsar. Releases are built and signed there (`Build-Release.ps1 -UpdateSigningKey`) and uploaded with
  `Publish-GitHubRelease.ps1` (docs/guides/releasing.md): whoever takes the GitHub account cannot publish an update that
  installations take.
- Everything updates from the panel: the panel's machine (the panel and its agent), and every other node.
- The panel looks for a new release once a day, with a switch to turn that off (GitHub sees the panel's address when it
  looks), and a *Check now* button.

**Design:**
- *The release.* `pulsar-update.json` names the version, the oldest installed version that may update to it directly, the
  notes (plain text) and every file with its size and SHA-256; `pulsar-update.json.sig` is an ECDSA P-256 signature of its
  exact bytes, for its own purpose (`pulsar-update-manifest-v1`), by a key in `UpdateKeys.Trusted`
  (`src/Pulsar.Contracts/Updates.cs`). The maintainer's tool, `pulsar-update-signer` (`tools/Pulsar.UpdateSigner`), keeps the
  key in a file encrypted with a passphrase (AES-256, PBKDF2-SHA256, a million iterations) and signs only a well-formed
  manifest with a trusted key.
- *The panel* looks at `releases/latest/download/pulsar-update.json` and its signature (redirects followed by hand, only to
  GitHub's release storage, over HTTPS, both files bounded), keeps the newest release whose signature verifies, as its signed
  bytes and their SHA-256, and offers it to `settings.manage` (Administration → Updates, and a banner).
  Updating asks for the password and, with two-factor authentication on, the code; it takes the session cookie only (never an
  API key). The panel's machine (its agent announces `node.panel_host`) goes first: a node newer than its panel is refused.
- *The node.* The panel sends a signed `UpdatePulsarCommand` with the version, the manifest's SHA-256 and its own id for the request, nothing else. The
  agent (the PulsarAgent service, as SYSTEM; not one the agent MSI maintains, nor a development one) downloads the manifest,
  its signature and the ZIP from GitHub itself, checks the manifest is that one, signed by a key it trusts, newer than itself
  and from a version it may update, checks the ZIP's size and SHA-256, unpacks it (every entry inside the folder, bounded)
  into `%ProgramData%\Pulsar\Updates`, which only SYSTEM and Administrators can change, and starts the release's
  `Install-Pulsar.ps1 -Update -Unattended` detached.
- *Without breaking anything.* `Install-Pulsar.ps1 -Update` backs up the database and the programs first, updates, checks
  that the panel answers and the agent connects, and puts the old version back (programs and database) when they do not;
  it writes how it ended in `Updates\last-update.json`, which the agent reports after each Hello (docs/guides/update.md).
  Every report names the request it is about (the agent passes the id to the installer, `-UpdateAttempt`, which writes it in
  its result), and the panel takes a request's own reports only, in order: an earlier request's result sent again after a
  reconnection never ends a later one of the same version, and a step that arrives late never moves one back (*1.0.3*;
  reports without an id, from agents and releases before it, are taken in order only, an end once the agent said it began).
  Game servers keep running in their host processes throughout.

**Done when:**
1. A release built and signed on the maintainer's PC and published on GitHub is offered by an installed panel, with its notes,
   and refused when it is signed by any other key or changed after signing.
2. *Update the panel* updates the panel's PC (panel and agent) with its game servers running; the other nodes then update
   from the panel.
3. An update whose new version does not start puts the old one back by itself, and the panel says so.
4. No password, key or token appears on a command line or in a log; an API key cannot start an update.

## Post-v1 (tentative order)

Linux agent. (Server hosts and bulk enrollment: Phase 14; backups of running games: Phase 15; client certificates for agents: Phase 16; SFTP: Phase 17.)

A stop step that closes a game's window (`WM_CLOSE`, as `taskkill` without `/F` does), for games whose server is a windowed program that neither reads its console nor takes Ctrl+C: Core Keeper (checked on 2026-10-24: `CoreKeeperServer.exe` is a GUI program, Ctrl+C left it running until the stop timed out, and its own launch script stops it with `taskkill`). The host would post the message on the game's own desktop, which needs checking with servers run as their own accounts by the agent's service.

Satisfactory, once its worlds can live in the server folder: its dedicated server keeps them in the running account's `%LOCALAPPDATA%\FactoryGame\Saved\SaveGames` (no argument moves them, and manifests may not redirect that variable), so backups would miss them; its name and passwords are set in the game when it is claimed.

More game options where a new patch format is needed (1.0.8 added every option other panels expose that fits the existing formats): Project Zomboid's sandbox settings (zombie population, loot, XP, day length) live in `<server>_SandboxVars.lua`, a nested Lua table the game writes at its first start; Unturned's gameplay tuning lives in `Config.txt`, sectioned text of its own.

Minecraft: Bedrock Edition beyond what 1.0.8 does: its player count (the RakNet ping went away with NetherNet, and NetherNet's status, if any, is not documented), and firewall rules for the UDP range set in its UDP ports option (Pulsar's rules cover allocated ports only).

## 1.0.9 — What other panels have (2026-10-25, at the user's request)

A comparison with AMP, Pterodactyl/Pelican, TCAdmin and WindowsGSM gave fifteen points, done in this order, each one checked before the next (the Linux agent stays post-v1):

1. **Game updates** · *done*: checks against Steam and Mojang, shown and notified, installed by themselves as a sequence (warn, stop, update, start again; when nobody plays if asked). Doc 05, "Implementation (1.0.9)"; doc 03, "Game update checks".
2. **Scheduled tasks of several steps** · *done*: steps one after the other (message the players, command, wait, stop, start, restart, back up, update) with presets, conditions (only while running, only when nobody plays, only when a game update is out), cancel. Doc 05, "Implementation (1.0.9): scheduled tasks of several steps"; doc 07, "Schedules".
3. **Minecraft server types, mods, plugins and modpacks** · *done*: Paper, Purpur, Fabric, Forge and NeoForge besides Mojang's server (doc 04, "Server types"); mods and plugins from Modrinth and CurseForge with what they require, versions, updates and removal (doc 04, "Mods"); Modrinth modpacks and CurseForge packs through their authors' server files (doc 04, "Modpacks"); CurseForge with each administrator's own key. Follow-ups: list a modpack's own projects (from its files' checksums) so the catalog marks and refuses mods the pack already has; one catalog's project and the other's can still both be installed (the same mod twice).
4. Player actions from the Players card: kick, ban, whitelist, operator.
5. Compressing and extracting archives in the file manager.
6. More games (Space Engineers, Conan Exiles, DayZ, Don't Starve Together, Terraria, Squad, Insurgency: Sandstorm, Soulmask, Icarus, Abiotic Factor, Euro Truck Simulator 2, Killing Floor 2…).
7. More mod sources: Thunderstore, CurseForge for ARK, Bohemia's workshop, uMod plugins.
8. Choosing a game's Steam branch or version.
9. Moving a server to another node, cloning it, changing its game.
10. Restoring single files from a backup.
11. More notifications: CPU or memory high for a while, a server back online.
12. Signing in with Discord, Google or an OIDC provider.
13. **A light theme and a better phone layout** · *done*: dark by default, light or the system's from My account or the menu; row actions as a sheet on phones, 16 px fields (doc 07).
14. Actions on several servers at once.
15. Hosting: quotas per user, users creating their servers within them, suspending servers, billing integration.

## 1.0.10 — Fixes and template updates (2026-10-26)

- A server prepared again (moving it to a newer template, say) no longer refuses the account the agent made for it (doc 06, "Isolation").
- Template updates: newer versions of a server's game template are notified once, or moved to by themselves with the game updates' warnings (doc 02, "Template upgrades"; doc 05).
- A release page shows its version once (the notes alone under its title).

## 1.0.11 — Players and files (2026-10-26, at the user's request)

- Players from the panel: kick, ban, whitelist and admins with the game's own commands, from the Players card or by name, and the game's lists (doc 04, "Players"; doc 07; doc 08, "Player actions"). New permission `server.players.manage`; agent capability `manifest.player_commands`.
- A node whose agent does not read a newer template yet keeps it pending (its servers move once the node updates) instead of refusing it.
- A backup read live (no snapshot) leaves out a file the game keeps locked (Minecraft's `session.lock`) instead of failing; Minecraft's template takes the save confirmation newer versions print (`System chat: Saved the game`), so a backup no longer waits two minutes for it.
- Zips in the file manager: zip files and folders, unzip a `.zip` into a folder, while the game keeps running (doc 03, "Archives"; doc 07; doc 08, "File manager"). Agent capabilities `server.files_compress` and `server.files_extract`.

## 1.0.12 — More games (2026-10-26, at the user's request)

- Ten more games, each checked on a real server through a development panel and agent (`games/<key>/README.md`): Abiotic Factor, Conan Exiles, Don't Starve Together (offline, without a Klei token), Icarus, Insurgency: Sandstorm, Killing Floor 2, Soulmask, Space Engineers, Squad, and Terraria through TShock. DayZ was dropped; Euro Truck Simulator 2 waits for a way to make its `server_packages` without the game's client.
- EXILED for SCP: Secret Laboratory from its Mods tab, and its plugins (LabAPI's and EXILED's) in the server's folder, where the file manager and backups reach them.
- New manifest features, each read only by agents that announce it (doc 03): Ctrl+C for games run with pipes (`manifest.pipes_ctrl_c`), several sections of one ini (`manifest.ini_sections`), a log file as the console with pipes and log names by pattern (`manifest.log_file_console`), `runtime.userProfile` (`manifest.user_profile`), the `text` and `xml` formats (`manifest.text_config`), `.tar.gz` addons (`addons.tar_gz`), and the `githubRelease` and `dotnetRuntime` install steps (`install.github_release`, `install.dotnet_runtime`) (doc 04).
- Secrets never show in a server's console (doc 04, "Console"; doc 08, T47).
- New servers start with their node's agent by default (`auto_start` on at creation); a server's settings keep their Save button and its result in view at the bottom of the form; a node can be renamed from beside its name (at the user's request).

## 1.0.13 — SCP: Secret Laboratory's console (2026-10-27, at the user's request)

- SCP: Secret Laboratory 1.4.1: LocalAdmin without `--printStd`, so the console no longer carries the engine's own warnings, and the game with `-silent-crashes` (`games/scpsl/README.md`, "Console").

## 1.0.14 — Long console lines and more mod sources (2026-10-27, at the user's request)

- uMod's plugins for Rust (gap point 7): searched, installed, updated and removed from the Mods tab into the folder of Oxide or Carbon, whichever the server has, while it runs; each file checked against uMod's SHA-1 (doc 04, "Mods"; doc 03, "Mods"; doc 08, T51). Mod catalogs can be typed by the game's addons, take files other than `.jar` in folders up to three names deep, and change files while the game runs (agent capability `mods.umod`).
- Thunderstore's server mods for Valheim (gap point 7): BepInEx installed as an addon from Thunderstore, then packages of Valheim's server-side category with the packages they need, unpacked into BepInEx's folder, updated and removed (doc 04, "Mods"; doc 08, T52: Thunderstore publishes no checksum). Agent capability `mods.thunderstore`.
- ARK: Survival Ascended's mods by name (gap point 7): its `mods` setting is a list editor that searches the game's mods on CurseForge and names them with the administrator's key; the server still downloads them itself (doc 04, "Variables"; doc 07, "Workshop"; doc 08, T53). Agent capability `manifest.catalog_lists`.
- Arma Reforger's mods by name (gap point 7): its `mods` setting is a list editor that searches the game's Workshop and names its mods, read from the Workshop's pages; they go into `server.json`'s `game.mods`, merged so that mods added there by hand, and the versions pinned on the panel's, stay (doc 04, "Configuration files"; doc 08, T54).
- Workshop items added one after another, before the first is looked up on Steam, all go into the list (only the last one did).
- Two more games, each checked on a real server (games/<key>/README.md): Half-Life 2: Deathmatch (`hl2dm`) and Pirates, Vikings, and Knights II (`pvkii`, the 64-bit srcds), Source servers like CS:S. Counter-Strike 1.6, Half-Life Deathmatch (GoldSrc's HLDS, whose console and RCON differ) and Synergy (it needs Half-Life 2 itself, downloaded with an account that owns it) wait.
- A server's Steam branch (gap point 8): the game's branches as Steam lists them, chosen on the server's page; changing it backs the stopped server up, then installs the branch (doc 05, "Steam branches"; doc 03, "Game update checks"; doc 08, T55). Agent capability `steam.branches`.
- Lines wider than a pseudo console (220 columns), which the console host cuts once its screen is full, are joined again as the agent reads them, so a long link (SCP: Secret Laboratory's `!verify static`) comes whole and opens whole (doc 06, "Console: ConPTY vs pipes").

## 1.0.15 — GoldSrc (2026-10-27, at the user's request)

- Counter-Strike 1.6 (`cs16`) and Half-Life Deathmatch (`hldm`), GoldSrc's HLDS (app 90), each checked on a real server (games/<key>/README.md). Their console is `qconsole.log` and commands go over GoldSrc's RCON on UDP (`"protocol": "goldsrc"`, doc 04, "Console"): every process bound to the port must run as the server's account before anything is sent, and HLDS's console log and logs folder, which hold the password, are kept from the file manager, SFTP, zips and backups (`privatePaths`, doc 08, T56). Agent capability `manifest.goldsrc_rcon`.

## 1.0.16 — Change game, clone, transfer (gap point 9, at the user's request)

- Change a server's game (doc 02, "Changing a server's game"): files deleted without a backup (the user's choice), name, access, limits, backups and ports kept where they fit; checked on a real node (Terraria to Minecraft: Java Edition, then started).
- Clone a server (doc 02, "Cloning a server") onto its node or another, with a copy of its files relayed by the panel from a backup; checked on a real node (Minecraft: Java Edition cloned with its world, then started).
- Transfer a server to another node as the same server (doc 02, "Transferring a server to another node"), its files relayed by the panel, its old copy deleted once it moved, moved back as it was if it fails; checked on two real nodes (a Minecraft server moved with its world, then started; a node without free allocations refused at once).
- The six findings of the 2026-10-28 audit fixed (mods' writes undone, settling before loading, requirements' versions, sequence steps saved with their operation, agent update attempts, modpack zips).

## 1.0.17 — Second audit of 2026-10-28 (on e6a94aa)

- Clones and transfers copy every file (a whole backup, with the agent's records of mods, addons, pack and lists), never one that left a file out.
- Mods' requirements: a version needed by one project is kept against another's need; mods the server keeps are asked what they need.
- Files a failed mod change left behind are replaced by installing again; a pack written in part lists no pack.
- Clones take the source's mods, addons, pack and Steam branch along.
- A template move is saved with its provisioning and the step following it; changing a game ends the server's sequences under way.

## 1.0.18 — Restoring some files from a backup, more notifications, sign-in providers (gap points 10 to 12)

- A backup's folders are listed from its archive, and a restore can take only some of its files and folders, cleanly inside them if asked (doc 03, "Backups"; doc 07).
- Signing in with Discord, Google or an OpenID Connect provider (gap point 12): only accounts that exist, once their owner linked the provider's account with the password; the panel's two-step verification still asked (doc 07, "Sign-in providers"; doc 08, T59).
- More notifications (gap point 11): a server or node using much CPU or memory for a while (thresholds in the notification settings), and a server running again after a crash (doc 02, "Notifications"; doc 07).

## 1.0.20 — A light theme, phones (gap point 13)

- A light theme beside the dark one, which stays the default: chosen in My account (Dark, Light, or the system's) or with the menu's button, and kept in the browser (doc 07).
- On phones, a table row's actions open as a sheet from the bottom, and text fields no longer make iOS zoom in (doc 07).

## Before the first public release

- A Windows installer: Phase 18 (`PulsarSetup.exe`, Inno Setup, on top of `Install-Pulsar.ps1`, with the ZIP still published for automation).
- Code signing for the installer and the programs, so SmartScreen does not warn: built into `Build-Release.ps1` and off until the project has a certificate (next section).
- A reporting contact in `CODE_OF_CONDUCT.md`; branch protection for `main`; CodeQL on (skipped while the repository is private).
- Tag `v1.0.0` (the release workflow builds, attests and drafts the release). The version is 1.0.0 until then.

### Code signing

`Build-Release.ps1` builds an unsigned release unless it is given a signing option (`-RequireSigning` makes an unsigned build an error, for a release that must not go out without). With one, it Authenticode-signs, with `signtool.exe` from the Windows SDK (SHA-256 digests, an RFC 3161 timestamp on every signature):

- the panel's and the agent's own programs, *before* they are packed into the ZIP, the MSI and the setups, so the packed copies are signed. They are an explicit list derived from the project files: the assemblies of `Pulsar.Api` and `PulsarAgent` and of the projects they reference, the two apphosts (`Pulsar.Api.exe`, `PulsarAgent.exe`) and satellite assemblies. Any other `Pulsar*.exe` or `Pulsar*.dll` in a published folder (a new project, or something a build left behind) fails the build instead of being signed or skipped;
- the agent MSI, after it is built;
- both setups, which Inno Setup signs through its `SignTool` directive with `SignedUninstaller=yes` (`scripts/release/setup/Common.iss`): signing the finished setup afterwards would leave the uninstaller unsigned. A signed uninstaller keeps its messages in `unins000.msg` beside it.

Not signed: the ZIP (it has its SHA-256 and, from CI, a provenance attestation), the third-party libraries (they keep their publishers' signatures), and the PowerShell scripts (they run with `-ExecutionPolicy Bypass`, so a signature would never be checked).

**Getting a certificate.** Any of these:

- *SignPath Foundation*: free for open-source projects (apply at signpath.org; it asks for an OSI-approved license, which the AGPL v3 is, a project that is public and built by CI, and a code signing policy published in the repository). Its key stays in SignPath's HSM and it signs the files it is sent, so it is **not** a parameter of this script: it needs release workflow stages that send the programs, build the MSI and the setups from the signed ones, and send those too. Not built; decide when the application is approved.
- *Azure Trusted Signing*: an Azure subscription, a Trusted Signing account, an identity validation and a *Public Trust* certificate profile (check which countries and legal forms Microsoft validates: it began with organizations in the USA, Canada, the EU and the UK, and individuals in the USA and Canada). Sign in to Azure the usual ways (`az login`, `AZURE_*` environment variables, a managed identity); the account needs the *Trusted Signing Certificate Profile Signer* role. Install Microsoft's Trusted Signing client tools, which install per user, in a folder the current user can write: as administrator, copy that folder to one under `C:\Program Files\` (for example `C:\Program Files\Pulsar signing\TrustedSigningClient`) and give the script that copy, since it refuses a client it could be swapped for. Its certificates last a few days, which is why every signature is timestamped: use Microsoft's timestamp authority with it.
- *A certificate from a certificate authority*: since 2023 the private key of a publicly trusted code-signing certificate lives on hardware (a USB token, or the authority's cloud HSM). Install the token's software so the certificate shows up under `Cert:\CurrentUser\My`; signtool asks for the token's PIN for each run (a few times per release).

**Running it.** Needs the Windows SDK's *Signing Tools for Desktop Apps* (signtool.exe; 10.0.22621 or later for Trusted Signing), installed where only administrators can change it (Program Files, as the SDK's installer does), and, for the setups, Inno Setup 7 installed for all users (in Program Files): the compiler runs signtool with the signing credentials, so a signed release refuses a per-user install of it (`%LOCALAPPDATA%\Programs`), which an unsigned one still takes; install it for all users as administrator, or name such a copy with `-InnoSetupCompiler`.

```powershell
# A certificate, or the one on a hardware token, in the current user's Personal store, by its thumbprint
Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Format-List Subject, Thumbprint, NotAfter
.\scripts\release\Build-Release.ps1 -Version 1.0.0 -RequireSetups -RequireSigning -SigningThumbprint 0123456789ABCDEF0123456789ABCDEF01234567

# Azure Trusted Signing (the metadata file in a folder under %LOCALAPPDATA%, which is the current user's alone)
.\scripts\release\Build-Release.ps1 -Version 1.0.0 -RequireSetups -RequireSigning -TrustedSigningMetadata "$env:LOCALAPPDATA\PulsarSigning\metadata.json" `
    -TrustedSigningDlib "C:\Program Files\Pulsar signing\TrustedSigningClient\Azure.CodeSigning.Dlib.dll" -TimestampUrl http://timestamp.acs.microsoft.com
```

`metadata.json` holds `{ "Endpoint": "https://eus.codesigning.azure.net", "CodeSigningAccountName": "<account>", "CertificateProfileName": "<profile>" }` and no secret. The script reads those three values, requires the endpoint to be `https://<region>.codesigning.azure.net`, and gives signtool a fresh file of its own, never this one. The other options: `-SignTool` (another `signtool.exe`, in a folder only administrators can change; by default the newest in `Windows Kits\10\bin\<version>\x64`) and `-TimestampUrl` (default `http://timestamp.digicert.com`). The two ways exclude each other, and a signing option without one of them is refused rather than ignored.

**What the script checks.** Before the long builds, and again before each use: that `signtool.exe` (and the `mssign32.dll` and `wintrust.dll` the SDK ships beside it) is validly signed by Microsoft Corporation, and that it, the Trusted Signing client and the libraries beside it (which must be Microsoft's too), and the Inno Setup compiler, are in a folder only SYSTEM, Administrators and TrustedInstaller can change, the rule `Install-Pulsar.ps1` applies to its own folder (a folder the user can write in is refused, however well signed what is in it: a DLL planted beside a program is loaded before Windows' own). signtool runs in place from there. For a thumbprint: that the certificate is in the store with its key, valid now and for code signing. Everything is built in a work folder inside the output folder, and the programs are verified again, right before the MSI, the ZIP and each setup are made of them: every file passes `signtool verify /pa` and has a valid embedded signature with a timestamp, from one certificate (one subject for the whole release), or the build fails; so does any signtool warning. Only when all of that is done does the release move into the output folder, so a failed build leaves nothing behind. `release.json` then records `signing` (`signed` and the signer's subject), written once the programs and the MSI are signed. Check a result by hand: `signtool verify /pa /v <file>`, or `Get-AuthenticodeSignature <file>`.

**The maintainer's PC.** A release built there compiles and runs third-party code (NuGet packages' build tasks, npm packages' plugins) as the maintainer, before anything is signed, and whatever the maintainer's account runs can use a certificate in the user's store (a key marked non-exportable can still sign) or the Azure sign-in kept in the profile. The script narrows this: the web build runs without install scripts, MSBuild's nodes and the compiler server do not stay resident (`-nodeReuse:false`, `UseSharedCompilation=false`, `dotnet build-server shutdown`), the `AZURE_*` variables are taken out of the environment of everything it starts but signtool and the Inno Setup compiler that runs it, and only the listed programs are signed. It cannot remove the risk: prefer a certificate on a hardware token with a PIN for each use (a prompt nobody expected is the alarm) or a Trusted Signing identity that holds the signer role on one certificate profile only, and build on a PC kept for releases. Signing never makes the build's own code trustworthy: review the dependency updates that go into a release.

**CI.** `release.yml` does not sign: its build job runs the repository's npm and dotnet code and must hold no signing identity. Signing there needs a job of its own (SignPath's, or Azure with a federated credential in a job that runs no repository code) and the programs signed before the MSI and the setups are built. Until then a release is signed on the maintainer's machine, where it has no provenance attestation: publish its SHA-256 files with it.

## Cross-cutting testing strategy

| Layer | Tool | Focus |
|---|---|---|
| Architecture | xUnit (`Pulsar.Architecture.Tests`) | Project dependency rules |
| Domain | xUnit | State machines, permission evaluation, anti-escalation rules |
| Manifests | xUnit | Schema, validation, rendering, injection cases, every `games/*` |
| Application/API | xUnit + `WebApplicationFactory` + real PostgreSQL (`PULSAR_TEST_POSTGRES`, a fresh database per test class; CI starts the PostgreSQL preinstalled on the runners) | Authorization (endpoint × role matrix), IDOR, flows, migrations |
| Protocol | xUnit | Signature, replay, expiry, versioning, revoked session |
| Agent Core | xUnit | Supervisor, mailbox, SteamCMD parser, path validator |
| Agent Windows | xUnit (Windows runner) | Jobs, accounts, ACL, ConPTY, junctions, process cleanup |
| Frontend | Vitest + Testing Library | Components, realtime hooks, permissions in the UI |
| E2E | Playwright | Complete v1 flow |

All .NET tests run on Microsoft.Testing.Platform (xUnit v3). Commits follow Conventional Commits, one coherent change per commit, small pull requests.
