# Killing Floor 2

Checked on 2026-10-26 on Windows 11 Pro (build 26200), with the server of build 14007421 (app 232130, about 25 GB),
through a development panel and agent:

- SteamCMD installs it anonymously. Pulsar runs `Binaries/Win64/KFServer.exe` from that folder, with the travel URL
  (map, game mode, difficulty, length, slots), `-Port` and `-QueryPort`. It opens a window of its own and writes nothing
  to standard output: its log, `KFGame/Logs/Launch.log`, is the server's console (pipes with a log file, agents of 1.0.12).
- Its settings are `KFGame/Config/PCServer-KFGame.ini` and the other `PCServer-*` files, which it writes at its first
  start around the keys Pulsar set there: the name in `[Engine.GameReplicationInfo]`, the join and admin passwords in
  `[Engine.AccessControl]`, the message of the day, friendly fire and the votes in `[KFGame.KFGameInfo]`. Its web admin
  stays off (`KFWeb.ini`, `bEnabled=false`).
- Ready when the Steam query answers, about 12 s after the start; A2S answers with the name, the map, the slots and
  whether a password is needed. The very first start after the install ended by itself with code -1 after 12 s, before
  any map was loaded, and Pulsar started it again (a crash restart); a start with the settings files deleted afterwards
  ran at once.
- It reads no commands from its console. Ctrl+C, sent by the server's host to the console the game opened (agents of
  1.0.12), ends it with code 0 in about 2 s (`Exit: Exiting.`). It keeps no world.
