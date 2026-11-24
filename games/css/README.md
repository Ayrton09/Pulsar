# Counter-Strike: Source (css)

## Options added in 1.0.8

Server tags, region, Steam group, LAN only, cheats, voice chat, spectators, dead-player camera, frag limit, footsteps, fast download URL, allow downloads, pausable, pure server, max rate, max update rate and SourceTV auto-record, all in `cstrike/cfg/server.cfg`.

They come from AMP's template; none was tried on a real server.

## Players (1.0.11)

The Players card kicks a player by name (`kick "…"`). Source's bans take a user id or a Steam ID, which the server's query does not list, so the template has none. From the game's and hosting providers' documentation; not run on a server yet.

## Linux

The template runs on Linux nodes too: the agent's SteamCMD downloads the game's Linux files and starts `srcds_linux` itself
(Valve's start script is not used), a 32-bit program, with `bin` (as `srcds_run` does) as its library path. Checked for real on a Linux node
(Ubuntu 24.04): installed, started with the configured map, `status` over RCON, the player count from the query, stopped
with `quit` and deleted. Like every 32-bit Steam server it needs the 32-bit C++ library, which the agent's installer adds.
