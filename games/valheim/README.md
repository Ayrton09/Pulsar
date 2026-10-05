# Valheim

Checked on 2026-10-04 on Windows 11 Pro (build 26200), with the Steam build of that day (app 896660), through a
development panel and agent:

- SteamCMD installs it anonymously in about a minute and a half.
- Ready on `Registering lobby`: 48 s with a new world, 18 s with an existing one.
- The game listens on UDP `game` and `game + 1` (the Steam query port), hence `"follows": { "port": "game", "offset": 1 }`.
- Ctrl+C saves the world and exits with code 0 in about 4 s; there are no console commands.
- `-savedir saves` keeps worlds in the server folder; backups take `saves/**`.
- A2S answers only when the server is listed (`-public 1`). The manifest sets `SteamAppId=892970` in the environment, as Valheim's own start script does.
