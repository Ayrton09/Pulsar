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

## Options added in 1.0.8

Version 1.2.0 replaces the twelve difficulty options that Dead Hot Summer (game V3.0) removed from `serverconfig.xml` (difficulty, day length, daylight hours, blood moon frequency and zombies, zombie speeds, drop on death, loot abundance and respawn, XP, air drops) with `sandboxCode` (`SandboxCode`), and adds region, language, random world seed and size, max animals, max view distance, land claim count, size, expiry and decay, bedroll expiry, safe zone level, crossplay, persistent player profiles and creative mode. A server that upgrades to this version loses the stored values of the removed options (the upgrade drops values of variables the new version no longer has); a server on 2.6 or older should stay on 1.1.0.

They come from AMP's template for the game (and, where noted, other panels' templates or the game's documentation); they were not each tried on a real server. The test suite checks that they are written to the right file and key, and that an option left empty changes nothing.

## Players (1.0.11)

The Players card kicks and bans (for ten years: `ban add "…" 10 years`) a player who is on the server, by name. Removing a ban takes the player's platform id (`ban remove`): use the console. From the game's and hosting providers' documentation; not run on a server yet.
