# Unturned

Checked on 2026-10-24 on Windows 11 Pro (build 26200), with the server of build 25613089 (app 1110390, about 1.9 GB),
through a development panel and agent:

- SteamCMD installs it anonymously. `Unturned.exe -batchmode -nographics +InternetServer/pulsar` runs the server
  whose folder is `Servers/pulsar`: its settings (`Server/Commands.dat`, `Config.txt`) and its world (`Level`).
- `Commands.dat` takes one `Command value` per line; Pulsar changes only the commands it sets and the game says so
  for each (`Successfully set gamemode to hard!`, `Successfully enabled PvE.`). `PvE` takes no value: Pulsar writes
  `PvE on` for PvE and removes the line for PvP.
- Ready on `Loading level: 100%` in 16 to 21 s. A2S on the game port answers with the name, the map, the slots and
  whether a password is needed (also on loopback). The server also prints a Server Code that friends can join with.
- Without a game server login token it says it is neither listed on the internet nor joinable over it.
- `Shutdown` from its console saves the world and exits with code 0 in about 2 s.

## Options added in 1.0.8

Day length (`Cycle`), chat delay (`Chatrate`), ping timeout (`Timeout`), decay time (`Decay`) and starting items (`Loadout`), in `Commands.dat`. The gameplay file (`Config.txt`) is not covered: Pulsar has no format for it.

They come from AMP's template and the game's server documentation; none was tried on a real server.
