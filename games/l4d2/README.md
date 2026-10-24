# Left 4 Dead 2

Checked on 2026-10-24 on Windows 11 Pro (build 26200), with the server of build 23990100 (app 222860, about 10 GB),
through a development panel and agent:

- SteamCMD installs it anonymously. As in CS:S, the console is `left4dead2/console.log` and commands go over RCON;
  ready when RCON answers, in 4 to 6 s.
- The game mode cannot be set by the server: `mp_gamemode` is an unknown command to it (on its command line and at
  run time), and `map <map> versus` still runs a campaign. Players choose the mode in their lobby; started from the
  panel it runs the campaign of its map. `sv_alltalk` does not exist either.
- `sv_allow_lobby_connect_only 0` lets players join by address too.
- A2S answers on the machine's network addresses (not on loopback) with the name, the map and the slots, but not
  while the server has a join password: then the panel shows no players.
- `quit` over RCON stops it in about 2 s, sometimes with exit code 0xC0000409 as TF2.

## Options added in 1.0.8

Only `sv_*` cvars that AMP's template lists without a plugin requirement: tags, region, lobby search key, LAN only, cheats, voice, slots shown in the browser, pure, consistency, fast download URL and pausable, in `left4dead2/cfg/server.cfg`. No `mp_*` cvars (they need SourceMod) and no slots variable.

None was tried on a real server: as with `sv_alltalk`, a cvar this server does not know is ignored by the game, so check the console (`find <name>`) if one seems to do nothing.
