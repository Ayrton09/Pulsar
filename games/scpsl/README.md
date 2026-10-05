# SCP: Secret Laboratory

Checked on 2026-10-04 on Windows 11 Pro (build 26200), with the Steam build of that day (app 996560, LocalAdmin
2.5.16), through a development panel and agent:

- Pulsar starts `LocalAdmin.exe`, which starts `SCPSL.exe`. Without `--acceptEULA` LocalAdmin asks and waits forever, so
  the manifest requires the `eula` variable.
- Ready on `Waiting for players...` in about 10 s. `exit` stops it with code 0 in about 2 s.
- The game's configuration goes to the server folder only with **absolute** paths (`{{server.dir}}`) that **already
  exist** (the install step creates `config/config`), and ending in `/`: LocalAdmin quotes each argument when it starts
  the game, and a trailing `\` would escape that quote.
- LocalAdmin itself keeps its settings and logs in `%APPDATA%\SCP Secret Laboratory` of the account the server runs
  as (each server has its own account when the agent runs as a service).
