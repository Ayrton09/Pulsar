# Rust

Checked on 2026-10-04 on Windows 11 Pro (build 26200), with the Steam build of that day (app 258550), through a
development panel and agent:

- SteamCMD installs it anonymously (about 6 GB); a failed verification was retried by the agent on its own.
- A 1000 m map is generated and the server answers RCON (ready) in under a minute; bigger maps take longer.
- RCON listens on TCP at the game port's number, on the allocation's address; commands from the console go there and
  their answers are shown. `-logfile output.log` is the console.
- A2S on the query port answers with the name, the map and the slots.
- `quit` saves the map and exits in about 2 s, with exit code -1 even when the shutdown is clean.
- Without the companion app port, `+app.port -1` turns Rust+ off instead of letting it take a port Pulsar did not assign.

Checked again on 2026-10-05 (Phase 11, build 2634, release#166652): the panel shows the player count, the slots and
the map from the A2S query on the query port (Rust counts past 255 in its keywords, which the agent reads). The first
start of a fresh install crashed inside Unity while loading the world configuration; the agent restarted it and the
second start was ready.

## Options added in 1.0.8

Map type (the map is now a choice, Procedural by default as before), game mode, tick rate, map salt, custom map URL, max team size, radiation, upkeep period, save backups kept and Rust+ logo URL, all launch arguments.

They come from LinuxGSM's configuration, the Facepunch wiki and public server guides; none was tried on a real server.

## Plugins from uMod (1.0.14)

With Oxide or Carbon installed (Mods tab, Addons), the same tab searches uMod's Rust plugins and installs, updates and
removes them in `oxide/plugins` or `carbon/plugins` while the server runs: both load and unload a plugin as its file
comes and goes. Each file is checked against the SHA-1 uMod lists for its newest release.

## Players (1.0.11)

The Players card kicks and bans a player who is on the server by name (`kick "…"`, `ban "…"`). Unbanning and owners take a Steam ID (`unban`, `ownerid`): use the console. From the game's and hosting providers' documentation; not run on a server yet.

## Linux

The template runs on Linux nodes too: the agent starts `RustDedicated` with `RustDedicated_Data/Plugins` and its `x86_64`
folder as its library path, as `runds.sh` does. Checked for real on a Linux node (Ubuntu 24.04): installed, started (secure, connected to Steam), `status` over RCON, stopped
with `quit` and deleted. Oxide comes from its Linux asset (`Oxide.Rust-linux.zip`); Carbon from its
`Carbon.Linux.Release.tar.gz`, whose doorstop the addon's `linux` block preloads (`libdoorstop.so`, with
`DOORSTOP_ENABLED` and `DOORSTOP_TARGET_ASSEMBLY`) as Carbon's `environment.sh` would. Both were installed from the
panel there and answered `oxide.version` and `c.version`.
