# 07 — Frontend architecture

> Status: **proposal under review**. The skeleton (Vite, React, TypeScript, Tailwind, i18n, Vitest, Playwright) exists since Phase 0; the rest arrives with the features that need it.

## Stack

| Need | Choice | Why | Status |
|---|---|---|---|
| Build | Vite + React + TypeScript (strict) | Requirement. No SSR. Type checking runs on TypeScript 7 (native compiler, `@typescript/native` → `tsc`); the `typescript` package is an alias of `@typescript/typescript6`, the TS 6 API that typescript-eslint and openapi-typescript still need (Microsoft's side-by-side setup). Drop the alias once those tools support TS 7. | In place |
| Styles | Tailwind CSS v4 | No CSS-in-JS runtime. | In place |
| i18n | i18next (en + es from day one) | Adding it later means touching every screen. | In place |
| Tests | Vitest + Testing Library; Playwright for E2E | Requirement. | In place |
| Routing | React Router (SPA/library mode) | Standard, no Node server. | Phase 1 |
| Server state | TanStack Query | Caching, invalidation, dedup; SignalR events write into the same cache. | Phase 1 |
| UI state | `useState`/context | No Redux/Zustand needed. | — |
| API client | `openapi-typescript` (types) + `openapi-fetch` (minimal client) | Types generated from the backend OpenAPI document; CI fails if the client is stale. | Phase 1 |
| Real time | `@microsoft/signalr` | Same protocol as the backend. | Phase 2 |
| Console | `@xterm/xterm` + `@xterm/addon-fit`, `@xterm/addon-search`, `@xterm/addon-web-links` | Requirement. The clipboard addon (OSC 52) is not loaded. | Phase 5 |
| Components | Own shadcn-style components (copied into the repo) on Radix primitives | Accessibility without a full component library. | Phase 1 |
| Icons | lucide-react (ISC) | One consistent stroke set; only the icons imported end up in the bundle. Icons are decoration (`aria-hidden`): a label always names the control. | UI pass after the audit |
| Forms | react-hook-form + zod | Client validation (the server always re-validates). | Phase 1 |
| Charts | uPlot (only for live CPU/RAM) | Very light and fast for time series. | Phase 5 |

Rule: every new npm dependency is justified in its pull request. The lockfile is committed, CI uses `npm ci`, Dependabot groups updates.

## Layout

```
src/Pulsar.Web/
├─ index.html
├─ vite.config.ts            build output → ../Pulsar.Api/wwwroot; dev proxy for /api, /hubs (ws), /agent, /health
├─ playwright.config.ts      E2E against the *published* panel
├─ e2e/
└─ src/
   ├─ app/                   App, route table, query client, i18n setup
   ├─ api/                   openapi.json (committed copy of the API document), schema.d.ts (generated),
   │                         client.ts (openapi-fetch + antiforgery middleware + ApiError)
   ├─ locales/en, es
   ├─ test/                  Vitest setup and a fake API (routes keyed by "METHOD /path")
   ├─ components/ui/         Button, Form (Field, Input, Select, Checkbox), Dialog (Radix), Display (Table,
   │                         Badge, Alert, PageHeader…): no business logic
   ├─ lib/                   permissions, error messages (translated by ProblemDetails code), formatting
   └─ features/              auth, shell, admin (Phase 1); later dashboard, servers, console, files, backups,
                             schedules, network, startup, activity, nodes
```

Each `feature` holds `api.ts` (TanStack Query hooks), its pages/components and tests next to the code. Pages that most sessions never open (administration) are loaded on demand through React Router's `lazy`.

## Routes

```
/login  /setup
/                                  Dashboard
/servers                           list; new servers are created in a dialog
/servers/:id?tab={overview|console|files|addons|backups|schedules|settings|network}
/templates                         games (bundled and imported templates)
/nodes                             list; new nodes are created in a dialog (also open to node.credentials.manage and settings.manage, for their sections)
/nodes/:id                         one page: usage, drives, allocations, credentials, administration
/admin/{users|roles|audit|notifications|backup-storage}
/account                           profile, password, 2FA, API keys
```

The server page has a fixed header (name, status, power actions, live CPU/RAM) and tabs (`?tab=`); tabs without permission are hidden. Why an operation failed (Recent operations) and why the node refused a power action are shown in the user's language from their codes (`errors.*`), the code itself only as a tooltip, and the agent's own words when a code has no translation. The node page is one page of sections. There is no settings page: the panel's own settings are in its `appsettings.Production.json` (docs/guides/install.md), notification settings in `/admin/notifications`.

*Bulk enrollment (Phase 14):* `/nodes` also lists the **reusable enrollment tokens** to users who manage node credentials (name, the start of the token, uses out of the maximum, expiry, whether its nodes wait for approval, and its state), with a dialog to create one (name, machines, days, "New nodes wait for my approval", checked by default) that shows the token once inside the two commands that use it: the agent MSI's silent install (`msiexec /i pulsar-agent-<version>-windows-x64.msi /qn PANELURL=… TOKEN=… PANELKEY=…`, the package's name from the panel's own version) and the installer script. Nodes waiting for approval show an "Awaiting approval" badge and, to users who manage nodes, a card above the list with a checkbox per node showing its machine's host name and Windows, the token that created it and its key fingerprint, none checked at first (a node that a leaked token adds while the page is open is never approved by accident), a "Select all" box and an "Approve N nodes" button that approves the checked ones (in requests of up to 200, the most the API takes, one after another); a node's page explains why its agent cannot connect yet and offers "Approve". The dashboard does not count them as offline. Usable tokens are listed first; expired, used-up and revoked ones fold under "Show N finished tokens".

*Who sees what on `/nodes`:* the page opens for `node.view`, `node.manage`, `node.credentials.manage` or `settings.manage`; the node list and the approval card need `node.view` or `node.manage` (the API refuses the list otherwise), the reusable tokens `node.credentials.manage`, the panel keys and agent connections `settings.manage`. The live channel's notice that nodes changed also refreshes the panel keys and the reusable tokens (uses). A dialog that creates something with a one-time secret (node, reusable token, API key) cannot be closed while the request is pending.

*Client certificates (Phase 16):* to users with `settings.manage`, `/nodes` ends with an "Agent connections" section. Without an agents' address it says how to add one (the Kestrel endpoint `Agents`, the installer's `-AgentsPort`). With one it shows the address, whether certificates are required, and either "every enrolled node connects with its client certificate" or how many still do not, listing the first hundred by name with whether they are connected and an "Update the agent" badge when their agent cannot move. "Require client certificates" asks first when nodes are listed (their sessions end), and "Stop requiring them" asks too; the count it asks with is read again at that moment, as is the count of the nodes that still trust only the old panel key when retiring it. A node's page shows its connection in the machine card: with a client certificate, with the session token alone, or with the session token alone and an agent to update.

*Phase 2:* nodes are created from a dialog on `/nodes` (no `/nodes/new` page) that ends with the one-time enrollment command (since Phase 9 the installer's: `Install-Pulsar.ps1 -Mode Agent -PanelUrl … -Token … -PanelKey …`, run in the release ZIP's folder), and `/nodes/:id` is a single page (machine, live usage, credentials, administration) until servers give it more to show. Node state refreshed by polling every 5 s until Phase 5 moved it to the live channel.

## Real time

- **One SignalR connection per tab** to `/hubs/panel`, authenticated by cookie.
- Explicit per-resource subscription: `useServerChannel(serverId)` calls `JoinServer(serverId)` (the hub authorizes `server.view`) and leaves on unmount; reference counted to avoid duplicates.
- Events → `queryClient.setQueryData(...)`: server state, metrics and operation progress update **without polling**.
- On reconnect: re-join active groups and invalidate visible queries (a single refetch to resync).
- Dashboard: a `dashboard` group with aggregates the panel emits at low frequency (≈5–10 s).

*Implementation (Phase 5):* `LiveProvider` opens the connection in the signed-in layout and reconnects forever (0.5 s doubling up to 30 s). Pages watch through hooks: `useWatchServers` and `useWatchNodes` (notices about the servers and nodes the user may see: every one with a global role, else per node and per server since Phase 11), `useServerMetrics(id)` and `useServerConsole(id, write)`. Watches are shared, reference counted and sent again after every reconnect (groups belong to a connection). Server and node notices refetch what changed (a server's settings excepted, after the page's own actions too: only a save or a template upgrade changes them, and a refetch would drop an admin's unsaved edits); operation progress, server usage and node usage are written into the cache; after a reconnect every visible query is fetched again (a server's settings excepted, for the same reason). While the channel is down, also when its first connection fails (a proxy refusing WebSockets), the sidebar says live updates are paused, and the session is read again when the connection drops and after every attempt that fails: the panel refuses a session that ended (it expired, or the user signed out in another tab, which also closes the user's live connections), so the route guard sends the user to sign in. A session, a node page, or a server's addons, backups, databases and schedules that cannot be fetched again keep their last data, with a notice and a retry, instead of an error that would drop unsaved edits, a shown password or an open dialog. The panel pushes these notices after every save that touches servers, operations, variables, allocations, nodes or credentials (an EF Core interceptor), so no code path forgets one. A refused watch answers `false` or `null` rather than throwing: SignalR logs every hub exception as an error. The dashboard composes the server and node lists on the client instead of a `dashboard` group, and has no low-disk alert (low disk is a notification event, see Notifications). *Phase 11 (node and server roles):* the session lists the permissions held on single nodes and servers; `can` stays global, `canOnServer` and `canOnNode` add a server's own and its node's roles (the server page, its tabs and actions, the node page, the create dialog's nodes), and `canAnywhere` decides navigation and route guards, whose pages then list only what the user may see. The user dialog has an "Access to nodes and servers" section to give and take roles on one node or server. *Phase 11:* a server that answers its query shows its players and map next to its usage, and a Players card on its overview (name, score, time on the server); names and the server's own name are the players' and the game's text, rendered as text (React escapes it; no markup is ever built from it).

*Usage history (Phase 13):* a server's overview and a node's page have a "Usage history" card: a 1 h / 24 h / 7 d / 30 d switch (pressed buttons in one row) and one chart per measure (CPU and memory; players too when the game answered queries in that period), each with its own axis, never two scales on one chart. The charts are drawn by the page itself as SVG (no chart library): the average as a 2 px line over a light area, broken where nothing was recorded, the axis rounded up to 1, 2, 2.5 or 5 times a power of ten (a node's memory axis ends at the machine's memory). A crosshair follows the pointer, or the arrow keys, Home and End when the chart has focus (Escape hides it), and reads out the time, the average and the peak; every point is also in a table for screen readers. History is fetched from `GET /servers/{id}/metrics` and `GET /nodes/{id}/metrics` (`range=hour|day|week|month`), again every minute for the two short ranges (a minute is written once it is over), and the previous chart stays dimmed while another range loads.

*Steam accounts (Phase 13):* `/admin/steam-accounts` (with `settings.manage`) lists, adds, edits and deletes the accounts SteamCMD signs in with (the password write-only, and left empty it is kept only while the Steam user name stays the same: `steam_account.password_required` otherwise; the page says to use an account of its own for servers). A server's Settings tab has a Steam account card next to the Resources card: who can change the caps (`server.limits.edit`) chooses an account or Anonymous, everyone else sees which one; a game that needs an account says so. When the server's last install or update failed with `steamcmd.guard_required`, its page shows a Steam Guard box (with `server.install`): the 5-character code from the mail or the mobile app, and the same operation again with it (an update also offers to verify every file, since the failed operation does not record whether it did). API: `GET /steam-accounts` (`settings.manage`, or `server.limits.edit` anywhere, to choose one), `POST`, `PUT /{id}`, `DELETE /{id}` (`settings.manage`; audited); `PUT /servers/{id}/steam-account` (`server.limits.edit`; audited); `POST /servers/{id}/install/steam-guard` `{steamGuardCode}` (a reinstall answering the code; `POST /install` keeps taking no body) and `steamGuardCode` in `POST /servers/{id}/update`. `ServerDetails.steam` says whether an account is needed and which one is used.

*Databases (Phase 13):* a server's `?tab=databases` (with `server.databases.view`) lists its MySQL/MariaDB databases: name (which is also its user), the address and port game servers connect to, the host and where its user may connect from, and how many of its limit it uses. With `server.databases.manage`: create one on a host its node may use (the name's suffix is typed, the full `s{short id}_{suffix}` is shown as it is typed), show a password, change it (after asking) and delete one (after asking); a created, changed or shown password appears in a connection-details box with a copy button until it is closed, and is never kept in the query cache. `/admin/database-hosts` (with `settings.manage`) adds, edits, tests and deletes hosts: address and port for the panel, the account (its password write-only; left empty it is kept, but only while the address, port and user stay the same, `database_host.password_required` otherwise), an optional address and port for game servers, *Connections from*, *Require TLS* (for a new host it follows the address as it is typed, on unless it is local or private, until it is ticked or cleared), and the nodes that may use it (when the nodes cannot be listed, since that takes `node.view`, `node.manage` or `server.create`, the dialog says so and whether the host applies to every node or stays limited to the ones it has); the list says which hosts require TLS. The database limit sits with the CPU and memory limits in the Resources card: empty CPU or memory fields remove those caps, an empty database field keeps the current limit (it is left out of the request), and a database limit outside 0–50 is refused before anything is sent, as is a field the browser cannot read as a number (a half-typed `1e`, which it reports as empty and would remove the cap). API: `GET /servers/{id}/databases` (view), `POST /servers/{id}/databases`, `GET …/{databaseId}/password`, `POST …/{databaseId}/rotate-password`, `DELETE …/{databaseId}` (manage; audited, reading a password too, the password never; answers with a password are `Cache-Control: no-store`); `GET|POST /database-hosts`, `PUT|DELETE /database-hosts/{id}`, `POST /database-hosts/{id}/test` (`settings.manage`; audited); `PUT /servers/{id}/limits` takes `databases` (null or left out keeps the current limit, while `cpuPercent` and `memoryMb` are replaced: null removes the cap). A host the panel cannot reach answers 502: `database_host.unreachable` with what the server said on the host routes (settings managers), `database.host_unreachable` without it on a server's routes. A host name already in use answers 409 `database_host.name_taken`.

## Console

- The `Terminal` component creates the xterm instance **once** and writes with `terminal.write()` from the event handler, **without going through React state** (a re-render per line would destroy performance).
- `WatchConsole(serverId, fromSequence)` (requires `server.console.view`) delivers backlog + stream. Output pushed before the answer arrives is held and written after it; each sequence is written once, in order. After a reconnect the browser asks for what came after the last entry it wrote.
- 5,000 lines of scrollback (configurable). Search (addon-search). "Clear" = local `terminal.clear()`, it does not affect the server.
- Optional timestamps: prefix generated client-side from each chunk's timestamp (exact in `pipes` mode, approximate in `pty`; documented). Not in v1 yet: CS2 and srcds already stamp their log lines.
- Input: a separate command field under the terminal, with history (↑/↓). Only visible with `server.console.write`, and disabled while the server is not running or its node is disconnected. Clearer for game consoles than typing into xterm, and it allows read-only consoles. A command is one line without control characters (the panel refuses them with `console.invalid_input`): a pasted tab becomes a space and other control characters are dropped as they are typed or pasted. A command the node refuses says why in the user's language (the translation of its code, as for the lifecycle buttons), or the status and code when there is none.
- Reconnect banner and a visible marker when the agent reports a gap (`IsGapMarker`). When the panel refuses to open the console (a hub error while the connection is up, not a dropped connection, which joins again by itself) the tab says so instead of staying blank.
- Output is filtered before xterm writes it: text, line breaks, tabs and colours (SGR) go through; every other control character or sequence (clearing the screen or scrollback, the alternate screen, cursor moves, resets, titles, OSC 8 hyperlinks: their text stays, their target does not, since the reader could not see where they lead) is dropped, so a player's or the game's text cannot wipe or rewrite what the admin reads. A carriage return without a line break after it becomes a line break (text after it would otherwise overwrite its row). A pseudo console's move to the start of the next row becomes a line break, as the agent reads it. A sequence split between chunks is held until it ends; one that never does (longer than 4 KiB, or a string a line break cuts) is dropped and a dimmed marker says so (in the interface's language when it is written), and the output after it is shown.
- Links: `http/https` only (URLs written in the output), opened in a new tab without access to the panel (`noopener,noreferrer`).
- Commands go through `POST /api/v1/servers/{id}/console` (`server.console.write`, antiforgery, audited; commands starting with a sensitive prefix of the game are audited as `prefix ***`).

## Files

- `/servers/:id?tab=files&path=…&file=…` (`server.files.view`; changes need `server.files.edit`): one folder at a time with breadcrumbs, folders first; the folder and the open file are in the address. Links are shown as such and never opened. Names are whatever the game or its players chose, so characters that are invisible and reorder or hide in a name (every Unicode format character: direction overrides and isolates, the Arabic letter mark, zero-width spaces, the soft hyphen, the byte order mark, tag characters; plus the line and paragraph separators and the invisible Hangul fillers) are shown as their code, `invoice[U+202E]fdp.exe`, not as `invoiceexe.pdf`; the joiner, the non-joiner and the left-to-right and right-to-left marks stay, since emoji, Persian or Hebrew names need them. Requests use the real name. The listing is not live: it refreshes after changes made in the page, on demand, after a reconnect and when the panel says the server changed (an install, update or restore rewrites the files; the open editor's content is not refreshed).
- Actions: new folder, new file (opens it in the editor), rename, delete (asked first; a folder with everything in it), upload (several files, one after another, with progress through `XMLHttpRequest`; an existing name asks before replacing, in the folder the file was uploaded to and only while that folder is open), download (a plain link: the panel streams the file). While an operation runs on the server, or its node is disconnected, changes are disabled and a note says why.
- Editor: CodeMirror 6, loaded only when a file is opened (its own chunk), files up to 1 MiB that are UTF-8 text (others, such as a Windows-1252 file with accents, are said not to be UTF-8 and offered as downloads). Highlighting for Source configuration and KeyValues files (`.cfg`, `.vdf`…), JSON, INI/properties, TOML, YAML, XML, Lua, shell and PowerShell, in the panel's colors. Ctrl+S saves with `If-Match` on the version it opened: when the file changed meanwhile (the agent patches `server.cfg` at every start) it offers Reload or Overwrite. Line endings and a byte order mark are kept as the file had them; a file that mixes Windows and Unix line endings is shown read only (the editor writes one ending everywhere, so saving would change them), to be downloaded for editing. The content is read once and only reloaded when the user asks (a cached query would be refreshed by the live channel under the user's edits). Ctrl+S on an unchanged file writes nothing; a save refreshes the folder's listing (size and date). Leaving unsaved changes asks first: closing the editor, another tab or page of the panel (a router blocker), reloading or closing the browser tab (`beforeunload`). Switching tabs keeps `path` and `file` in the address, so going back to Files finds the folder and the file again. Tab indents in the editor; a note under it says how to leave with the keyboard (Esc then Tab, or Ctrl+M, Shift+Alt+M on a Mac, to make Tab move the focus: CodeMirror's tab-focus mode, bound by its default keymap), so the editor is no keyboard trap. A failed refresh of the server keeps its page, the editor included, with the last data and a notice (only a first load that failed shows an error instead of the page; a refresh that finds the server deleted, or no longer the user's to see, shows that it does not exist, so no tab or action stays offered); the Settings tab does the same for its settings.
- API: `GET /api/v1/servers/{id}/files?path=`, `GET …/files/content?path=` (editor, raw bytes, the version as a strong ETag, `no-store`), `GET …/files/download?path=` (attachment), `PUT …/files/content?path=` (raw bytes; `If-Match` / `If-None-Match: *` / neither = replace), `POST …/files/directories`, `POST …/files/move`, `DELETE …/files?path=`. Writes, moves, deletions, new folders and downloads are audited.

## Backups

- `/servers/:id?tab=backups` (`server.backups.view`): the server's backups, newest first, with their size, file count, how long they took, who made them (a user or the schedule) and, for a failed one, why (the translation of its operation's code, `errorCode` in the list, with the code as a tooltip; the agent's own words when there is none). The list follows the live channel: a backup row changing (one completing) refreshes it, and the progress of the active operation shows at the top of the page with Cancel (`server.backups.create` for a backup).
- Actions: create (`server.backups.create`; an optional name, the time by default; a warning when the server is running), download (a plain link: the panel streams the zip from the node), restore (`server.backups.restore`; asked first; only with the server stopped and idle), lock and unlock, delete (`server.backups.delete`; locked backups cannot be deleted; asked first) and the retention limit (`server.backups.delete`, since it deletes backups; lowering it asks first). The retention limit and the remote storage fields start again from what is stored when another admin changes them.
- While a backup runs, the server can still be started and restarted and its files edited; a restore shows the server as *Restoring* and blocks both.
- *Snapshots and clean restores (Phase 15):* a backup of a running game says how its files were read: *Copied from a snapshot of the disk*, or *Copied while the game ran, without a snapshot* with the reason (an agent that is no administrator, a disk that cannot have snapshots, no room for one, the service off, snapshots turned off…), and whether the game saved first (confirmed, asked, or not confirmed); a backup of a stopped server says nothing. The create dialog's warning for a running server says this. Restoring opens a dialog instead of a confirmation: the backup's files replace the current ones, and *Delete the files made after this backup* (unticked by default) restores clean; a backup that left files out cannot, and the box is disabled with the reason. API: `POST …/backups/{backupId}/restore?clean=true`; without `clean`, a restore keeps newer files as before.
- *Remote copy (Phase 10):* each backup says whether its copy is in remote storage, being made, or why it failed; "Remote copy" (`server.backups.delete`, like the retention limit) chooses where new backups are copied among the panel's storages, or nowhere. `/admin/backup-storage` (`settings.manage`): the storages (endpoint or AWS region, bucket and folder, access key id, a secret key that is never shown again, path-style addressing) with how many servers use each and how many copies it holds; one in use cannot be deleted, and one holding copies keeps its endpoint and bucket (`backup_storage.holds_copies`; its keys, region, path style and folder for new copies may change).
- API: `GET /api/v1/servers/{id}/backups` (the list and the limit), `POST …/backups` (201), `GET …/backups/{backupId}/download` (`application/zip`, named after the server and the backup's time), `POST …/backups/{backupId}/restore`, `PUT …/backups/{backupId}/lock`, `DELETE …/backups/{backupId}`, `PUT …/backups/settings`, `PUT …/backups/storage`, and `GET|POST /api/v1/backup-storages`, `PUT|DELETE …/backup-storages/{storageId}`; a backup is cancelled with `POST /api/v1/servers/{id}/operations/{operationId}/cancel`. Requests, downloads, restores, locks, deletions and limit changes are audited.

## Mods

*Phase 12.*

- `/servers/:id?tab=addons`, labelled **Mods** (`server.view`), only for games whose template has addons: one card per addon with its name (a link to its home page), licence, description, what it needs and excludes, whether game updates reinstall it, and the release installed and when.
- Install, "Update to latest" and Remove (asked first) need `server.install` and a stopped, installed, idle server; otherwise the tab says to stop it. The operation's progress shows at the top of the page with Cancel for installs, like an update; the list refreshes when it ends. Refusals (what it needs, what it excludes, what needs it) are shown with the names.
- API: `GET /api/v1/servers/{id}/addons`, `POST` and `DELETE /api/v1/servers/{id}/addons/{key}`; the server's details carry `hasAddons`.

## Workshop

*Phase 12.* A variable with `workshop` settings (doc 04, "Variables") is edited as a list, in the create wizard and in the Settings tab: each item by its name on Steam (a link to its page), with its size and last update, or why it is suspect (Steam does not know it, it is in another game's Workshop, it is a collection). An id or a Steam link adds an item; a collection offers to add its items. Removing is one click; the value saved is the game's own list. Steam is asked through the panel (`POST /api/v1/workshop/lookup`, `server.create` or `server.startup.edit` anywhere; up to 100 ids a request), never from the browser: no third-party requests from the page, and the CSP stays as it is. When Steam cannot be reached the ids are shown and edited as they are.

## Schedules

- `/servers/:id?tab=schedules` (`server.schedules.view`): the server's tasks with what each does, when (in words: "Every day at 04:00", with its time zone when it is not the browser's), its next run (in the browser's time) and how its last run went (done, failed and why, skipped and why: "3 runs missed: the panel was not running"). History shows the last 50 runs. The list follows the live channel: a run ending refreshes it.
- Editor (`server.schedules.edit`): name, action (only the actions the user may do themselves: restart, start, stop, back up, update, console command), the command or "verify every file" when the action needs one, "only while the server is running" for restarts and backups, and when: every day at a time, every week on a day at a time, every 1–12 hours, or a cron expression (five fields). The time zone defaults to the browser's; the list is the browser's IANA zones plus UTC (browsers name some zones by their older names, such as `America/Buenos_Aires`, and the panel accepts them). The next five runs are previewed by the panel as the user edits, shown in the task's time zone, so daylight saving time is visible before saving; a zone the browser does not know (the panel accepts every zone .NET resolves) is shown in UTC, and the dialog says so.
- Run now runs a task at once, outside its schedule, and shows the outcome; deleting asks first.
- API: `GET /api/v1/servers/{id}/schedules`, `GET …/schedules/preview?cron=&timeZone=`, `POST …/schedules` (201), `PUT …/schedules/{taskId}`, `DELETE …/schedules/{taskId}`, `POST …/schedules/{taskId}/run`, `GET …/schedules/{taskId}/runs?limit=`. Creating, changing, deleting and running a task now are audited; what a task does is audited as the schedule's.

## Notifications

*Phase 10.*

- `/admin/notifications` (`settings.manage`):
  - Channels: a Discord webhook or 1 to 10 email addresses; the events each wants (server crashed, did not start, backup failed, scheduled task failed, node offline and back, node low on disk); every server or some; English or Spanish; on or off.
  - A Discord webhook is shown by its id only: its address is a secret and never comes back. "Send test" sends a message at once and says whether it went, or why not.
  - Settings: the panel's address (for links in messages), the low-disk threshold (% free) and how long a node may be away before it counts as offline. The SMTP server: host, port, STARTTLS, TLS or none, user, a password that is never shown (left empty it is kept, but only for the same server and user), sender.
  - Recent messages: the last 100, with their status, attempts and last error; refreshed while some are pending.
- `/nodes/:id` shows each drive the agent uses (game servers, backups) with its free space, next to CPU and memory.
- API: `GET|POST /api/v1/notifications/channels`, `PUT|DELETE …/channels/{id}`, `POST …/channels/{id}/test`, `GET /api/v1/notifications/deliveries`, `GET|PUT /api/v1/notifications/settings`. Changes and tests are audited.

## Two-factor authentication

- Sign-in: after a correct password, a user with two-factor authentication gives a code from their authenticator app or one of their recovery codes (`POST /api/v1/auth/login` answers `twoFactorRequired`; `POST /api/v1/auth/login/two-factor` signs in).
- `/account`: setting it up asks for the current password (a new key ends the user's other sessions), then shows a QR code (drawn as SVG by `uqr`, so the CSP needs nothing new) and the key as text; a code from the app, sent with that password, turns it on and shows ten recovery codes once, with Copy (which says so when the browser refuses the clipboard, as on plain HTTP) and "I saved them". With it on, the page shows how many codes are left and offers new codes and turning it off (both with the password).
- A user whose role requires it (`/admin/roles`: a switch per role, the Administrator role included) sees only the setup screen and Sign out until it is on; the API agrees (`403 auth.two_factor_required` anywhere else).
- `/admin/users`: a badge for users with it on, and turning it off for a user who lost their phone (asked first; their sessions end).
- API: `GET /api/v1/auth/two-factor`, `POST …/two-factor/setup` (password), `POST …/two-factor/enable`, `POST …/two-factor/disable`, `POST …/two-factor/recovery-codes`, `PUT /api/v1/roles/{id}/two-factor`, `POST /api/v1/users/{id}/two-factor/reset`.

## Template upgrades

The server page's Administration section says when a newer version of the server's game template exists, with "Move to x.y.z" (asked first; `server.install`, the server stopped).

## API keys

- `/account`: the user's keys (name, the first characters, permissions, last use, expiry) with Revoke (asked first), and Create: a name, an expiry (30, 90 or 365 days, or never) and optionally a checklist of the permissions the user holds anywhere. The value is shown once, with Copy and "I copied it".
- `/admin/users`: how many keys a user has, and Revoke all (asked first).
- The audit log shows key actions as "API key: user · key".
- API: `GET`/`POST /api/v1/account/api-keys`, `DELETE /api/v1/account/api-keys/{id}`, `DELETE /api/v1/users/{id}/api-keys`.

## Client-side security

- Cookie authentication `HttpOnly; Secure; SameSite=Strict`. **No tokens in localStorage.**
- CSRF protection: ASP.NET Core antiforgery with a header on mutations.
- Strict CSP served by the backend: `default-src 'self'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'none'`. Implies: no inline scripts, no CDNs, bundled fonts.
- Never `dangerouslySetInnerHTML`. File contents are shown in the editor, never as HTML.
- The UI hides actions without permission, but **the backend is the only authority**.
- One-time secrets (passwords sent, enrollment tokens and database passwords shown) do not stay in the query client's mutation cache: finished mutations leave it once no component shows them, and pages that stay open reset theirs once the value is shown or sent.

## Integration with ASP.NET Core

- `npm run build` writes straight into `src/Pulsar.Api/wwwroot` (git-ignored). The panel picks the files up as regular static web assets, so `MapStaticAssets()` fingerprints and precompresses them at publish time.
- `dotnet publish -p:BuildWebClient=true` runs `npm ci` + `npm run build` first; by default the .NET build does not need Node.js.
- `MapFallbackToFile("index.html")` serves client routes; unknown routes under `/api`, `/hubs` and `/agent` return a 404 `ProblemDetails` instead of the SPA.
- **Serve the published output.** With `dotnet run` outside the Development environment, the precompressed asset variants (`.gz`) only exist under `obj/` and are not served correctly. Development (`launchSettings.json`, port 5180) and published builds work; E2E tests therefore run against `dotnet publish` output.
- Node.js is only needed to build; the published panel does not require it.
- After an update, a tab opened before it cannot load pages it had not visited (the old build's files are gone): the route table's error page reloads it once (not again within a minute, so a broken build cannot loop) and otherwise offers a reload. A browser offline or a panel restarting fails to load pages the same way: the page reloads only when the panel answers (`/health/live`), since a reload then would show the browser's own error page and lose the tab; otherwise it says the panel cannot be reached and offers the reload, translated like the rest of the interface.

## Visual design

- Desktop first (suitable information density, sidebar), responsive down to tablet/phone for basic actions (power, console, status).
  - From Tailwind's `md` breakpoint the sidebar is always shown and stays in view while the page scrolls. Below it, a top bar opens the same navigation in a menu panel: a Radix dialog, so focus stays inside and Escape closes it. Choosing a page closes it, and so does a screen that grows past the breakpoint.
  - The server's sections are tabs with icons. On a narrow screen they scroll sideways and keep the open one in view. They follow the WAI-ARIA tabs pattern: only the open tab is in the Tab order, and the arrow keys, Home and End move between them.
  - Grids of cards size their columns with `minmax(0, 1fr)` and cards have `min-width: 0`, so a wide table scrolls inside its card instead of widening the page. No page is wider than a 320 px screen.
- A button that cannot be used loses its variant's fill (a faded accent still reads as "click me"). A busy one keeps its look, with a spinner. Killing a server is an outlined danger button, so it does not outshout the everyday actions.
- One theme, dark (the light one was dropped in the UI pass after the audit). Design tokens are CSS variables mapped into Tailwind's `@theme`. `color-scheme: dark` makes the browser's own controls (scrollbars, date pickers) dark too.
- States shown with color **and** text/icon (accessibility).
- The page's `lang` follows the interface's language, so screen readers read Spanish as Spanish.
- Dashboard v1: cards for online/offline servers, connected nodes, global CPU/RAM, alerts (crash loop, node disconnected; low disk is a notification event, not a dashboard alert) and recent activity (audit). Nothing more. Disabled nodes are neither offline nor alerts: the nodes card counts them apart. The CPU and memory cards draw how full they are as a bar. The bar turns amber from 75 % (the `warning` token) and red from 90 %; the value is written next to it, so the bar adds no text. With nothing to report, the alerts card is one quiet line.
