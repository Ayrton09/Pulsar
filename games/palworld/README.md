# Palworld

Checked on 2026-10-04 on Windows 11 Pro (build 26200), with server v1.0.5.102999 (app 2394010), through a development
panel and agent:

- `PalServer.exe` is only a launcher: under a pseudo console its child's output never arrives, and with redirected
  output the game buffers it until it exits (and writes part of it in UTF-16). Pulsar runs
  `Pal/Binaries/Win64/PalServer-Win64-Shipping-Cmd.exe Pal` directly, in `pty` mode, with `-logformat=text`.
- The game reads no commands from its console: Pulsar sends them over RCON, which listens on TCP at the game port's
  number. `AdminPassword` is the RCON password the agent generates.
- Settings live in `Pal/Saved/Config/WindowsServer/PalWorldSettings.ini`, one `OptionSettings=(...)` line; Pulsar
  writes only its keys and keeps any others added by hand.
- Ready when RCON answers (about 6 s). `Save` then `Shutdown 1` stop it with code 0 in 4 to 6 s.
- `-QueryPort` moves the Steam query port; worlds are in `Pal/Saved/SaveGames`.
