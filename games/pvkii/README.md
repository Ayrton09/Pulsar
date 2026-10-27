# Pirates, Vikings, and Knights II (pvkii)

Checked on 2026-10-27 on Windows 11 Pro (build 26200), with the server of build 24661910 (app 17575, about 5.4 GB),
through a development panel and agent:

- SteamCMD installs it anonymously. The app ships `srcds_win64.exe`, `srcds_win32.exe` and a `srcds.exe` that picks one
  by `launch_settings.txt` (64-bit by default); Pulsar runs `srcds_win64.exe` with `-game pvkii`.
- As in CS:S, the console is `pvkii/console.log` and commands go over RCON, on TCP at the game port's number; ready
  when RCON answers, in about 4 s. `status` answered with the name, the map (bt_glacier) and the slots.
- A2S answers on the machine's network addresses, not on loopback, with the name, the map, the game mode ("PVKII:
  Booty"), the slots and whether a password is needed.
- The install has its own `pvkii/cfg/server.cfg`: Pulsar changes only the lines of the options set in the panel (the
  first start changed the name and nothing else). All the options were set on that server (time, round and win limits,
  finish the round, team balance and size difference, sudden death time, voice chat, tags, region, LAN only, downloads,
  pure server) and the game reported each value back. Voice chat has the game's three modes: own team only (0),
  everyone (1), players nearby and the own team always (2).
- The console shows `Unknown command "cl_interp"` and material warnings at every start: they come from the game's own
  files.
- Broadcasts arrive as `say` ("Console: …"). `quit` over RCON stops it in about 2 s, with exit code 0.
- Maps by mode: booty (bt_), team wars (tw_), territory (te_), last team standing (lts_), duel (duel_), competitive
  (comp_), and dm_fort.

## Players

The Players card kicks a player by name (`kick "…"`), as in the other Source games; not run with players on.
