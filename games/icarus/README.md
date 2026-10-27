# Icarus

Checked on 2026-10-26 on Windows 11 Pro (build 26200), with the server of build 26134457 (app 2089300, game version
3.0.33, about 10 GB), through a development panel and agent:

- SteamCMD installs it anonymously. Pulsar runs `Icarus/Binaries/Win64/IcarusServer-Win64-Shipping.exe` itself: the
  `IcarusServer.exe` at the top only starts it.
- It is a windowed program (Unreal Engine): with `-Log` it opens a console window of its own and writes its log to
  standard output too, which is the server's console in Pulsar (pipes).
- Its settings are in `Icarus/Saved/Config/WindowsServer/ServerSettings.ini`, section
  `[/Script/Icarus.DedicatedServerSettings]`, which it does not write itself: Pulsar writes the slots, the join and
  admin passwords and the prospect options there (the passwords stay off the command line, which the game logs). Its
  prospects go to `Icarus/Saved/PlayerData/DedicatedServer`. Without `-UserDir` everything stays in the server's folder.
- The game shuts itself down when nobody joins within five minutes, and when the last player leaves
  (`ShutdownIfNotJoinedFor`, `ShutdownIfEmptyFor`): Pulsar sets both to `-1`, and the server was still running after
  seven minutes with nobody on it.
- Ready when the Steam query answers, about 20 s after the start; A2S on the query port answers with the name and the
  slots. It waits in its lobby for players to launch or resume a prospect (`CreateProspect` in its settings sent its
  request but loaded no prospect without a player, so the template does not offer it).
- It reads no commands from its console: admins log in from the game (`/AdminLogin`), so the template requires an admin
  password (without one, anyone who types `/AdminLogin` would be an admin). Ctrl+C, sent by the server's host to the
  console the game opened (agents of 1.0.12), ends it in about 4 s with code 0.
