# Soulmask

Checked on 2026-10-26 on Windows 11 Pro (build 26200), with the server of build 26158250 (app 3017310, about 5.6 GB),
through a development panel and agent:

- SteamCMD installs it anonymously (app 3017310 is the Windows server; 3017300 is Linux's). Pulsar runs
  `WS/Binaries/Win64/WSServer-Win64-Shipping.exe Level01_Main -server -log …` itself, as the game's `StartServer.bat`
  does through `WSServer.exe`. The Steam libraries some guides say to copy beside it were not needed.
- It writes its log to standard output, which is the server's console in Pulsar (pipes), and to `WS/Saved/Logs/WS.log`.
  Its command line, passwords included, is only in the log file; Pulsar's console masks secrets anyway (agents of 1.0.12).
- Its RCON (Source RCON, `-rconaddr`, `-rconport`, `-rconpsw`) is the server's console for commands: Pulsar binds it to
  `127.0.0.1` on an internal port with a password of its own and sends the console's commands there. The game prints each
  command and its answer to its log (`TRY RUN ADMIN COMMAND: lp`, `RESPONSE: …`), so the console does not repeat them.
  `help` lists its commands: `lp` (players online), `fps`, `SaveWorld 1`, `SaveAndExit <seconds>`, backups of its
  database. It has no command to message or kick players.
- The game also always opens its echo port, a line console on the loopback that neither greets nor asks for a password:
  `-EchoPort=0` only makes its port a random one. Any program on the machine that finds it can run the server's commands,
  so on a node shared with servers whose mods you do not trust, keep Soulmask apart (Pulsar does not use that port).
- The world is a SQLite database in `WS/Saved/Worlds/Dedicated/Level01_Main/world.db`. The first start creates it in
  about 3 minutes; later starts take about 3 minutes as well.
- Ready when the Steam query answers; A2S on the query port answers with the name, the map, the slots and whether a
  password is needed (`PSW_s:1`).
- A backup while it runs first sends `SaveWorld 1` and waits 10 s (no line confirms the save): an 11 MB backup was made
  in 10 s. A stop sends `SaveAndExit 1`: the game saves its database (`WORLD IS TEARING DOWN, SAVE ALL PLAYERS`) and
  exits with code 0 in 12 to 25 s.
