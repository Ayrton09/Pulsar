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
/servers                           /servers/new
/servers/:id/{overview|console|files|backups|schedules|network|startup|settings|activity}
/nodes                             /nodes/new
/nodes/:id/{overview|servers|system|storage|network|activity|settings}
/admin/{users|roles|templates|audit|settings}
/account                           profile, password, 2FA
```

Server and node pages use a layout with a fixed header (name, status, power actions, live CPU/RAM) and tabs. Tabs without permission are hidden.

*Phase 2:* nodes are created from a dialog on `/nodes` (no `/nodes/new` page) that ends with the one-time enrollment command, and `/nodes/:id` is a single page (machine, live usage, credentials, administration) until servers give it more to show. Node state refreshed by polling every 5 s until Phase 5 moved it to the live channel.

## Real time

- **One SignalR connection per tab** to `/hubs/panel`, authenticated by cookie.
- Explicit per-resource subscription: `useServerChannel(serverId)` calls `JoinServer(serverId)` (the hub authorizes `server.view`) and leaves on unmount; reference counted to avoid duplicates.
- Events → `queryClient.setQueryData(...)`: server state, metrics and operation progress update **without polling**.
- On reconnect: re-join active groups and invalidate visible queries (a single refetch to resync).
- Dashboard: a `dashboard` group with aggregates the panel emits at low frequency (≈5–10 s).

*Implementation (Phase 5):* `LiveProvider` opens the connection in the signed-in layout and reconnects forever (0.5 s doubling up to 30 s). Pages watch through hooks: `useWatchServers` and `useWatchNodes` (notices about every server and node: global permissions only, so these groups must become per user when scoped grants arrive), `useServerMetrics(id)` and `useServerConsole(id, write)`. Watches are shared, reference counted and sent again after every reconnect (groups belong to a connection). Server and node notices refetch what changed (a server's settings excepted: only a save changes them); operation progress, server usage and node usage are written into the cache; after a reconnect every visible query is fetched again. The panel pushes these notices after every save that touches servers, operations, variables, allocations, nodes or credentials (an EF Core interceptor), so no code path forgets one. A refused watch answers `false` or `null` rather than throwing: SignalR logs every hub exception as an error. The dashboard composes the server and node lists on the client instead of a `dashboard` group, and has no low-disk alert yet (the agent does not report disks).

## Console

- The `Terminal` component creates the xterm instance **once** and writes with `terminal.write()` from the event handler, **without going through React state** (a re-render per line would destroy performance).
- `WatchConsole(serverId, fromSequence)` (requires `server.console.view`) delivers backlog + stream. Output pushed before the answer arrives is held and written after it; each sequence is written once, in order. After a reconnect the browser asks for what came after the last entry it wrote.
- 5,000 lines of scrollback (configurable). Search (addon-search). "Clear" = local `terminal.clear()`, it does not affect the server.
- Optional timestamps: prefix generated client-side from each chunk's timestamp (exact in `pipes` mode, approximate in `pty`; documented). Not in v1 yet: CS2 and srcds already stamp their log lines.
- Input: a separate command field under the terminal, with history (↑/↓). Only visible with `server.console.write`. Clearer for game consoles than typing into xterm, and it allows read-only consoles.
- Reconnect banner and a visible marker when the agent reports a gap (`IsGapMarker`).
- Links: `http/https` only (written URLs and OSC 8 hyperlinks), opened in a new tab without access to the panel (`noopener,noreferrer`).
- Commands go through `POST /api/v1/servers/{id}/console` (`server.console.write`, antiforgery, audited; commands starting with a sensitive prefix of the game are audited as `prefix ***`).

## Files

- `/servers/:id?tab=files&path=…&file=…` (`server.files.view`; changes need `server.files.edit`): one folder at a time with breadcrumbs, folders first; the folder and the open file are in the address. Links are shown as such and never opened. The listing is not live: it refreshes after changes made in the page, on demand, and after a reconnect.
- Actions: new folder, new file (opens it in the editor), rename, delete (asked first; a folder with everything in it), upload (several files, one after another, with progress through `XMLHttpRequest`; an existing name asks before replacing), download (a plain link: the panel streams the file). While an operation runs on the server, changes are disabled and a note says why.
- Editor: CodeMirror 6, loaded only when a file is opened (its own chunk), files up to 1 MiB that are UTF-8 text (others are offered as downloads). Highlighting for Source configuration and KeyValues files (`.cfg`, `.vdf`…), JSON, INI/properties, TOML, YAML, XML, Lua, shell and PowerShell, in the panel's colors. Ctrl+S saves with `If-Match` on the version it opened: when the file changed meanwhile (the agent patches `server.cfg` at every start) it offers Reload or Overwrite. Line endings and a byte order mark are kept as the file had them. The content is read once and only reloaded when the user asks (a cached query would be refreshed by the live channel under the user's edits).
- API: `GET /api/v1/servers/{id}/files?path=`, `GET …/files/content?path=` (editor, raw bytes, the version as a strong ETag, `no-store`), `GET …/files/download?path=` (attachment), `PUT …/files/content?path=` (raw bytes; `If-Match` / `If-None-Match: *` / neither = replace), `POST …/files/directories`, `POST …/files/move`, `DELETE …/files?path=`. Writes, moves, deletions, new folders and downloads are audited.

## Backups

- `/servers/:id?tab=backups` (`server.backups.view`): the server's backups, newest first, with their size, file count, how long they took, who made them (a user or the schedule) and, for a failed one, why. The list follows the live channel: a backup row changing (one completing) refreshes it, and the progress of the active operation shows at the top of the page with Cancel (`server.backups.create` for a backup).
- Actions: create (`server.backups.create`; an optional name, the time by default; a warning when the server is running), download (a plain link: the panel streams the zip from the node), restore (`server.backups.restore`; asked first; only with the server stopped and idle), lock and unlock, delete (`server.backups.delete`; locked backups cannot be deleted; asked first) and the retention limit (`server.backups.delete`, since it deletes backups; lowering it asks first).
- While a backup runs, the server can still be started and restarted and its files edited; a restore shows the server as *Restoring* and blocks both.
- API: `GET /api/v1/servers/{id}/backups` (the list and the limit), `POST …/backups` (201), `GET …/backups/{backupId}/download` (`application/zip`, named after the server and the backup's time), `POST …/backups/{backupId}/restore`, `PUT …/backups/{backupId}/lock`, `DELETE …/backups/{backupId}`, `PUT …/backups/settings`; a backup is cancelled with `POST /api/v1/servers/{id}/operations/{operationId}/cancel`. Requests, downloads, restores, locks, deletions and limit changes are audited.

## Client-side security

- Cookie authentication `HttpOnly; Secure; SameSite=Strict`. **No tokens in localStorage.**
- CSRF protection: ASP.NET Core antiforgery with a header on mutations.
- Strict CSP served by the backend: `default-src 'self'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'none'`. Implies: no inline scripts, no CDNs, bundled fonts.
- Never `dangerouslySetInnerHTML`. File contents are shown in the editor, never as HTML.
- The UI hides actions without permission, but **the backend is the only authority**.

## Integration with ASP.NET Core

- `npm run build` writes straight into `src/Pulsar.Api/wwwroot` (git-ignored). The panel picks the files up as regular static web assets, so `MapStaticAssets()` fingerprints and precompresses them at publish time.
- `dotnet publish -p:BuildWebClient=true` runs `npm ci` + `npm run build` first; by default the .NET build does not need Node.js.
- `MapFallbackToFile("index.html")` serves client routes; unknown routes under `/api`, `/hubs` and `/agent` return a 404 `ProblemDetails` instead of the SPA.
- **Serve the published output.** With `dotnet run` outside the Development environment, the precompressed asset variants (`.gz`) only exist under `obj/` and are not served correctly. Development (`launchSettings.json`, port 5180) and published builds work; E2E tests therefore run against `dotnet publish` output.
- Node.js is only needed to build; the published panel does not require it.

## Visual design

- Desktop first (suitable information density, sidebar), responsive down to tablet/phone for basic actions (power, console, status).
- Dark and light themes following `prefers-color-scheme`; design tokens as CSS variables mapped into Tailwind's `@theme`.
- States shown with color **and** text/icon (accessibility).
- Dashboard v1: cards for online/offline servers, connected nodes, global CPU/RAM, alerts (crash loop, node disconnected, low disk) and recent activity (audit). Nothing more.
