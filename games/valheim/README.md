# Valheim

Checked on 2026-10-04 on Windows 11 Pro (build 26200), with the Steam build of that day (app 896660), through a
development panel and agent:

- SteamCMD installs it anonymously in about a minute and a half.
- Ready on `Registering lobby`: 48 s with a new world, 18 s with an existing one.
- The game listens on UDP `game` and `game + 1` (the Steam query port), hence `"follows": { "port": "game", "offset": 1 }`.
- Ctrl+C saves the world and exits with code 0 in about 4 s; there are no console commands.
- `-savedir saves` keeps worlds in the server folder; backups take `saves/**`.
- A2S answers only when the server is listed (`-public 1`). The manifest sets `SteamAppId=892970` in the environment, as Valheim's own start script does.

Checked again on 2026-10-05 (Phase 11): listed (`-public 1`), the server answers the agent's A2S query on its query
port and the panel shows the player count; Valheim puts the server name where A2S has the map.

## Server mods from Thunderstore (1.3.0, Pulsar 1.0.14)

BepInEx is an addon (Mods tab), installed from Thunderstore's `denikson-BepInExPack_Valheim`: its `BepInExPack_Valheim`
folder goes into the server's (`winhttp.dll`, `doorstop_config.ini`, `BepInEx/core`), and `valheim_server.exe` loads it
on its own. The same tab then searches Thunderstore's server-side packages for Valheim and installs them, with the
packages they need, into `BepInEx/plugins/<namespace-name>` while the server is stopped. Backups take `BepInEx/config`.

Checked on 2026-10-27 through a development panel and agent: BepInEx 5.4.23.5 (pack 5.4.2351) loaded at the next start;
AdventureBackpacks 2.4.6 brought Jotunn 2.30.2 and YamlDotNet 16.3.1 (the BepInEx pack it also names is the addon) and
BepInEx loaded all three (`Chainloader startup complete (3 loaded, 0 skipped, 0 failed)`); removing them emptied
`BepInEx/plugins` and kept their configuration in `BepInEx/config`.

## Options added in 1.0.8

Version 1.2.0 adds backups kept, first and later backup intervals (`-backups`, `-backupshort`, `-backuplong`), fire hazards (`-setkey fire`) and the crossplay instance id (`-instanceid`). The backup arguments come from the Pelican egg and the game's own server documentation, the others from AMP's template.

They come from AMP's template for the game (and, where noted, other panels' templates or the game's documentation); they were not each tried on a real server. The test suite checks that they are written to the right file and key, and that an option left empty changes nothing.
