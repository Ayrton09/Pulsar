# Counter-Strike 2 (cs2)

## Workshop maps (1.0.26)

A server started with only `+host_workshop_map` (no `+map`) never loaded the map: the Workshop download needs a running server, so the game has to be up on a map first. The template now always starts on the starting map (`+map`) and asks for the Workshop map or collection last, after the token (template 1.5.1). From a user's server; the change itself was not run on a real server.

## Steam's client libraries (1.0.25)

The dedicated server does not ship `steamclient64.dll`: it looks for it beside `cs2.exe` or in an installed Steam client, which the server's own account does not have, and then stops with "Failed to initialize Steamworks SDK for gameserver. Could not determine Steam client install directory". The install and the update copy it, with `tier0_s64.dll` and `vstdlib_s64.dll`, from the server's SteamCMD into `game/bin/win64` (`steamClient`). A server installed before gets them when it moves to this template version (1.5.0), which installs again, or at its next game update.

## Options added in 1.0.8

- Server: `sv_tags`, `sv_lan`, `sv_cheats`, `sv_voiceenable` (in `server.cfg`).
- Match rules, in the five `gamemode_*_server.cfg`: `mp_overtime_enable`, `mp_overtime_maxrounds`, `mp_halftime`, `mp_match_can_clinch`, `mp_c4timer`, `bot_difficulty`.
- Launch: Workshop map (`+host_workshop_map`, after `+map` since 1.0.26), Workshop collection (`+host_workshop_collection`) and map group (`+mapgroup`).

They come from AMP's template (tags, LAN, cheats, Workshop and map group) and from public CS2 cvar lists (the match rules); none was tried on a real server.

## Players (1.0.11)

The Players card kicks a player by name (`kick "…"`). Source's bans take a user id or a Steam ID, which the server's query does not list, so the template has none. From the game's and hosting providers' documentation; not run on a server yet.
