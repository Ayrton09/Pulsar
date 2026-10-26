# Counter-Strike 2 (cs2)

## Options added in 1.0.8

- Server: `sv_tags`, `sv_lan`, `sv_cheats`, `sv_voiceenable` (in `server.cfg`).
- Match rules, in the five `gamemode_*_server.cfg`: `mp_overtime_enable`, `mp_overtime_maxrounds`, `mp_halftime`, `mp_match_can_clinch`, `mp_c4timer`, `bot_difficulty`.
- Launch: Workshop map (`+host_workshop_map`, used instead of `+map` when set), Workshop collection (`+host_workshop_collection`) and map group (`+mapgroup`).

They come from AMP's template (tags, LAN, cheats, Workshop and map group) and from public CS2 cvar lists (the match rules); none was tried on a real server.

## Players (1.0.11)

The Players card kicks a player by name (`kick "…"`). Source's bans take a user id or a Steam ID, which the server's query does not list, so the template has none. From the game's and hosting providers' documentation; not run on a server yet.
