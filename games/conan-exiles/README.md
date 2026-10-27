# Conan Exiles

Checked on 2026-10-26 on Windows 11 Pro (build 26200), with the server of build 25792439 (app 443030, its public branch,
the Unreal Engine 5 "Enhanced" version, about 4.5 GB), through a development panel and agent:

- SteamCMD installs it anonymously. Pulsar runs `ConanSandbox/Binaries/Win64/ConanSandboxServer-Win64-Shipping.exe`
  itself (the `ConanSandboxServer.exe` at the top only starts it), with `-log`: its log on standard output is the
  server's console (pipes), and it opens a console window of its own.
- Its settings are in `ConanSandbox/Saved/Config/WindowsServer`: the name and join password in `Engine.ini`
  (`[OnlineSubsystem]`), RCON in `Game.ini` (`[RconPlugin]`), the admin password and the game options in
  `ServerSettings.ini` (`[ServerSettings]`, which the game fills with its 220 settings at the first start). The game
  rewrites these files; Pulsar changes only the keys it sets.
- The world is `ConanSandbox/Saved/game_0.db` (SQLite, with the game's own `game_0_backup_*.db` beside it, which
  backups leave out).
- Ready when the Steam query answers, 20 to 40 s after the start; A2S answers with the name, the slots and whether a
  password is needed.
- RCON (Source protocol) answered `help`, `listplayers`, `broadcast` (`Message has been broadcast.`) and
  `GetServerSetting`, which read back the PvP, avatar, nudity, idle kick and message of the day settings Pulsar wrote. It
  prints decimal settings empty (`PlayerDamageMultiplier=` even untouched), so the multipliers were checked in the file
  only. The Players card's `kickplayer name …` and `banplayer name …` were not tried with players on.
- Ctrl+C, sent by the server's host to the console the game opened (agents of 1.0.12), ends it with code 0 in 87 to
  93 s, after writing its database (its `-wal` and `-shm` files are gone afterwards): the stop's timeout is 180 s.
