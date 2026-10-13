# 04 — Game manifests

> Status: **implemented** (v1 format: Phases 3 and 4, see "Implementation status" at the end; backups use their section in Phase 7) · Decision: ADR-0008.

## Goals

1. Add a game without touching Pulsar Core.
2. **Declarative and closed**: a manifest describes *what* to do with a finite set of typed steps; it never contains shell, scripts or arbitrary expressions.
3. Validated identically by panel and agent (`Pulsar.Manifests`), with a public JSON Schema for editors and community CI.
4. Versioned and immutable: a server is pinned to `key@version`; updating a manifest does not change existing servers without an explicit action.

## Trust model

A manifest decides which executable is launched and what is downloaded. **Importing a manifest is equivalent to installing software on every node.** Therefore:

- Only `template.manage` (global) can import; the UI shows a summary of "capabilities" (executables, download domains, ports, the GitHub repositories of its addons) and the diff against the previous version.
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
- `key` (`^[a-z0-9][a-z0-9-]{1,63}$`) + `version` (semver, ASCII digits only) identify an immutable template. `pulsar-test` is reserved for Pulsar's test game.
- `requirements` (`minDiskGb`, `minMemoryMb`) are shown when creating a server, not enforced.

### Variables

| Type | Mandatory constraints |
|---|---|
| `string` | `pattern` is **mandatory** when used in `arguments` or `configFiles` (enforced by the validator) and always matches the whole value (anchored or not: it runs as `\A(?:pattern)\z`, and the validator compiles that same text); `maxLength`. |
| `integer` | `min` and `max`. Rendered in its plain form (`+8` and `008` become `8`): after `+maxplayers`, `+8` would be a command of its own. |
| `boolean` | — |
| `enum` | Closed `options`; each option can map sub-values (`{{var.gameMode.gameType}}`); an enum used with a sub-value needs a `default` or `required`. |

Attributes: `secret` (strings only, no default; encrypted in the database, write-only in the API, masked in UI and logs, never placed in the environment where child processes would inherit it), `required`, `userEditable` (when `false`, only users with `server.settings.edit` can change it; whoever creates the server sets its first value).

`workshop` (Phase 12): `{ "appId": 108600, "separator": ";" }` on a string variable that is not secret says it lists Steam Workshop items of that game (the game's app id, not its dedicated server's), separated by `;` (the default) or `,`. The panel then offers an editor that shows each item by name and adds items by id or link (a collection adds its items), looking them up on Steam's public Workshop API; the value is still the plain list, checked by the variable's `pattern`, and the game downloads the items itself. Project Zomboid's `workshopItems` uses it.

Additional default validation for `string` values used as arguments: no leading whitespace, no control characters, and they **cannot start with `-` or `+`**, nor can any word in them (a `-` or `+` after whitespace and before a letter): a value is one argument, but a game may split it at spaces, so it never becomes a flag or a Source console command (`EU - 24/7` and `+1` stay text). The words are read with the template's text after the value: `x -` before `?Port=7777` makes a `-?Port=7777` word and is refused. Where `+` arguments are console commands (`logfile+rcon`, or a manifest with `+` arguments of its own, as for extra arguments) a value has no `;` (it would start another command) and no `"`. Regexes are evaluated with `RegexOptions.NonBacktracking` and a timeout (no ReDoS from a manifest).

### Argument templates

- Allowed placeholders: `{{var.<key>}}`, `{{var.<key>.<sub>}}`, `{{port.<name>}}`, `{{port.<name>.ip}}`, `{{rcon.password}}`, `{{server.shortId}}`, `{{server.dir}}` (the server's absolute folder, filled in by the agent, for games that ignore relative paths; the panel checks manifests with a stand-in). Nothing else: no functions, no expressions, no environment access.
- **Every array element is exactly one argument (argv) after substitution.** A value is never split into several arguments.
- A single conditional: `{ "if": "var.x", "then": [...] }` (true for boolean `true` or a non-empty string) or `{ "if": "port.x", "then": [...] }` (an optional port that has an allocation), with an optional `"else": [...]` for when it does not hold (Rust turns its companion app off with `+app.port -1` when that port has no allocation). Optional ports can only be used inside their own condition, never in its `else`.
- The agent builds the Windows command line with the MSVCRT/`CommandLineToArgvW` quoting algorithm (quotes and backslashes escaped correctly) and passes it to `CreateProcessAsUser` with an explicit `lpApplicationName`. Never through `cmd.exe`.
- `allowExtraArguments`: when `true`, users with `server.startup.edit` can add arguments (each one argv, with the same control-character validation). No word of an extra argument may set what the manifest sets (`-port=…` included) or start with a sensitive prefix of the console; the same goes for the URL options a word carries (`?Key=value`, separated by `?` or `&`, the part before the first `?` aside), as Unreal Engine games take their settings (ARK: `Map?listen?SessionName=…?Port=…`): the keys the manifest's own arguments set are reserved, and a sensitive prefix bars a key like it bars a switch (`ark-sa` lists `ServerAdminPassword`, `ServerPassword` and `SpectatorPassword` besides the console commands); where `+` arguments are console commands (`logfile+rcon`, or a manifest with `+` arguments of its own) `;` and anything about RCON are refused too. When `false`, they cannot.

### Ports

`ports` lists what the game listens on: `name`, `label`, `protocols` (`udp`, `tcp`), `default` and `required` (default `true`). A server gets one allocation of its node (an `ip:port` pair, both protocols reserved) per port; required ports get one when the server is created (the free allocation on the default port first), optional ones only when asked for. **`protocols` are what the firewall opens** when Pulsar manages it: declare only what players need. An RCON console uses the TCP side of its port even when the port declares UDP only, which keeps RCON reachable from the node alone.

A port can be `internal` (Phase 12): only the agent uses it, on the node, so the firewall never opens it whatever its protocols say (7 Days to Die's telnet console listens on a TCP port of its own).

A port can `follow` another: `"follows": { "port": "game", "offset": 1 }` says the game always listens there at the other port's number plus `offset` (1 to 10), on the same address (Valheim's query port is its game port + 1). Its `default` must be the followed port's default plus the offset, both share `required`, and chains are refused. The panel gives it the allocation `ip:port+offset` of the node together with the followed port, so a server can only take a game port whose follower is also free; users never pick it, and the renderer refuses a binding that breaks the rule.

### Executable and paths

- `executable` and `workingDirectory`: **relative** to the server directory, using `/`, no `..`, no root, no drive letter, no `:` (alternate data streams), no reserved names (`CON`, `NUL`, `COM1`…, also before an extension and spaces: `nul.txt`, `CON .cfg`), no trailing dots/spaces, names of up to 255 characters (the file manager's rules). Every path of a manifest (and every backup glob) stays out of Pulsar's own `.pulsar` folder (SteamCMD, its login, addon records), whatever its case or an 8.3 short name (`PULSAR~1`) for it.
- `.exe` only. `.bat`, `.cmd`, `.ps1`, `.vbs`, `.js` and `.msi` are forbidden: they involve interpreters or a shell.
- (Post-v1) `executable: { "runtime": "java-21", "path": "bin/java.exe" }` for agent-managed runtimes (Minecraft). The schema reserves the slot but v1 does not implement it.

### Environment

`environment` sets variables with placeholders; names are unique ignoring case, as Windows reads them. Deny list: `PATH`, `PATHEXT`, `ComSpec`, `SystemRoot`, `windir`, `PSModulePath`, `TEMP`/`TMP` (set by the agent inside the server directory), `USERPROFILE`, `APPDATA`, `LOCALAPPDATA`; and every name starting with `DOTNET_`, `COR_` or `CORECLR_` (the .NET runtime's own settings, `DOTNET_STARTUP_HOOKS` and the profiler variables among them, load any DLL into the game). A `string` variable in a value needs a `pattern`, as anywhere else.

### Install/update steps (closed set)

| Type | v1 | Parameters |
|---|---|---|
| `steamcmd` | Yes | `appId`, `login` (`anonymous`, or `account` when only an account that owns the game may download it: such a server is not installed without a Steam account, Phase 13), `beta?`, `betaPasswordVar?` (a secret variable, with `beta`; the agent fails the install with `steamcmd.beta_password_invalid` when its value has spaces, double quotes or control characters, starts with `+` or `-`, or is longer than 64 characters, since SteamCMD would split it into commands), `validate?` |
| `ensureDirectory` | Yes | `path` |
| `writeFileIfMissing` | Yes | `path`, `content` (literal text, no placeholders) |
| `copyFileIfMissing` | Phase 12 | `from`, `to`: copies a file of the server directory when `to` does not exist yet (at most 16 MiB), through the agent's link-safe file access. 7 Days to Die's server makes its `serverconfig.xml` the server's own `pulsar-serverconfig.xml`, which the game's updates then leave alone. |
| `minecraftServer` | Yes | `versionVar?` (a string variable with a pattern, or an enum: a version id or `latest`) |
| `download` | Post-v1 | `url` (**https only**), **mandatory** `sha256`, `path` |
| `extractArchive` | Post-v1 | `archive`, `destination`, zip-slip protection |

`minecraftServer` writes Mojang's `server.jar` for that version and installs the Java runtime the version's metadata asks for (`javaVersion.majorVersion`) in `java/`: the latest Eclipse Temurin JRE of that major version for Windows x64, from Adoptium's API. The agent downloads only over HTTPS from Mojang's and Adoptium's hosts (and GitHub's, where Adoptium's binaries live), follows redirects itself to check every hop, and accepts a file only if it matches the checksum its publisher lists in the metadata fetched over that same TLS: SHA-1 for Mojang (the only one it publishes), SHA-256 for Adoptium. A version whose details Mojang's version list gives no checksum for is refused (`minecraft.no_checksum`): those details name the server's download and its checksum. The runtime's archive must contain `bin/java.exe` and nothing outside `java/`; `java/pulsar-java.txt` records its release and files, so an update replaces exactly those and skips the download when the release is the same.

There is no `run`/`exec`/`script` step. If a game needed its own installer, it would be discussed as a new typed step with its own security review. `install` runs on install and reinstall; `update` (optional) on the update operation, which can also ask SteamCMD to validate every file. SteamCMD steps run as the server's account (doc 06); the others are done by the agent through its link-safe file access.

### Configuration files

- `mode: "patch"`: the agent changes only the declared keys and keeps the rest of the file (users can still edit it in the file manager). `mode: "managed"`: the file is regenerated entirely; the UI warns that manual edits are lost.
- v1 formats: `source-cfg` (`cvar "value"` per line), `properties` (`key=value`) and `unreal-ini`: the keys of one `section` of an Unreal Engine ini, as plain `Key=Value` lines or, with `struct`, as the members of one `Struct=(Key=Value,...)` line (Palworld's `OptionSettings`). In a struct, strings are quoted and numbers and `True`/`False` are not, as Unreal writes them; the members Pulsar does not declare stay, and a struct line that cannot be read stops the start rather than being overwritten. Other sections are kept. Generic `ini` and `json` post-v1.
- Each format has an **encoder** that rejects whatever would break it: `source-cfg` rejects `"`, `;`, `\r`, `\n` (in Source, `;` separates commands: `hostname "x"; rcon_password y` would be an injection).
- An empty value removes the key (patch mode) or leaves it out (managed mode), secrets included: declared keys belong to Pulsar, so a value written there by hand is replaced at the next start. The agent writes the files before every start; a file larger than 1 MiB is refused, and so is one that is not UTF-8 text (a `server.cfg` saved as Windows-1252, or as UTF-16 with its byte order mark, say): rewriting it would turn its other characters into `U+FFFD`, so the start fails with `config.invalid` and the file stays as it is (a hand-edited file saved as ANSI included), and the server's console says which file and to save it as UTF-8. The same goes for the files addons edit, at a start and when the addon is removed (the removal then fails with `config.invalid` before removing anything).
- `unreal-ini` rejects `"` and `\` (Unreal reads `\` as an escape inside quotes); its keys and `struct` are `^[A-Za-z_][A-Za-z0-9_]{0,127}$`, and `section` is `^[A-Za-z0-9_./:-]{1,128}$`.
- `properties` writes each backslash as `\\`; `source-cfg` keys are `^[A-Za-z0-9_.]{1,64}$`, `properties` keys `^[A-Za-z0-9_.-]{1,128}$`.
- Phase 12: `key-value` writes `Key=Value` lines as they are, without escapes (Project Zomboid's `.ini`, where Build 42 writes mods as `\ModId`); `#` and `!` start comments, and only `=` separates the key.
- Phase 12: `xml-properties` sets `<property name="Key" value="Value"/>` elements (7 Days to Die's `serverconfig.xml`), with values XML-escaped. Patch mode only: every element of a line is seen (several may share one); the first element of each key outside comments gets the new value and keeps the rest of its line (the game's own comment), repeats and empty values are removed (anything else on their line, a comment included, stays: a comment may go on over the next lines), and missing keys go before the closing tag of the root element named by `section` (a missing file starts as that empty root; a file without it stops the start). A commented-out element (`<!-- <property name="UserDataFolder" .../> -->`) is left as it is.

### Console

Three modes, chosen per game from the evidence of [spike 0001](../spikes/0001-console-capture.md) (ADR-0007):

| `mode` | Output | Input / graceful stop | For |
|---|---|---|---|
| `pty` | ConPTY stream | Written to the ConPTY (Ctrl+C = `0x03`) | Console applications that use their console normally (SteamCMD) |
| `pipes` | Redirected stdout/stderr | stdin | Processes that only use stdio (e.g. Java) |
| `logfile+rcon` | Tail of `console.logFile` | RCON (`console.rcon`) | **Source engine on Windows** (srcds allocates its own console and crashes without one) |

- `encoding` (`pipes` and `logfile+rcon`): `utf-8` (default), `oem`, `ansi`.
- `inputNewline` (`pty`/`pipes` without `rcon`), `sensitiveCommandPrefixes` (commands starting with these are audited as `rcon_password ***`).
- `logFile` (`logfile+rcon`): relative path of the log the game writes (e.g. `cstrike/console.log` with `-condebug`). The agent deletes it before each start and follows it (byte order mark skipped, truncation read from the start; a game that rotates its log, renaming it and starting another under the same name, is followed into the new one within about a second).
- `rcon` (required in `logfile+rcon`, optional in `pty`/`pipes`): `{ "protocol": "source", "port": "game" }`, a required port of the manifest. `"protocol": "telnet"` (Phase 12) is 7 Days to Die's telnet console: a banner, a password prompt answered with the RCON password, then one command per line. That game streams its whole log to every telnet client without marking where an answer ends, so the agent drains the connection all the time and takes as the answer the lines that are not log lines (`2026-10-05T12:14:43 74.506 INF …`, which the console already shows from the game's own output) until none came for 400 ms. Source RCON waits up to 15 s for an answer to start: ARK: Survival Ascended takes 2 to 4 s even when nobody plays. The answer is read until no packet came for 250 ms (a packet that started is always read whole), at most 30 s and 1 MiB: an answer that never pauses (a plugin flooding its output) is cut there and its session closed, and the next command opens another. The agent generates a random 32-character password at every provisioning (a reinstall rotates it; the server must be stopped) and keeps it encrypted in its state, and sends it (or anything else) only once the connection's other end is a process running as the server's account (doc 08, T18); the manifest passes it to the game with `{{rcon.password}}` (the only placeholder not backed by a user variable, refused in the environment), and a manifest with an RCON console must use it. The agent's client speaks both dialects measured in spike 0001 (srcds and CS2). The live console shows each command typed in it and its RCON answer; `"logsCommands": true` says the game writes the commands to its log file itself (CS2 does, srcds does not), so the console does not show them twice; answers it logs too (CS2 logs `status` but not cvar queries) are not added again, and the others are shown once the log has had 2 seconds to show them.
- `pty`/`pipes` with `rcon`: the output still comes from the process, but commands typed in the live console and `rconCommand` stop steps go through RCON (Palworld prints its log but reads nothing from its console).
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

- Readiness: `logPattern` (regex over the output, or over the log file in `logfile+rcon`), `processAlive` (`graceSeconds`), `rcon` (the first successful RCON login), `query` (the first answer to the manifest's query, Phase 11). Moves `Starting → Running`. The bundled Source manifests use `rcon`: `Server is hibernating` never appears when users turn hibernation off.
- Stop: steps `consoleCommand` (`pty`/`pipes`), `rconCommand` (any console with `rcon`), `ctrlC` (`pty` only), `wait`; after `timeoutSeconds` the agent **terminates the Job** (kills the whole tree).

### Backups

Globs (`Microsoft.Extensions.FileSystemGlobbing`) relative to the server directory. **Content reinstallable through SteamCMD is excluded by default**: a CS2 backup should weigh megabytes of configuration, not 60 GB of binaries. Without a `backup` section the whole directory is backed up.

As implemented (Phase 7): the globs match files only and ignore case (as Windows does); `dir/**` takes everything below `dir`. Pulsar's own files are never backed up, whatever the globs say: the server's SteamCMD (`.pulsar/**`) and transfers in progress (`.pulsar-upload-*`). Backups name folders by their long names, so a game's folder named like an 8.3 short name (`data~1`) is backed up like any other; a restore writes such a folder only as a folder of that very name (one the server has, or one it creates), and leaves out what an archive has under a name that leads to another folder of the server (`PULSAR~1` is `.pulsar` on volumes that keep short names), then fails saying so (`files.exists`) after restoring the rest. Links are never followed, so nothing outside the server directory gets in.

`save` (Phase 15) makes a running game write its world to disk before a backup captures it:

```json
"save": { "commands": ["save-off", "save-all flush"], "confirmation": "INFO\\]: Saved the game\\r?$", "timeoutSeconds": 120, "resume": ["save-on"] }
```

`commands` (1 to 4 lines) go through the server's console like typed ones (over RCON when the console uses it). The backup then waits for a console line matching `confirmation` (a non-backtracking regular expression, at most `timeoutSeconds`, 1 to 300, default 60) or, for a game without such a line, `waitSeconds` (0 to 60). `resume` (up to 4 lines) undoes what the commands paused; the agent sends it once the files are captured, and again after a restart of the agent if it stopped in between. Without an answer in time the backup goes on and records that the game did not confirm. Bundled (2026-10-10): Minecraft (`save-off`, `save-all flush` until *Saved the game*, then `save-on`), Rust (`server.save` until *Saved … ents*), and ARK: Survival Ascended (`SaveWorld`, 10 s), Palworld (`Save`, 5 s), 7 Days to Die (`saveworld`, 10 s) and Project Zomboid (`save`, 10 s), whose answers are not documented; Valheim takes no console commands, and CS2, CS:S and SCP: Secret Laboratory keep no world.

### Query

`"query": { "protocol": "a2s", "port": "game" }`: how the agent asks the running game for its name, map, player counts and players, shown on the server's page. `a2s` is Valve's server query over UDP (Source games, Rust, Valheim and most Steam games; a challenge answered when the game asks for one, answers split over several packets put back together, Rust's player counts above 255 read from its keywords); `minecraft` is Minecraft's Server List Ping over TCP (the message of the day as the name, the player sample as the list). The port must be a required port of the manifest with the protocol's transport. A server on every interface (`0.0.0.0`) is asked where it answers: srcds ignores queries from loopback (checked with CS:S), so the agent's first query goes to loopback and to each of the node's addresses at once, and the address that answers is used from then on (Minecraft is asked on loopback). Bundled: CS2 and CS:S on `game`, Rust and Valheim on `query` (Valheim answers only when the server is listed, and puts its name where the map goes), Minecraft on `game`. CS2 counts its players and bots but sends their names empty, so the panel shows counts without a list. Checked on 2026-10-05 with each game through a published panel and a real agent. *Implemented in Phase 11.*

### Addons

```json
"addons": [
  { "key": "metamod", "name": "Metamod:Source", "license": "zlib", "url": "https://www.metamodsource.net/",
    "source": { "type": "githubRelease", "repo": "alliedmodders/metamod-source", "asset": "^mmsource-2\\.0\\.0-git[0-9]+-windows\\.zip$", "prerelease": true },
    "target": "game/csgo",
    "lineEdits": [ { "path": "game/csgo/gameinfo.gi", "after": "Game_LowViolence csgo_lv", "line": "Game\tcsgo/addons/metamod" } ] },
  { "key": "counterstrikesharp", "name": "CounterStrikeSharp", "license": "GPL-3.0", "url": "https://docs.cssharp.dev/",
    "source": { "type": "githubRelease", "repo": "roflmuffin/CounterStrikeSharp", "asset": "^counterstrikesharp-with-runtime-windows-.*\\.zip$" },
    "target": "game/csgo", "requires": ["metamod"] }
]
```

Mods and mod frameworks the game supports, installed on request from the server's **Mods** tab (never by the game's install). Each one has a `key` (`^[a-z0-9][a-z0-9-]{1,31}$`, unique), a `name`, its `license` and an https `url` shown before installing, and:

- `source`: a closed set, like install steps. `githubRelease` takes the GitHub `repo` (`owner/name`, neither of them `.` or `..`), an `asset` regex (NonBacktracking, at most 200 characters) that must match exactly one asset of the release, and either a `tag` (a fixed release, such as Carbon's `production_build`; not `.` or `..`) or `prerelease: true` (the newest release, pre-releases included: Metamod's only builds for CS2), or neither (the latest stable release). GitHub must publish the asset's SHA-256 (`digest`); without one the agent refuses it.
- `target`: the folder the zip is unpacked into ("" for the server's own folder).
- `requires` / `conflicts`: other addons of the manifest. The panel refuses to install one before what it needs, or next to one it conflicts with (either side declaring it is enough), and to remove one another installed addon needs (`409`). Addons cannot require each other in a cycle (`A` requires `B` requires `A`): none of them could ever be installed, so the manifest is refused.
- `reinstallAfterUpdate`: the game's updates overwrite it (Oxide replaces Rust's own files), so after every update the agent installs its latest release again, in the same operation.
- `lineEdits` (at most 8): lines the addon needs in the game's files. Before every start, while the addon is installed, the agent puts `line` right after the first line starting with `after` (spaces and tabs compared as one run, the anchor's indentation kept, never twice); a missing file fails the start (`addon.file_missing`), and so does a missing anchor (`config.invalid`). Removing the addon takes the line out. This is how Metamod stays in `gameinfo.gi`, which every CS2 update restores.

The agent downloads only from `api.github.com`, `github.com` and GitHub's asset hosts, over https, with the same rules as the Minecraft downloads (no other redirects, a size cap, a stall timeout, the hash checked before anything is unpacked). The zip's paths go through the same checks as the file manager's; it records the files it added and the game files it replaced in `.pulsar/addons/<key>.json`, before it writes the first one (an install that fails midway still knows which files are the addon's, for a retry or a removal). The server's account can write that record, so paths in it are used only if they are plain paths outside `.pulsar` (never SteamCMD or another addon's record). Outside `.pulsar` also means a first folder named like an 8.3 short name (`PULSAR~1`), which may be `.pulsar`'s on volumes that keep short names: refused in the zip and in the record alike, as the file manager refuses it. Installing an installed addon brings it to its latest release (nothing is downloaded when the asset is the same); the new release's files replace the old ones, and the files only the old one had are deleted. Game files an older release replaced stay recorded as replaced when a newer one no longer ships them. Removing deletes the files it added (changed ones too), the folders that leaves empty below its `target`, and its lines; game files it had replaced stay until the next update with file validation brings them back.

Bundled: `cs2` has Metamod:Source and CounterStrikeSharp (with its .NET runtime); `rust` has Oxide (uMod) and Carbon, which exclude each other. *Implemented in Phase 12.*

### Reserved for later versions

Additional `healthChecks`, `databases` (request a MySQL/MariaDB database from the game databases module), managed `runtime`s, `linux-x64`.

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

Semantic rules (beyond the schema; every name pattern ends at `\z`, so a name with a final newline is refused): placeholders reference existing variables/ports; strings used in arguments, the environment or configuration files have a `pattern` or are `enum`s; a variable's `default` is a string, a number or a boolean (and passes the variable's own checks); addons require each other without a cycle; valid paths; `.exe` executable; no duplicate ports; regexes compile in NonBacktracking mode.

Repository tests load **every** `games/*/manifest.json` and fail if any is invalid or does not match the schema, and check that the schema describes every part of the C# model (properties, required ones and enum values). Rendering tests include injection cases (`map = "de_dust2 +rcon_password x"`, values with quotes, `;`, line breaks, `-insecure`), and secret values never appear in error messages.

## Implementation status

The v1 format is implemented in `Pulsar.Manifests`: strict parsing (unknown properties and step types are errors), the semantic rules above, the renderer and the configuration file encoders, checked against the published schema. Bundled manifests (`games/`) are copied next to the panel and registered at startup (key + version immutable; a changed file without a new version is logged and ignored); administrators with `template.manage` import others after a preview (doc 05). The `backup` section is validated now and used by backups (Phase 7).

Bundled games: `cs2` (Counter-Strike 2) and `css` (Counter-Strike: Source), both checked on real servers (spike 0001). Garry's Mod was dropped from v1; it and other games follow the same pattern once run ([docs/guides/manifests.md](../guides/manifests.md)).

`games/_test/manifest.json` (key `pulsar-test`) runs Pulsar's test game server (`tests/Pulsar.TestGameServer`). Its install step `testGameServer` is accepted only in that manifest, the panel offers it only with `Pulsar:EnableTestGame` (development, CI) and only for agents that announce they have the test game (`Pulsar:TestGameDirectory`), so production panels and agents never see it; it cannot be imported.
