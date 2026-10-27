# Half-Life 2: Deathmatch (hl2dm)

Checked on 2026-10-27 on Windows 11 Pro (build 26200), with the server of build 24600426 (app 232370, about 1 GB),
through a development panel and agent:

- SteamCMD installs it anonymously. The server is the 32-bit `srcds.exe` (the app ships no 64-bit one) with
  `-game hl2mp`; the app also brings the parts of Half-Life 2 the game needs (`hl2`, `hl2_complete`).
- As in CS:S, the console is `hl2mp/console.log` and commands go over RCON, on TCP at the game port's number; ready
  when RCON answers, in about 7 s. `status` answered with the name, the map and the slots.
- A2S answers on the machine's network addresses, not on loopback, with the name, the map, the game ("Half-Life 2
  Deathmatch"), the slots and whether a password is needed.
- The install has no `hl2mp/cfg/server.cfg`: Pulsar writes one with the options set in the panel. All the options were
  set on that server (mode, frag and time limits, friendly fire, weapons stay, force respawn, fall damage, flashlight,
  footsteps, all talk, tags, region, LAN only, cheats, downloads, pure server) and the game reported each value back.
  The mode (`mp_teamplay`) and `sv_pure` take effect at the next map.
- Broadcasts arrive as `say` ("Console: …"). `quit` over RCON stops it in about 1 s, with exit code 0xC0000409 as
  CS:S and TF2 do; Pulsar records it as the requested stop.
- The maps: dm_lockdown, dm_overwatch, dm_powerhouse, dm_resistance, dm_runoff, dm_steamlab, dm_underpass and halls3.

## Players

The Players card kicks a player by name (`kick "…"`), as in the other Source games; not run with players on.
