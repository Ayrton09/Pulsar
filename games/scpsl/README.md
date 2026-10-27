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

## Options added in 1.0.8

Hide from the public list, server info Pastebin ID, IP bans, fast round restart (and delay), intercom cooldown and speech time, warhead countdown effects (gates, doors, checkpoints), keep items after escaping, round end with one player, spawn protection (and time), MTF wave times and sizes and Chaos wave size, in `config_gameplay.txt`.

They come from AMP's template; none was tried on a real server. A key the game's file does not have is added at the end of it.

## Plugins and EXILED (1.0.12)

Checked on 2026-10-26 with the server of that day and EXILED 9.15.2, through a development panel and agent:

- The game's LabAPI keeps its plugins, their dependencies and configs in `SCP Secret Laboratory/LabAPI` of its
  account's AppData, whatever `-appdatapath` says, so plugins put in the server's folder were never loaded. Version 1.4.0
  gives the game `profile` for its user's folders (`userProfile`, agents of 1.0.12): LabAPI then uses
  `profile/AppData/Roaming/SCP Secret Laboratory/LabAPI` (`plugins/global`, `dependencies/global`, `configs`), reached by
  the file manager and SFTP and backed up. LocalAdmin itself still keeps its own settings in its account's profile.
- EXILED installs from the Mods tab: its release's `Exiled.tar.gz` (checked against the SHA-256 GitHub publishes) goes
  into `profile/AppData/Roaming`, where LabAPI loads `Exiled.Loader` and EXILED its plugins from `EXILED/Plugins`
  (`Exiled root path set to: …\profile\AppData\Roaming\EXILED`). Removing it deletes its files; the configs it wrote stay.
- EXILED's plugins go in `profile/AppData/Roaming/EXILED/Plugins` (their dependencies in `Plugins/dependencies`), LabAPI's
  own in `profile/AppData/Roaming/SCP Secret Laboratory/LabAPI/plugins/global`.
