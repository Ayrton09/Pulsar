# Half-Life Deathmatch (hldm)

Checked on 2026-10-27 on Windows 11 Pro (build 26200), with HLDS 1.1.2.2 build 10210 (app 90, about 880 MB), through a
development panel and agent:

- SteamCMD installs HLDS anonymously; app 90 always brings Half-Life (`valve`) and Counter-Strike. The server is the
  32-bit `hlds.exe` with `-game valve`, run on the server's own desktop like Counter-Strike 1.6's.
- Console and RCON as in Counter-Strike 1.6 (games/cs16/README.md): `qconsole.log` and `valve/logs`, which only the
  console shows, with the password masked, and GoldSrc's RCON on UDP at the game port; ready in about 2 s.
- A2S answers with the name, the map and the game ("Half-Life").
- Pulsar patches only the options set in the panel into `valve/server.cfg`. Mode (team deathmatch by player model),
  frag limit and weapons stay were set on that server and the game reported each value back; the mode takes effect at
  the next map.
- Broadcasts arrive as `say`. `quit` over RCON stops it in about 2 s.
- The maps: boot_camp, bounce, crossfire, datacore, frenzy, gasworks, lambda_bunker, rapidcore, snark_pit, stalkyard,
  subtransit, undertow.

## Players

The Players card kicks a player by name (`kick "…"`); not run with players on.

## Linux

The template runs on Linux nodes too: the agent's SteamCMD downloads the game's Linux files and starts `hlds_linux` itself
(Valve's start script is not used), a 32-bit program, with its own folder (`.`, as `hlds_run` does) as its library path. Checked for real on a Linux node
(Ubuntu 24.04): installed, started with the configured map, `status` over RCON, stopped
with `quit` and deleted. Like every 32-bit Steam server it needs the 32-bit C++ library, which the agent's installer adds.
