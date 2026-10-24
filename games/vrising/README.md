# V Rising

Checked on 2026-10-24 on Windows 11 Pro (build 26200), with server v1.1.15.0 (app 1829350, build 25169871, about
2 GB), through a development panel and agent:

- SteamCMD installs it anonymously; `SteamAppId=1604030` lets it start without the Steam client.
- `-persistentDataPath save-data` keeps its settings (`save-data/Settings`) and worlds (`save-data/Saves/v4/<world>`)
  in the server folder; the install copies the game's own `ServerHostSettings.json` and `ServerGameSettings.json`
  there, which its updates leave alone.
- The join password and RCON are set in `ServerHostSettings.json`, so no password is on the command line. RCON
  listens on 127.0.0.1 only: Pulsar's console commands and readiness use it.
- Ready (RCON answering) in 26 to 46 s. A2S on the query port answers with the name, the slots and whether a password
  is needed.
- Ctrl+C saves the world (`SaveOnExit() - Saving the world on exit!`) and exits with code 0 in 6 to 8 s.
- The options left empty leave `ServerGameSettings.json` as it is; set ones are written with their JSON type
  (`"ClanSize": 4`, `"BloodBoundEquipment": false`, `"MaterialYieldModifier_Global": 2.5`), and the game took them.

## Options added in 1.0.8

Version 1.1.0 adds a ruleset (`GameSettingsPreset`), admin slots, autosaves kept and server frame rate (`ServerHostSettings.json`), and 17 custom game rules in `ServerGameSettings.json`: new player PvP protection, player damage, castle heart, siege golem health, who can loot a body, drops, stack size, blood essence yield, day length, blood moon range, player, enemy and boss health and damage. A ruleset or a difficulty overrides the custom game options (AMP's notes), so the descriptions of the older options now say so. The ruleset starts on Custom (an empty `GameSettingsPreset`, the game's own default), which uses the custom options; it is always written, so going back to Custom from a ruleset works.

They come from AMP's template for the game (and, where noted, other panels' templates or the game's documentation); they were not each tried on a real server. The test suite checks that they are written to the right file and key, and that an option left empty changes nothing.
