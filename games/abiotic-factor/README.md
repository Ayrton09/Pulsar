# Abiotic Factor

Checked on 2026-10-26 on Windows 11 Pro (build 26200), with the server of build 26092453 (app 2857200, game version
1.5.0, about 3.3 GB), through a development panel and agent:

- SteamCMD installs it anonymously. Pulsar runs `AbioticFactor/Binaries/Win64/AbioticFactorServer-Win64-Shipping.exe`
  itself: the `AbioticFactorServer.exe` at the top only starts it.
- It is a windowed program (Unreal Engine 5): with `-log` it opens a console window of its own and also writes its log
  to standard output, which is the server's console in Pulsar (pipes). A pseudo console sees nothing of it.
- Its log file prints its whole command line, join and admin passwords included; Pulsar's console masks them (agents
  of 1.0.12), and the panel shows only what the game writes to standard output, where the command line is not.
- The world (`-WorldSaveName`, `Cascade` by default) is in `AbioticFactor/Saved/SaveGames/Server/Worlds/<world>`.
  The sandbox rules go in `AbioticFactor/Saved/Config/WindowsServer/SandboxSettings.ini` (`-SandboxIniPath` is relative
  to `Saved`); the game says which rules it took (`Key GameDifficulty sandbox option modified value: 2`).
- Ready when the Steam query answers, about 10 s after the start; A2S on the query port answers with the name and the
  slots. The console shows the session's short code (`Session short code: RZSC8`) players can join with.
- It reads no commands. Ctrl+C, sent by the server's host to the console the game opened (agents of 1.0.12), ends it in
  about 2 s with code 0 (`Engine exit requested (reason: ConsoleCtrl RequestExit)`). The game saves its world every
  few minutes by itself, not when it stops: a stop loses what happened since the last save.
