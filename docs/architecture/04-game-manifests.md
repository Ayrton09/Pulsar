# 04 — Game manifests

> Status: **implemented** (v1 format: Phases 3 and 4, see "Implementation status" at the end; backups use their section in Phase 7) · Decision: ADR-0008.

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

[`games/cs2/manifest.json`](../../games/cs2/manifest.json), written after CS2 was run under the capture tool of spike 0001 (F7). Abridged:

```json
{
  "$schema": "../../schemas/manifest.v1.schema.json",
  "schemaVersion": 1,
  "key": "cs2",
  "version": "1.0.0",
  "name": "Counter-Strike 2",
  "platforms": ["windows-x64"],
  "requirements": { "minDiskGb": 85, "minMemoryMb": 4096 },

  "variables": [
    { "key": "hostname", "label": "Server name", "type": "string", "default": "Pulsar CS2", "maxLength": 64, "pattern": "^[^\";]{1,64}$" },
    { "key": "map", "label": "Starting map", "type": "string", "default": "de_dust2", "pattern": "^[A-Za-z0-9_]{1,64}$" },
    { "key": "maxPlayers", "label": "Slots", "type": "integer", "default": 10, "min": 2, "max": 64 },
    { "key": "gameMode", "label": "Game mode", "type": "enum", "default": "competitive",
      "options": [
        { "value": "casual", "label": "Casual", "map": { "gameType": "0", "gameMode": "0" } },
        { "value": "competitive", "label": "Competitive", "map": { "gameType": "0", "gameMode": "1" } }
      ] },
    { "key": "password", "label": "Join password", "type": "string", "secret": true, "pattern": "^[A-Za-z0-9_-]{1,32}$" },
    { "key": "gslt", "label": "Game Server Login Token", "type": "string", "secret": true, "pattern": "^[A-Fa-f0-9]{32}$" }
  ],

  "ports": [
    { "name": "game", "label": "Game", "protocols": ["udp"], "default": 27015 },
    { "name": "tv", "label": "CSTV", "protocols": ["udp"], "default": 27020, "required": false }
  ],

  "install": { "steps": [ { "type": "steamcmd", "appId": 730 } ] },
  "update": { "steps": [ { "type": "steamcmd", "appId": 730 } ] },

  "runtime": {
    "windows-x64": {
      "executable": "game/bin/win64/cs2.exe",
      "workingDirectory": "game/bin/win64",
      "arguments": [
        "-dedicated", "-condebug", "-usercon",
        "-ip", "{{port.game.ip}}", "-port", "{{port.game}}",
        { "if": "port.tv", "then": ["+tv_enable", "1", "+tv_port", "{{port.tv}}"] },
        "-maxplayers", "{{var.maxPlayers}}",
        "+game_type", "{{var.gameMode.gameType}}", "+game_mode", "{{var.gameMode.gameMode}}",
        "+map", "{{var.map}}",
        "+rcon_password", "{{rcon.password}}",
        { "if": "var.gslt", "then": ["+sv_setsteamaccount", "{{var.gslt}}"] }
      ],
      "allowExtraArguments": true,
      "console": {
        "mode": "logfile+rcon",
        "logFile": "game/csgo/console.log",
        "rcon": { "protocol": "source", "port": "game", "logsCommands": true },
        "sensitiveCommandPrefixes": ["rcon_password", "sv_password", "sv_setsteamaccount"]
      }
    }
  },

  "configFiles": [
    { "path": "game/csgo/cfg/server.cfg", "format": "source-cfg", "mode": "patch",
      "set": { "hostname": "{{var.hostname}}", "sv_password": "{{var.password}}" } }
  ],

  "readiness": { "type": "rcon", "timeoutSeconds": 300 },
  "stop": { "steps": [ { "type": "rconCommand", "command": "quit" } ], "timeoutSeconds": 30 },
  "backup": { "include": ["game/csgo/cfg/**", "game/csgo/addons/**", "game/csgo/gameinfo.gi"], "exclude": ["**/*.log", "**/*.dmp"] }
}
```

`games/css/manifest.json` (Counter-Strike: Source) follows the same shape with `srcds.exe`, `cstrike/console.log` and app 232330, and also sets srcds' RCON ban settings (`+sv_rcon_maxfailures 5 +sv_rcon_banpenalty 30`), which CS2 does not have.

## Sections and rules

### Identity and versioning

- `schemaVersion`: version of the **format** (interpreted by `Pulsar.Manifests`). A new format means a new schema and validator; the old one keeps being read.
- `key` (`^[a-z0-9][a-z0-9-]{1,63}$`) + `version` (semver) identify an immutable template. `pulsar-test` is reserved for Pulsar's test game.
- `requirements` (`minDiskGb`, `minMemoryMb`) are shown when creating a server, not enforced.

### Variables

| Type | Mandatory constraints |
|---|---|
| `string` | `pattern` is **mandatory** when used in `arguments` or `configFiles` (enforced by the validator); `maxLength`. |
| `integer` | `min` and `max`. |
| `boolean` | — |
| `enum` | Closed `options`; each option can map sub-values (`{{var.gameMode.gameType}}`). |

Attributes: `secret` (strings only, no default; encrypted in the database, write-only in the API, masked in UI and logs, never placed in the environment where child processes would inherit it), `required`, `userEditable` (when `false`, only users with `server.settings.edit` can change it; whoever creates the server sets its first value).

Additional default validation for `string` values used as arguments: no leading whitespace, no control characters, and they **cannot start with `-` or `+`** (prevents a value from becoming a flag or a Source console command). Regexes are evaluated with `RegexOptions.NonBacktracking` and a timeout (no ReDoS from a manifest).

### Argument templates

- Allowed placeholders: `{{var.<key>}}`, `{{var.<key>.<sub>}}`, `{{port.<name>}}`, `{{port.<name>.ip}}`, `{{rcon.password}}`, `{{server.shortId}}`. Nothing else: no functions, no expressions, no environment access.
- **Every array element is exactly one argument (argv) after substitution.** A value is never split into several arguments.
- A single conditional: `{ "if": "var.x", "then": [...] }` (true for boolean `true` or a non-empty string) or `{ "if": "port.x", "then": [...] }` (an optional port that has an allocation). Optional ports can only be used inside their own condition.
- The agent builds the Windows command line with the MSVCRT/`CommandLineToArgvW` quoting algorithm (quotes and backslashes escaped correctly) and passes it to `CreateProcessAsUser` with an explicit `lpApplicationName`. Never through `cmd.exe`.
- `allowExtraArguments`: when `true`, users with `server.startup.edit` can add arguments (each one argv, with the same control-character validation). When `false`, they cannot.

### Ports

`ports` lists what the game listens on: `name`, `label`, `protocols` (`udp`, `tcp`), `default` and `required` (default `true`). A server gets one allocation of its node (an `ip:port` pair, both protocols reserved) per port; required ports get one when the server is created (the free allocation on the default port first), optional ones only when asked for. **`protocols` are what the firewall opens** when Pulsar manages it: declare only what players need. An RCON console uses the TCP side of its port even when the port declares UDP only, which keeps RCON reachable from the node alone.

### Executable and paths

- `executable` and `workingDirectory`: **relative** to the server directory, using `/`, no `..`, no root, no drive letter, no `:` (alternate data streams), no reserved names (`CON`, `NUL`, `COM1`…), no trailing dots/spaces.
- `.exe` only. `.bat`, `.cmd`, `.ps1`, `.vbs`, `.js` and `.msi` are forbidden: they involve interpreters or a shell.
- (Post-v1) `executable: { "runtime": "java-21", "path": "bin/java.exe" }` for agent-managed runtimes (Minecraft). The schema reserves the slot but v1 does not implement it.

### Environment

`environment` sets variables with placeholders. Deny list: `PATH`, `PATHEXT`, `ComSpec`, `SystemRoot`, `windir`, `PSModulePath`, `TEMP`/`TMP` (set by the agent inside the server directory), `USERPROFILE`, `APPDATA`, `LOCALAPPDATA`.

### Install/update steps (closed set)

| Type | v1 | Parameters |
|---|---|---|
| `steamcmd` | Yes | `appId`, `login` (`anonymous` in v1), `beta?`, `betaPasswordVar?` (a secret variable, with `beta`), `validate?` |
| `ensureDirectory` | Yes | `path` |
| `writeFileIfMissing` | Yes | `path`, `content` (literal text, no placeholders) |
| `download` | Post-v1 | `url` (**https only**), **mandatory** `sha256`, `path` |
| `extractArchive` | Post-v1 | `archive`, `destination`, zip-slip protection |

There is no `run`/`exec`/`script` step. If a game needed its own installer, it would be discussed as a new typed step with its own security review. `install` runs on install and reinstall; `update` (optional) on the update operation, which can also ask SteamCMD to validate every file. SteamCMD steps run as the server's account (doc 06); the others are done by the agent through its link-safe file access.

### Configuration files

- `mode: "patch"`: the agent changes only the declared keys and keeps the rest of the file (users can still edit it in the file manager). `mode: "managed"`: the file is regenerated entirely; the UI warns that manual edits are lost.
- v1 formats: `source-cfg` (`cvar "value"` per line) and `properties` (`key=value`). `ini` and `json` post-v1.
- Each format has an **encoder** that rejects whatever would break it: `source-cfg` rejects `"`, `;`, `\r`, `\n` (in Source, `;` separates commands: `hostname "x"; rcon_password y` would be an injection).
- An empty value removes the key (patch mode) or leaves it out (managed mode), secrets included: declared keys belong to Pulsar, so a value written there by hand is replaced at the next start. The agent writes the files before every start; a file larger than 1 MiB is refused.
- `properties` writes each backslash as `\\`; `source-cfg` keys are `^[A-Za-z0-9_.]{1,64}$`, `properties` keys `^[A-Za-z0-9_.-]{1,128}$`.

### Console

Three modes, chosen per game from the evidence of [spike 0001](../spikes/0001-console-capture.md) (ADR-0007):

| `mode` | Output | Input / graceful stop | For |
|---|---|---|---|
| `pty` | ConPTY stream | Written to the ConPTY (Ctrl+C = `0x03`) | Console applications that use their console normally (SteamCMD) |
| `pipes` | Redirected stdout/stderr | stdin | Processes that only use stdio (e.g. Java) |
| `logfile+rcon` | Tail of `console.logFile` | RCON (`console.rcon`) | **Source engine on Windows** (srcds allocates its own console and crashes without one) |

- `encoding` (`pipes` and `logfile+rcon`): `utf-8` (default), `oem`, `ansi`.
- `inputNewline` (`pty`/`pipes`), `sensitiveCommandPrefixes` (commands starting with these are audited as `rcon_password ***`).
- `logFile` (`logfile+rcon`): relative path of the log the game writes (e.g. `cstrike/console.log` with `-condebug`). The agent deletes it before each start and follows it (byte order mark skipped, truncation read from the start).
- `rcon` (`logfile+rcon`): `{ "protocol": "source", "port": "game" }`, a required port of the manifest. The agent generates a random 32-character password at every provisioning (a reinstall rotates it) and keeps it encrypted in its state; the manifest passes it to the game with `{{rcon.password}}` (the only placeholder not backed by a user variable, refused in the environment), and a manifest with an RCON console must use it. The agent's client speaks both dialects measured in spike 0001 (srcds and CS2). The live console shows each command typed in it and its RCON answer; `"logsCommands": true` says the game writes both to its log file itself (CS2 does, srcds does not), so the console does not show them twice.
- The game still gets a pseudo console (srcds dies without one); its output is drained and ignored.

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

- Readiness: `logPattern` (regex over the output, or over the log file in `logfile+rcon`), `processAlive` (`graceSeconds`), `rcon` (the first successful RCON login), post-v1 `query` (A2S for Source). Moves `Starting → Running`. The bundled Source manifests use `rcon`: `Server is hibernating` never appears when users turn hibernation off.
- Stop: steps `consoleCommand` (`pty`/`pipes`), `rconCommand` (`logfile+rcon`), `ctrlC` (`pty` only), `wait`; after `timeoutSeconds` the agent **terminates the Job** (kills the whole tree).

### Backups

Globs (`Microsoft.Extensions.FileSystemGlobbing`) relative to the server directory. **Content reinstallable through SteamCMD is excluded by default**: a CS2 backup should weigh megabytes of configuration, not 60 GB of binaries. Without a `backup` section the whole directory is backed up.

### Reserved for later versions

`query` (query protocol), additional `healthChecks`, `databases` (request a MySQL/MariaDB database from the game databases module), managed `runtime`s, `linux-x64`.

## Validation

`Pulsar.Manifests` exposes:

```csharp
ManifestParseResult ManifestParser.Parse(ReadOnlySpan<byte> utf8Json);   // strict typed model + semantic rules + SHA-256
IReadOnlyList<ManifestError> ManifestValidator.Validate(GameManifest m);  // semantic rules
LaunchPlan LaunchPlanRenderer.Render(GameManifest m, string platform, ServerBinding binding);  // throws LaunchPlanException
string? LaunchPlanRenderer.Check(GameManifest m, string platform, ServerBinding binding);      // the panel's check
string ConfigFileWriter.Apply(RenderedConfigFile file, string? existing);                    // the encoders
```

`ServerBinding` carries the variable values, the port allocations, the extra arguments and the RCON password. The published JSON Schema (`schemas/manifest.v1.schema.json`, draft 2020-12) describes the shape for editors and community CI; Pulsar validates more than a schema can express.

Semantic rules (beyond the schema): placeholders reference existing variables/ports; strings used in arguments have a `pattern` or are `enum`s; valid paths; `.exe` executable; no duplicate ports; regexes compile in NonBacktracking mode.

Repository tests load **every** `games/*/manifest.json` and fail if any is invalid or does not match the schema, and check that the schema describes every part of the C# model (properties, required ones and enum values). Rendering tests include injection cases (`map = "de_dust2 +rcon_password x"`, values with quotes, `;`, line breaks, `-insecure`), and secret values never appear in error messages.

## Implementation status

The v1 format is implemented in `Pulsar.Manifests`: strict parsing (unknown properties and step types are errors), the semantic rules above, the renderer and the configuration file encoders, checked against the published schema. Bundled manifests (`games/`) are copied next to the panel and registered at startup (key + version immutable; a changed file without a new version is logged and ignored); administrators with `template.manage` import others after a preview (doc 05). The `backup` section is validated now and used by backups (Phase 7).

Bundled games: `cs2` (Counter-Strike 2) and `css` (Counter-Strike: Source), both checked on real servers (spike 0001); Garry's Mod and others follow the same pattern once run.

`games/_test/manifest.json` (key `pulsar-test`) runs Pulsar's test game server (`tests/Pulsar.TestGameServer`). Its install step `testGameServer` is accepted only in that manifest, the panel offers it only with `Pulsar:EnableTestGame` (development, CI) and only for agents that announce they have the test game (`Pulsar:TestGameDirectory`), so production panels and agents never see it; it cannot be imported.
