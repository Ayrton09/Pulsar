# 7 Days to Die

Checked on 2026-10-05 on Windows 11 Pro (build 26200), with the Steam build of that day (app 294420, V 3.2.0 b10),
through a published panel and a development agent:

- SteamCMD installs it anonymously (about 17 GB). Its `serverconfig.xml` is copied once to `pulsar-serverconfig.xml`
  (`copyFileIfMissing`), which the game's updates leave alone; Pulsar patches its `<property>` elements, and the game's
  comments stay.
- `-logfile -` sends the log to standard output (`pipes`); commands go to its telnet console, on an `internal` port
  the firewall never opens, with a password Pulsar generates (with a password the game listens on every interface).
- The game writes "Executing command" to its log but the answers only to telnet: the console shows each answer once,
  added by the agent (`logsCommands`).
- Ready on `StartGame done`: about 60–90 s on Navezgane.
- `shutdown` over telnet stops it in about 4–7 s with code 0.
- It answers A2S on the game port when listed (`ServerVisibility` 1 or 2), not when hidden.
- World data goes to `UserData` in the server folder (`UserDataFolder`), which backups take.
