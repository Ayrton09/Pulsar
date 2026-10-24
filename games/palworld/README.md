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

## Options added in 1.0.8

Version 1.2.0 adds these options: autosave interval, player-to-player damage, friendly fire, base raids, hardcore, Pals lost on death, Pal and player damage rates, player and Pal hunger and stamina rates, structure decay, item weight, equipment wear, bases on the server, buildings per player, supply drop interval, respawn delay, world backups, player list, client mods and public IP, then the death penalty (`DeathPenalty`) and the Pal randomizer (`RandomizerType`), 27 in all. Those two are written as bare words (`DeathPenalty=All`), as the game's own file and documentation have them: Pulsar writes a choice that is a plain word without quotes in a struct line (1.0.8), and quotes other text.

They come from AMP's template for the game (and, where noted, other panels' templates or the game's documentation); they were not each tried on a real server. The test suite checks that they are written to the right file and key, and that an option left empty changes nothing.
