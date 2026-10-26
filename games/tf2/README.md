# Team Fortress 2

Checked on 2026-10-24 on Windows 11 Pro (build 26200), with the server of build 25995664 (app 232250, about 15 GB),
through a development panel and agent:

- SteamCMD installs it anonymously. Pulsar runs `srcds_win64.exe`, the 64-bit server: `srcds.exe` (32-bit) took 33
  slots when asked for 64, `srcds_win64.exe` takes them all (up to 100).
- As in CS:S, the console is `tf/console.log` and commands go over RCON, on TCP at the game port's number; ready when
  RCON answers, in 10 to 27 s.
- A2S answers on the machine's network addresses, not on loopback, with the name, the map, the slots and whether a
  password is needed.
- The options left empty leave `tf/cfg/server.cfg` as it is; set ones are written there (`mp_timelimit`,
  `tf_weapon_criticals`, `tf_bot_quota`…). Bots join once a player is on: an empty server hibernates.
- `quit` over RCON stops it in about 2 s; both servers end with exit code 0xC0000409 when they quit, which Pulsar
  records as the requested stop it is.

## Options added in 1.0.8

Server tags, region, LAN only, cheats, stalemate (sudden death and its time), spectators, dead-player camera, no damage spread, fast download URL, allow downloads, pure server and slots shown in the browser, all in `tf/cfg/server.cfg`.

They come from AMP's template; none was tried on a real server.

## Players (1.0.11)

The Players card kicks a player by name (`kick "…"`). Source's bans take a user id or a Steam ID, which the server's query does not list, so the template has none. From the game's and hosting providers' documentation; not run on a server yet.
