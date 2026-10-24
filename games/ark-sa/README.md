# ARK: Survival Ascended

**Licence.** ARK: Survival Ascended dedicated servers are for personal, non-commercial use unless you have a licence
from the game's publisher (its server hosting partner program). Check its terms before renting servers to others.

Checked on 2026-10-05 on Windows 11 Pro (build 26200), with the Steam build of that day (app 2430930, ARK version
94.15), through a published panel and a development agent:

- SteamCMD installs it anonymously (about 12 GB).
- The server writes nothing useful to its console (only GameAnalytics messages): the console is
  `ShooterGame/Saved/Logs/ShooterGame.log` and commands go over RCON (`logfile+rcon`).
- It logs its whole command line (`Commandline: ...`), so nothing secret goes there: the RCON password
  (`ServerAdminPassword`) and the join password are written to `GameUserSettings.ini`, `[ServerSettings]`, where the
  game reads them too. RCON listens on the TCP side of the game port, which the firewall rule (UDP) does not open.
- `Full Startup` comes after about 45 s; players can join only once it logs `is now advertising for join`, about two
  and a half minutes after the start with a new world, which is when Pulsar reports it ready.
- RCON answers take 2 to 4 s even with nobody playing (the agent waits up to 15 s).
- `SaveWorld`, a 5 s wait and `DoExit` stop it in about 20–30 s; it exits with code -1.
- It does not answer A2S queries (it is listed through Epic Online Services), so the panel shows no players.
- An empty server uses about 10 GB of memory: a 4 GB memory limit stopped it after 100 s, reported as such.
- Mods are CurseForge project ids (`-mods=`); the server downloads them itself.
- On Windows Server, its own guides say the Amazon root certificate revocation list must be imported for it to be
  listed; Pulsar does not change the machine's certificates.

## Options added in 1.0.8

Version 1.2.0 adds 19 options: hardcore, player and wild creature damage and resistance, structure resistance, item stack size, resource respawn period, harvest health, player food and water drain, creature food drain, offline raid protection and its delay, max tamed creatures per tribe, max structures in range, hit markers (`GameUserSettings.ini`), players per tribe and no friendly fire in PvP (`Game.ini`).

They come from AMP's template for the game (and, where noted, other panels' templates or the game's documentation); they were not each tried on a real server. The test suite checks that they are written to the right file and key, and that an option left empty changes nothing.
