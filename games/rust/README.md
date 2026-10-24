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
