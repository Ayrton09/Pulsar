# Adding a game: writing a manifest

Every game Pulsar runs is described by a **manifest**: one JSON file that says how to install the game, how to start
it, which settings users may change, which ports it uses, how its console works, how to stop it and what to back up.
Pulsar's code knows nothing about any particular game. The format is specified in
[docs/architecture/04-game-manifests.md](../architecture/04-game-manifests.md) and described by
[schemas/manifest.v1.schema.json](../../schemas/manifest.v1.schema.json), which editors such as VS Code use for
completion and checks.

Manifests and the schema are MIT licensed: you can write manifests for your own panel without publishing them, and
contribute them to Pulsar if you want.

## A manifest is software

A manifest decides which program every node runs and what it downloads, so importing one is like installing software
on every node. Only users with `template.manage` can import manifests, and the panel shows what a manifest does before
importing it. The format is deliberately closed: there are no scripts and no shell, only typed steps (SteamCMD, file
operations), argument lists and typed settings with patterns.

## Start from a working one

Every manifest under `games/` was run against a real server; each new game's `README.md` says what was checked. Copy
the closest one to `games/<key>/manifest.json` and change it:

- `css` and `cs2`: Source engine, output from a log file and commands over RCON; `hl2dm` and `pvkii` (Pulsar 1.0.14) the
  same, `pvkii` with the 64-bit `srcds_win64.exe`.
- `valheim`: a console game stopped with Ctrl+C, and a port that follows another.
- `rust`: Unity, log file and RCON, an optional port with an `else`.
- `palworld`: console output with commands over RCON, settings in an Unreal Engine ini.
- `scpsl`: a launcher (LocalAdmin) that takes its own console commands, absolute paths with `{{server.dir}}`.
- `minecraft`: not on Steam: Mojang's server and a Java runtime, standard output, `server.properties`.
- `ark-sa`: a log file and RCON on the game port's TCP side, secrets in an Unreal Engine ini rather than on the command line the game logs.
- `7dtd`: standard output and a telnet console on an internal port, settings in an XML file copied from the game's.
- `project-zomboid`: a Java server run directly, standard output and its input, a `key=value` ini.
- `vrising`: JSON settings copied from the game's own and patched, secrets in a settings file, RCON on an internal port.
- `enshrouded`: a JSON file the game rewrites, an array element (`/userGroups/0/password`) and `writeIfEmpty`.
- `arma-reforger`: a JSON file the install writes because the game does not, a choice mapped to the values the game wants.
- `unturned`: `Key value` lines (`space`), a command stop, a port that follows another.
- `abiotic-factor`, `icarus`, `conan-exiles`, `squad`: Unreal Engine GUI servers run with pipes and stopped with Ctrl+C
  (Pulsar 1.0.12); `sandstorm` and `kf2` add several sections of one ini, `kf2` a log file as its console.
- `soulmask`: RCON bound to `127.0.0.1` on an internal port, a game that prints the commands it runs.
- `dst`: Lua console commands, a token file (`text`), `cluster.ini` sections.
- `space-engineers`: an XML settings file (`xml`) and a log named by the time the game started (`*`).
- `terraria`: not on Steam: a GitHub release (TShock) and the .NET runtime it needs, a pseudo console.
- `scpsl` (1.4.0): a game's AppData in the server's folder (`userProfile`), a `.tar.gz` addon (EXILED).

Then:

1. **Identity**: `key` (lowercase, unique, never changes), `version` (semantic version; a published version never
   changes, every change gets a new one), `name`, `description`, `authors`.
2. **Install and update**: usually one `steamcmd` step with the game's dedicated server app id (SteamDB lists them).
   A game whose dedicated server only an account that owns it may download says `"login": "account"`: its servers need
   a Steam account chosen in their settings (administrators add them under Steam accounts). `ensureDirectory` creates folders a game expects to
   exist; `copyFileIfMissing` makes a game's shipped settings file the server's own; `minecraftServer` installs
   Minecraft: Java Edition, and `minecraftBedrock` Minecraft: Bedrock Edition. A server published as a GitHub
   release's asset (Pulsar 1.0.12 agents) takes `githubRelease` (TShock), and one that runs on a .NET it does not ship
   takes `dotnetRuntime` with its channel (`"9.0"`): the agent points it at that runtime. There is no step that runs a
   script or downloads an arbitrary file.
3. **Runtime**: the executable (relative to the server's folder, `.exe`), its working directory and its arguments.
   Arguments are a list, one element per argv entry, with placeholders: `{{var.<key>}}` for settings,
   `{{port.<name>}}` and `{{port.<name>.ip}}` for allocated ports, `{{rcon.password}}` for the RCON password the agent
   generates, `{{server.shortId}}`, and `{{server.dir}}` for a game that ignores relative folders. Optional parts go in
   `{ "if": "var.x", "then": [...] }` or `{ "if": "port.x", "then": [...] }`, with an optional `"else": [...]`.
4. **Variables**: what users set. Every string used in arguments needs a `pattern` (or must be an `enum`) so a value
   cannot become another argument (where `+` arguments are console commands, as on Source servers, Pulsar also refuses
   values with `;` or `"`: leave them out of the pattern too, so users see why where they type); mark passwords and
   tokens `"secret": true` (the panel never shows them again). A
   setting that lists Steam Workshop items gets `"workshop": { "appId": <the game's app id> }` and an editor that
   shows the items by name. A multiplier is a `number` (a decimal between `min` and `max`). The game's options (friendly
   fire, rates, autosave…) are best left without a `default`: the panel shows them under "More game options", and
   left empty they change nothing (see 7). An optional choice can be used inside its own condition only:
   `{ "if": "var.combat", "then": ["-modifier", "combat", "{{var.combat}}"] }` adds nothing until one is made.
5. **Ports**: each with the protocols players use. Pulsar opens firewall rules only for these. A port the game always
   opens at another port's number plus one (or more) says so with `"follows": { "port": "game", "offset": 1 }`. A
   port only the agent uses (a telnet console) is `"internal": true` and never opened. Name the port players connect
   to `game`: the server cards and the server's page show its address as the one to connect to (a manifest without a
   `game` port shows none).
6. **Console**: `pty` (the game writes to a console), `pipes` (standard output) or `logfile+rcon` (Source servers:
   output from a log file, commands over RCON). `pty` and `pipes` can take an `rcon` too, for games that print to their
   console but read commands only over RCON (`"protocol": "source"`) or telnet (`"protocol": "telnet"`, 7 Days to Die).
   Agents of 1.0.12 mask the values of secret variables in the console, but a game that writes its command line to its
   log file keeps them there: where the game takes them from a configuration file, put them there. A game that writes
   only to its log takes `pipes` with `logFile` (1.0.12), whose file name may hold one `*` for a log named by the game's
   start. Unreal Engine servers are GUI programs: run them with pipes (and `-log`), and stop them with `ctrlC`, which
   1.0.12 agents send to the console the game opens. A game whose AppData should be in the server's folder (its
   plugins, its settings) takes `"userProfile": "profile"` (1.0.12). Spike [0001](../spikes/0001-console-capture.md) explains how to find
   out which a game needs; a launcher that starts the real server may hide its output (run the real executable). List in `sensitiveCommandPrefixes` the commands that carry secrets, so they are not audited
   in clear. They also keep users out of those settings in extra arguments (`-Name=…`, or `?Name=…` URL options as ARK takes them), which would store a secret in clear.
7. **Configuration files**: settings the game reads from a file (`source-cfg`, `properties`, `unreal-ini`, `key-value`
   or `xml-properties`), patched by the agent before every start: only the keys the manifest sets change, the rest of
   the file (the admin's own settings) stays. List the options' keys in `"keepIfEmpty": [...]`: an option left empty
   in the panel then leaves its line as the file has it, instead of removing it. Bundled games use `patch` only.
   `colon` (`key: value`, SCP: Secret Laboratory) is the format for YAML-like files whose values are plain text.
   `number` variables, `keepIfEmpty`, conditions on a choice and `colon` need agents of Pulsar 1.0.5 or newer.
   `json` files (Pulsar 1.0.7 agents) name each member by its JSON Pointer (`"/gameSettings/enemyDamageFactor"`); a
   lone `{{var.x}}` of an integer, number or boolean variable, or a lone `{{port.x}}`, is written as a JSON number or
   boolean, as is an enum whose options are all `true`/`false` (or all numbers) and a literal `true`, `false` or number;
   anything else is a string. Array elements are named by index (`"/userGroups/0/password"`), and keys listed in
   `"writeIfEmpty"` are written as an empty string instead of being removed.
   1.0.14 agents add a json file's `"lists"`: arrays of objects that list the ids of a list variable
   (`"/game/mods": { "variable": "mods", "key": "modId" }`, Arma Reforger), merged so that objects added by hand stay.
   `space` (`Key value` lines, Unturned's `Commands.dat`) needs 1.0.7 agents too. 1.0.12 agents add `text` (a file that
   is one value, a token), `xml` (elements' text by their path from the root, `/MyConfigDedicated/ServerName`), and
   several entries for one `unreal-ini` file, one per `section`.
8. **Readiness and stop**: how the agent knows the game is ready (a console pattern, RCON answering, the query
   answering) and how to stop it cleanly (a console or RCON command, then a wait) before it is killed.
9. **Query**: `"query": { "protocol": "a2s", "port": "query" }` lets the panel show the players and the map. `a2s`
   for Source and most Steam games (often on the game port, sometimes on a separate query port), `minecraft` for the
   Server List Ping. Check that the game answers before adding it: Steam's server browser (View, Game Servers,
   Favorites, add the address and query port) shows whether it does.
10. **Backups**: which folders a backup includes and excludes, and, for a game that keeps a world, how to make it save
   before a backup while it runs (`save`: its console commands, the line that says the save ended or a time to wait,
   and the commands that undo a pause such as Minecraft's `save-off`). Without a confirmation line, the backup waits
   `waitSeconds`; check the game's answer in its console before giving one.
11. **Addons** (optional): mods and mod frameworks users can install from the server's Mods tab, from the GitHub
   releases of their authors (`cs2` and `rust` show how). The release's asset must be a zip (or, for 1.0.12 agents, a
   `.tar.gz`: end the pattern with the extension) and GitHub must list its SHA-256 (the `digest` of the asset in its API); give the folder it unpacks into, what it needs and
   excludes, and the lines it needs in the game's files (Metamod's in `gameinfo.gi`), which the agent puts back before
   every start. Mark `reinstallAfterUpdate` when the game's updates overwrite it. Naming a repository means trusting its
   authors with code that runs as the game server.
12. **Mod catalogs** (optional): `mods` lets users search a catalog and install its files into the server: Modrinth
   and CurseForge by the server's type and game version (`minecraft` shows how), or (1.0.14) uMod's plugins by the
   addon the server has (`rust`: `addonTypes`, `community`, `extensions`, and `whileRunning` since Oxide and Carbon
   load plugins as they come), or Thunderstore's packages for a game's BepInEx (`valheim`: the BepInEx pack as a
   `thunderstore` addon, the catalog with `community`, `category` and the `bepinex` layout).

### Messages to the players

If the game's console has a command that tells everyone playing a message, give it in `commands.broadcast` with `{{message}}` where the message goes (`"commands": { "broadcast": "say {{message}}" }`): the panel uses it to warn the players before an automatic update. It cleans each message first (letters, digits, spaces and `. , ! ? : ' ( ) -`), so the template does not need to quote anything for safety; quote it only if the game needs quotes to take several words. A game whose command takes one word gets `"messageSpaces": "_"`. Check it on a running server from the console tab before you send the manifest.

Give the game's commands for one player in `commands.players` (`kick`, `ban`, `unban`, `whitelistAdd`, `whitelistRemove`, `adminAdd`, `adminRemove`), each with `{{player}}` where the name goes, only those the game has: the server's Players card offers them. The panel refuses a name that is not letters, digits and `_ - .`, so templates need no quoting for safety; add `"spaces": true` only when the command takes a name with spaces (`kick "{{player}}"`, or Unturned's arguments, which end at `/`), and `"byId": true` when the commands take a Steam ID rather than the name. If the game keeps who is banned, whitelisted or admin in a JSON file of objects (Minecraft's `ops.json`), name it in `lists` with the property holding the player, and the card shows it. Try each command on a running server first.

## Check it

1. `dotnet test --project tests/Pulsar.Manifests.Tests` loads every manifest under `games/` and fails on any error:
   unknown properties, placeholders that name nothing, strings in arguments without a pattern, regular expressions that
   are too expensive, and more than the schema can say.
2. Run it for real. Import it in a development panel (**Games → Import a template**) with a development agent (see
   CONTRIBUTING.md), create a server, and check that it installs, starts, shows its console, takes commands, stops
   cleanly, restarts after a crash if asked, backs up and restores. Then do the same with the agent installed as a
   service (Windows Sandbox: `scripts/sandbox/agent-install`), where the game runs as its own Windows account with no
   privileges: some games need write access only to their own folder, which is all they get.
3. Note in `games/<key>/README.md` what you checked, on which Windows and which game build, and anything surprising.

## Contributing it

Open a pull request with `games/<key>/manifest.json` and its README. Say which checks you ran. Manifests that were not
run against a real server are not merged: games behave in ways a manifest can only describe after watching them.
