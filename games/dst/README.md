# Don't Starve Together

Checked on 2026-10-26 on Windows 11 Pro (build 26200), with the server of build 26230508 (app 343050, about 4.3 GB),
through a development panel and agent:

- SteamCMD installs it anonymously. Pulsar runs `bin64/dontstarve_dedicated_server_nullrenderer_x64.exe` from `bin64`
  with `-persistent_storage_root` in the server's folder (`{{server.dir}}`, which it wants absolute): its cluster is
  `save/Cluster_1`, its overworld shard `save/Cluster_1/Master`. Without that argument it would keep them in the
  account's Documents. The caves (a second shard, a second process) are not part of this template.
- Its console reads commands on standard input (pipes; the game prints `RemoteCommandInput: "…"`): Lua such as
  `c_listallplayers()`, `c_announce("…")` (`[Announcement] …`), `c_save()`, `c_shutdown(true)`.
- `cluster.ini` takes the name, description, password and play style (`[NETWORK]`), the game mode, PvP and pause when
  empty (`[GAMEPLAY]`), the console and one shard: one file's sections, each declared on its own (agents of 1.0.12). The
  ports and slots go on the command line (`-port`, `-players`, `-steam_master_server_port`, `-steam_authentication_port`),
  which overrides the files. The game mode is taken when a world is made.
- A server needs a cluster token from Klei to be online: `cluster_token.txt`, the whole file being the token (the
  `text` format, agents of 1.0.12). Without one the game says "Your Server Will Not Start", so Pulsar adds `-offline`:
  it then runs on the local network only (`Forcing LAN only setting in offline mode`). It was checked offline: no token
  was available.
- The first start generates a world in about 90 s (`An error occured during world gen we will retry!` once or twice is
  normal); later starts take about 30 to 60 s. Ready on `Telling Client our new session identifier`.
- A backup while it runs first sends `c_save()` and waits 5 s: 1.6 MB, 14 files. `c_shutdown(true)` saves the world and
  exits with code 0 in about 4 s. No Steam query answered offline, so the template has none.
- The Players card's `TheNet:Kick("…")` and `TheNet:Ban("…")` take a player's Klei id (`KU_…`), which `c_listallplayers()`
  shows; they were not tried with players on.
