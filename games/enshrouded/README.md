# Enshrouded

Checked on 2026-10-24 on Windows 11 Pro (build 26200), with the server of build 26083076 (app 2278520, about 8 GB),
through a development panel and agent:

- SteamCMD installs it anonymously. `enshrouded_server.exe` takes no arguments: everything is in
  `enshrouded_server.json`, next to it. The game rewrites that file whole when it starts, with every setting it
  knows; Pulsar changes only the members it sets before each start.
- Players join with a user group's password. A group without one gets a random password made up by the game, so a
  server could not be public: the install writes the file with a `Default` group, and the join password goes to the
  first group (`/userGroups/0/password`), written empty for a public server. Groups added by hand stay as they are.
- `gameSettings` only apply with the `Custom` difficulty (`gameSettingsPreset`).
- Ready on `[Session] 'HostOnline' (up)!` in about 12 s. A2S on the game port answers on the machine's network
  addresses, not on loopback (as srcds), with the name, the slots and whether a password is needed.
- Ctrl+C saves the world (`savegame/world0.ksd`) and exits with code 0 in about 6 s.

## Options added in 1.0.8

Version 1.1.0 adds 28 options: voice chat range, player mana, body heat and diving time, food buff duration, time in the Shroud, what is lost on death, glider turbulences, weather, fishing, plant growth, workstation speed, weapon upgrade cost, rune return, mining and exploration XP, enemy amount, simultaneous attackers, enemy stamina, perception and attack frequency, boss health and damage, taming failure, Shroud curse, and the lengths of day, night and the hungry state (the game stores them in nanoseconds; the options are fixed choices inside the range AMP documents, 2 to 60 minutes for day and night). Like the older ones, the `gameSettings` options apply only with the `Custom` difficulty.

They come from AMP's template for the game (and, where noted, other panels' templates or the game's documentation); they were not each tried on a real server. The test suite checks that they are written to the right file and key, and that an option left empty changes nothing.
