# Squad

Checked on 2026-10-26 on Windows 11 Pro (build 26200), with the server of build 26005357 (app 403240, game version
v10.6.3, about 14 GB), through a development panel and agent:

- SteamCMD installs it anonymously. Pulsar runs `SquadGame/Binaries/Win64/SquadGameServer.exe` with `Port=`,
  `QueryPort=`, `beaconport=`, `RCONPORT=` and `FIXEDMAXPLAYERS=`; with `-log` it writes its log to standard output,
  which is the server's console (pipes), and opens a console window of its own. About 4 GB of memory with nobody on it.
- It opened the game port, the query port and the beacon port (UDP), and RCON on the loopback (TCP). No port at the
  game or query port plus one, which older guides list.
- Its settings are the files of `SquadGame/ServerConfig`, `Key=Value` lines with `//` comments. Pulsar writes RCON's
  address, port and password in `Rcon.cfg` (an empty password turns RCON off), and the name, slots and options in
  `Server.cfg`; `ShowServerInfo` over RCON answered with the name and slots set there. `FIXEDMAXPLAYERS` keeps the slots
  whatever `Server.cfg` says. There is no join password in `Server.cfg` (`AdminSetServerPassword` sets one over RCON).
- It answers no Steam query (A2S) on its query port, from the loopback or the network: it lists itself through Epic
  Online Services. Ready on `Match State Changed from EnteringMap to WaitingToStart`, 12 to 30 s after the start.
- RCON (Source protocol) answered `ShowServerInfo`, `ShowCurrentMap`, `ListPlayers`, `ListCommands 1` and
  `AdminBroadcast` (`Message broadcasted <…>`). The Players card's `AdminKick` and `AdminBan` (for good) take a name or
  a Steam ID; they were not tried with players on.
- Ctrl+C, sent by the server's host to the console the game opened (agents of 1.0.12), ends it with code 0 in 4 to 10 s.
  Stopped while it was still loading its map, it hung after `PreExit Game` and was killed at the stop's timeout: it keeps
  no world, so nothing is lost.
