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

- `css` and `cs2`: Source engine, output from a log file and commands over RCON.
- `valheim`: a console game stopped with Ctrl+C, and a port that follows another.
- `rust`: Unity, log file and RCON, an optional port with an `else`.
- `palworld`: console output with commands over RCON, settings in an Unreal Engine ini.
- `scpsl`: a launcher (LocalAdmin) that takes its own console commands, absolute paths with `{{server.dir}}`.
- `minecraft`: not on Steam: Mojang's server and a Java runtime, standard output, `server.properties`.

Then:

1. **Identity**: `key` (lowercase, unique, never changes), `version` (semantic version; a published version never
   changes, every change gets a new one), `name`, `description`, `authors`.
2. **Install and update**: usually one `steamcmd` step with the game's dedicated server app id (SteamDB lists them).
   Only games that allow anonymous SteamCMD downloads work in v1. `ensureDirectory` creates folders a game expects to
   exist; `minecraftServer` installs Minecraft: Java Edition. There is no step that runs a script or downloads an
   arbitrary file.
3. **Runtime**: the executable (relative to the server's folder, `.exe`), its working directory and its arguments.
   Arguments are a list, one element per argv entry, with placeholders: `{{var.<key>}}` for settings,
   `{{port.<name>}}` and `{{port.<name>.ip}}` for allocated ports, `{{rcon.password}}` for the RCON password the agent
   generates, `{{server.shortId}}`, and `{{server.dir}}` for a game that ignores relative folders. Optional parts go in
   `{ "if": "var.x", "then": [...] }` or `{ "if": "port.x", "then": [...] }`, with an optional `"else": [...]`.
4. **Variables**: what users set. Every string used in arguments needs a `pattern` (or must be an `enum`) so a value
   cannot become another argument; mark passwords and tokens `"secret": true` (the panel never shows them again).
5. **Ports**: each with the protocols players use. Pulsar opens firewall rules only for these. A port the game always
   opens at another port's number plus one (or more) says so with `"follows": { "port": "game", "offset": 1 }`.
6. **Console**: `pty` (the game writes to a console), `pipes` (standard output) or `logfile+rcon` (Source servers:
   output from a log file, commands over RCON). `pty` and `pipes` can take an `rcon` too, for games that print to their
   console but read commands only over RCON. Spike [0001](../spikes/0001-console-capture.md) explains how to find
   out which a game needs; a launcher that starts the real server may hide its output (run the real executable). List in `sensitiveCommandPrefixes` the commands that carry secrets, so they are not audited
   in clear.
7. **Configuration files**: settings the game reads from a file (`source-cfg`, `properties` or `unreal-ini`), written
   or patched by the agent before every start.
8. **Readiness and stop**: how the agent knows the game is ready (a console pattern, RCON answering, the query
   answering) and how to stop it cleanly (a console or RCON command, then a wait) before it is killed.
9. **Query**: `"query": { "protocol": "a2s", "port": "query" }` lets the panel show the players and the map. `a2s`
   for Source and most Steam games (often on the game port, sometimes on a separate query port), `minecraft` for the
   Server List Ping. Check that the game answers before adding it: Steam's server browser (View, Game Servers,
   Favorites, add the address and query port) shows whether it does.
10. **Backups**: which folders a backup includes and excludes.

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
