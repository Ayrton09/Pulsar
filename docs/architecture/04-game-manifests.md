# 04 — Game manifests

> Status: **partly implemented** (Phase 3: the runtime subset, see "Implementation status" at the end) · Decision: ADR-0008.

## Goals

1. Add a game without touching Pulsar Core.
2. **Declarative and closed**: a manifest describes *what* to do with a finite set of typed steps; it never contains shell, scripts or arbitrary expressions.
3. Validated identically by panel and agent (`Pulsar.Manifests`), with a public JSON Schema for editors and community CI.
4. Versioned and immutable: a server is pinned to `key@version`; updating a manifest does not change existing servers without an explicit action.

## Trust model

A manifest decides which executable is launched and what is downloaded. **Importing a manifest is equivalent to installing software on every node.** Therefore:

- Only `template.manage` (global) can import; the UI shows a summary of "capabilities" (executables, download domains, ports) and the diff against the previous version.
- Official manifests ship with the panel (`games/`) and are registered as `source = bundled`.
- (Post-v1) optional signing of the official catalog and a per-node local policy that only accepts signed manifests.

## Layout

```
games/<key>/manifest.json      one file = one hash
games/<key>/README.md          notes for maintainers (not loaded)
schemas/manifest.v1.schema.json
```

Manifests and schemas are MIT licensed so communities and hosting companies can write their own without licensing friction.

## Example: Counter-Strike 2

> Illustrative and **not validated yet**: CS2 (Source 2) was not part of spike 0001. Its console mode (`pty` below vs `logfile+rcon` like srcds), arguments and readiness pattern must be confirmed with a CS2 run before this manifest is written.

```json
{
  "$schema": "https://raw.githubusercontent.com/<org>/pulsar/main/schemas/manifest.v1.schema.json",
  "schemaVersion": 1,
  "key": "cs2",
  "version": "1.0.0",
  "name": "Counter-Strike 2",
  "description": "Counter-Strike 2 dedicated server.",
  "authors": ["Pulsar contributors"],
  "platforms": ["windows-x64"],

  "requirements": { "minDiskGb": 60, "minMemoryMb": 4096 },

  "variables": [
    { "key": "hostname", "label": "Server name", "type": "string",
      "default": "Pulsar CS2", "maxLength": 64, "pattern": "^[^\"\\r\\n;]{1,64}$" },
    { "key": "map", "label": "Starting map", "type": "string",
      "default": "de_dust2", "pattern": "^[A-Za-z0-9_]{1,64}$" },
    { "key": "maxPlayers", "label": "Slots", "type": "integer", "default": 10, "min": 2, "max": 64 },
    { "key": "gameMode", "label": "Mode", "type": "enum", "default": "competitive",
      "options": [
        { "value": "casual",      "label": "Casual",      "map": { "gameType": "0", "gameMode": "0" } },
        { "value": "competitive", "label": "Competitive", "map": { "gameType": "0", "gameMode": "1" } }
      ] },
    { "key": "rconPassword", "label": "RCON password", "type": "string", "secret": true,
      "pattern": "^[A-Za-z0-9_\\-]{8,64}$", "required": false },
    { "key": "gslt", "label": "Game Server Login Token", "type": "string", "secret": true,
      "pattern": "^[A-Za-z0-9]{1,64}$", "required": false }
  ],

  "ports": [
    { "name": "game", "label": "Game", "protocols": ["udp", "tcp"], "default": 27015, "required": true },
    { "name": "tv",   "label": "CSTV", "protocols": ["udp"],        "default": 27020, "required": false }
  ],

  "install": {
    "steps": [
      { "type": "steamcmd", "appId": 730, "login": "anonymous" }
    ]
  },
  "update": { "steps": [ { "type": "steamcmd", "appId": 730, "login": "anonymous" } ] },

  "runtime": {
    "windows-x64": {
      "executable": "game/bin/win64/cs2.exe",
      "workingDirectory": "game/bin/win64",
      "arguments": [
        "-dedicated",
        "-usercon",
        "-port", "{{port.game}}",
        "-maxplayers", "{{var.maxPlayers}}",
        "+game_type", "{{var.gameMode.gameType}}",
        "+game_mode", "{{var.gameMode.gameMode}}",
        "+map", "{{var.map}}",
        { "if": "var.gslt", "then": ["+sv_setsteamaccount", "{{var.gslt}}"] }
      ],
      "allowExtraArguments": true,
      "environment": {},
      "console": {
        "mode": "pty",
        "inputNewline": "\r\n",
        "sensitiveCommandPrefixes": ["rcon_password", "sv_password", "sv_setsteamaccount"]
      }
    }
  },

  "configFiles": [
    { "path": "game/csgo/cfg/server.cfg", "format": "source-cfg", "mode": "patch",
      "set": {
        "hostname": "{{var.hostname}}",
        "rcon_password": "{{var.rconPassword}}"
      } }
  ],

  "readiness": { "type": "logPattern", "pattern": "<to be confirmed by spike>", "timeoutSeconds": 300 },

  "stop": {
    "steps": [ { "type": "consoleCommand", "command": "quit" } ],
    "timeoutSeconds": 30
  },

  "backup": {
    "include": ["game/csgo/cfg/**", "game/csgo/addons/**", "game/csgo/gameinfo.gi"],
    "exclude": ["**/*.log", "**/*.dmp"]
  }
}
```

## Sections and rules

### Identity and versioning

- `schemaVersion`: version of the **format** (interpreted by `Pulsar.Manifests`). A new format means a new schema and validator; the old one keeps being read.
- `key` (`^[a-z0-9][a-z0-9-]{1,63}$`) + `version` (semver) identify an immutable template.

### Variables

| Type | Mandatory constraints |
|---|---|
| `string` | `pattern` is **mandatory** when used in `arguments` or `configFiles` (enforced by the validator); `maxLength`. |
| `integer` | `min` and `max`. |
| `boolean` | — |
| `enum` | Closed `options`; each option can map sub-values (`{{var.gameMode.gameType}}`). |

Attributes: `secret` (encrypted in the database, write-only in the API, masked in UI and logs), `required`, `userEditable` (when `false`, only users with `server.settings.edit` at node/global scope can change it).

Additional default validation for `string` values used as arguments: no leading whitespace, no control characters, and they **cannot start with `-` or `+`** (prevents a value from becoming a flag or a Source console command). Regexes are evaluated with `RegexOptions.NonBacktracking` and a timeout (no ReDoS from a manifest).

### Argument templates

- Allowed placeholders: `{{var.<key>}}`, `{{var.<key>.<sub>}}`, `{{port.<name>}}`, `{{port.<name>.ip}}`, `{{server.shortId}}`. Nothing else: no functions, no expressions, no environment access.
- **Every array element is exactly one argument (argv) after substitution.** A value is never split into several arguments.
- A single conditional: `{ "if": "var.x", "then": [...] }` (true for boolean `true` or a non-empty string).
- The agent builds the Windows command line with the MSVCRT/`CommandLineToArgvW` quoting algorithm (quotes and backslashes escaped correctly) and passes it to `CreateProcessAsUser` with an explicit `lpApplicationName`. Never through `cmd.exe`.
- `allowExtraArguments`: when `true`, users with `server.startup.edit` can add arguments (each one argv, with the same control-character validation). When `false`, they cannot.

### Executable and paths

- `executable` and `workingDirectory`: **relative** to the server directory, using `/`, no `..`, no root, no drive letter, no `:` (alternate data streams), no reserved names (`CON`, `NUL`, `COM1`…), no trailing dots/spaces.
- `.exe` only. `.bat`, `.cmd`, `.ps1`, `.vbs`, `.js` and `.msi` are forbidden: they involve interpreters or a shell.
- (Post-v1) `executable: { "runtime": "java-21", "path": "bin/java.exe" }` for agent-managed runtimes (Minecraft). The schema reserves the slot but v1 does not implement it.

### Environment

`environment` sets variables with placeholders. Deny list: `PATH`, `PATHEXT`, `ComSpec`, `SystemRoot`, `windir`, `PSModulePath`, `TEMP`/`TMP` (set by the agent inside the server directory), `USERPROFILE`, `APPDATA`, `LOCALAPPDATA`.

### Install/update steps (closed set)

| Type | v1 | Parameters |
|---|---|---|
| `steamcmd` | Yes | `appId`, `login` (`anonymous` in v1), `beta?`, `betaPasswordVar?` (secret), `validate?`, `platform?` |
| `ensureDirectory` | Yes | `path` |
| `writeFileIfMissing` | Yes | `path`, `content` (template using the encoder of its `format`) |
| `download` | Post-v1 | `url` (**https only**), **mandatory** `sha256`, `path` |
| `extractArchive` | Post-v1 | `archive`, `destination`, zip-slip protection |

There is no `run`/`exec`/`script` step. If a game needed its own installer, it would be discussed as a new typed step with its own security review.

### Configuration files

- `mode: "patch"`: the agent changes only the declared keys and keeps the rest of the file (users can still edit it in the file manager). `mode: "managed"`: the file is regenerated entirely; the UI warns that manual edits are lost.
- v1 formats: `source-cfg` (`cvar "value"` per line) and `properties` (`key=value`). `ini` and `json` post-v1.
- Each format has an **encoder** that rejects whatever would break it: `source-cfg` rejects `"`, `;`, `\r`, `\n` (in Source, `;` separates commands: `hostname "x"; rcon_password y` would be an injection).
- Empty non-secret variables → the line is removed in patch mode; empty secrets → the line is left untouched.

### Console

Three modes, chosen per game from the evidence of [spike 0001](../spikes/0001-console-capture.md) (ADR-0007):

| `mode` | Output | Input / graceful stop | For |
|---|---|---|---|
| `pty` | ConPTY stream | Written to the ConPTY (Ctrl+C = `0x03`) | Console applications that use their console normally (SteamCMD) |
| `pipes` | Redirected stdout/stderr | stdin | Processes that only use stdio (e.g. Java) |
| `logfile+rcon` | Tail of `console.logFile` | RCON (`console.rcon`) | **Source engine on Windows** (srcds allocates its own console and crashes without one) |

- `encoding` (`pipes` and `logfile+rcon`): `utf-8` (default), `oem`, `ansi`.
- `inputNewline` (`pty`/`pipes`), `sensitiveCommandPrefixes` (commands starting with these are audited as `rcon_password ***`).
- `logFile` (`logfile+rcon`): relative path of the log the game writes (e.g. `cstrike/console.log` with `-condebug`). The agent rotates it before each start and follows truncation/recreation.
- `rcon` (`logfile+rcon`): `{ "protocol": "source", "port": "game", "passwordArgument": ["+rcon_password", "{{rcon.password}}"] }`. The agent generates a random password per server (`{{rcon.password}}` is the only placeholder not backed by a user variable) and keeps the RCON session; RCON responses are inserted into the console stream.

Fragment for Counter-Strike: Source (validated by spike 0001):

```json
"runtime": {
  "windows-x64": {
    "executable": "srcds.exe",
    "arguments": [
      "-game", "cstrike", "-console", "-condebug",
      "-port", "{{port.game}}", "+maxplayers", "{{var.maxPlayers}}", "+map", "{{var.map}}",
      "+rcon_password", "{{rcon.password}}", "+sv_rcon_maxfailures", "5", "+sv_rcon_banpenalty", "30"
    ],
    "console": { "mode": "logfile+rcon", "logFile": "cstrike/console.log", "rcon": { "protocol": "source", "port": "game" } }
  }
},
"readiness": { "type": "logPattern", "pattern": "^Server is hibernating", "timeoutSeconds": 120 },
"stop": { "steps": [ { "type": "rconCommand", "command": "quit" } ], "timeoutSeconds": 30 }
```

### Readiness and stop

- Readiness: `logPattern` (regex over the output), `processAlive` (`graceSeconds`), `rcon` (first successful RCON command), post-v1 `query` (A2S for Source). Moves `Starting → Running`.
- Stop: steps `consoleCommand` (`pty`/`pipes`), `rconCommand` (`logfile+rcon`), `ctrlC` (`pty` only), `wait`; after `timeoutSeconds` the agent **terminates the Job** (kills the whole tree).

### Backups

Globs (`Microsoft.Extensions.FileSystemGlobbing`) relative to the server directory. **Content reinstallable through SteamCMD is excluded by default**: a CS2 backup should weigh megabytes of configuration, not 60 GB of binaries. Without a `backup` section the whole directory is backed up.

### Reserved for later versions

`query` (query protocol), additional `healthChecks`, `databases` (request a MySQL/MariaDB database from the game databases module), managed `runtime`s, `linux-x64`.

## Validation

`Pulsar.Manifests` exposes:

```csharp
ManifestParseResult ManifestParser.Parse(ReadOnlySpan<byte> utf8Json);   // JSON Schema + typed model
IReadOnlyList<ManifestError> ManifestValidator.Validate(GameManifest m);  // semantic rules
Result<LaunchPlan> LaunchPlanRenderer.Render(GameManifest m, Platform p,
    IReadOnlyDictionary<string, VariableValue> values, PortAssignments ports, IReadOnlyList<string> extraArgs);
```

Semantic rules (beyond the schema): placeholders reference existing variables/ports; strings used in arguments have a `pattern` or are `enum`s; valid paths; `.exe` executable; no duplicate ports; regexes compile in NonBacktracking mode.

A repository test loads **every** `games/*/manifest.json` and fails if any is invalid. Rendering tests include injection cases (`map = "de_dust2 +rcon_password x"`, values with quotes, `;`, line breaks, `-insecure`).

## Implementation status

Phase 3 implements what starting servers needs, in `Pulsar.Manifests`: strict parsing (unknown properties and step types are errors), the semantic rules above for identity, variables, paths, placeholders, environment, console, readiness and stop, and the renderer (one template element is one argv entry; string values placed in arguments cannot start with `-`, `+` or whitespace whatever their pattern). Not supported yet, and refused if present: ports and `{{port.*}}`, configuration files, backups, the `steamcmd` step, `logfile+rcon` (Phase 4 and later). Bundled manifests are copied next to the panel and registered at startup (key + version immutable; a changed file without a new version is logged and ignored).

`games/_test/manifest.json` (key `pulsar-test`) runs Pulsar's test game server (`tests/Pulsar.TestGameServer`). Its install step `testGameServer` is accepted only in that manifest, the panel offers it only with `Pulsar:EnableTestGame` (development, CI) and only for agents that announce they have the test game (`Pulsar:TestGameDirectory`), so production panels and agents never see it.
