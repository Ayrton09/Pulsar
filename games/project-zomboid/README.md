# Project Zomboid

Checked on 2026-10-05 on Windows 11 Pro (build 26200), with the Steam build of that day (app 380870), through a
published panel and a development agent:

- SteamCMD installs it anonymously (about 7 GB). The manifest runs the Java runtime the game ships
  (`jre64/bin/java.exe`) with the arguments of `StartServer64.bat`, with the heap the server's settings ask for.
- Output and commands go through standard output and input (`pipes`, UTF-8); `quit` saves and stops it in about
  35 s with code 0.
- `-cachedir` keeps the world and settings in the server folder (`zomboid/`); the settings are
  `zomboid/Server/pulsar.ini`, `key=value`, where the game adds its other options itself.
- `-adminpassword` sets the password of the in-game `admin` account at every start; the game does not write it to its
  log.
- Ready on `*** SERVER STARTED`: about 2–3 minutes for a new world.
- It answers A2S on the game port, listed or not.
- Workshop mods: `WorkshopItems` (the items' ids) and `Mods` (their mod ids); the server downloads the items itself.

## Options added in 1.0.8

Version 1.2.0 adds 21 options to the server `.ini`: maps, announce deaths, no fire, safehouse rules (claiming, looting, entering, fire, respawn, days to claim), loot respawn, PvP status, PvP melee and firearm damage, sleep, accounts per Steam user, backups (count, at start, period), factions and the Lua checksum. The sandbox variables (zombies, loot, XP, day length) live in `SandboxVars.lua`, which Pulsar cannot patch yet.

They come from AMP's template for the game (and, where noted, other panels' templates or the game's documentation); they were not each tried on a real server. The test suite checks that they are written to the right file and key, and that an option left empty changes nothing.
