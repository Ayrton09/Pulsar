# AO2027 (ao2027)

Checked on 2026-11-08 on Windows 11 Pro (build 26200), with CoverAO v1.0.6 (`CoverAO-servidor-win-x64.zip`, about
300 MB, 415 MB installed), through a development panel and agent:

- The install takes the latest release of `Ayrton09/AO-2027` from GitHub, checked against the SHA-256 GitHub publishes
  for the asset, and unpacks its `CoverAO-win-x64` folder (3,547 files) in about 30 s. Until 1.0.8 the releases were on
  `Ayrton09/CoverAO`; from 1.0.9 (2026-11-08) code and downloads are in `Ayrton09/AO-2027` alone, with the same server
  asset: the 1.0.9 zip was checked by its contents (the same folder, program, `Server.ini` and `data` as before; 3,548
  files, 435 MB unpacked), and template 1.0.1 takes it from the new repository. The update check compares the
  latest release's asset with the installed one; the game's own `BuscarActualizaciones` only says in the console that
  there is a new version and downloads nothing.
- `Server.ini` (`[INIT]`) gets `StartPort` from the base port; the other options are written only when set in the
  panel, and every other line stays as it is. From template 1.0.3 (checked against CoverAO 1.0.14's `ServerIni.cs`)
  they are: player limit, experience and gold, new characters, game masters only; bots, day and weather length,
  rain, global drop, campfires, items on the floor, sailing to cross; gold and full heal on level up, newbie items,
  the anti-macro sentinel, the familiar, the druid's metamorphosis, Rinkel passes, seahorses; whether guards attack,
  chase and attack newbies, and whom each side's guards attack; connections, characters and new accounts per IP,
  idle limit, cheats to kick and to ban, shortest password; saving characters, the game's own world copies and how
  many it keeps, and log days. A guards' list left empty in the panel stays as the file has it: an empty list (that
  side attacks nobody) is set in the file.
- From template 1.0.4 (Pulsar 1.0.42) the server's card and page show the players online and their names, from the
  game's public status (`/api/publico.json` on the Game port, what the app's server list reads), asked by the agent on
  the machine: checked on 2026-11-13 against CoverAO 1.0.17, 10 online, names and version read. The game says no
  maximum, so the panel shows the count alone; its bots are counted and listed like players (the status does not tell
  them apart). With `PaginaPublica=0` the game does not answer it and the panel shows no players.
- The game prints `Puertos:` when it is ready, about 2 s after it starts. Ctrl+C stops it in about 1 s ("Servidor
  detenido"), with exit code 0.
- Players play with the game's desktop app, CoverAO (`CoverAO-Setup.exe`, from the same releases): they add the
  server in it by address and Game port, for example `190.1.2.3:7669`. The app loads the server's page from that port
  and plays through `/ws` on it.
- Ports, all from `StartPort`: +0 is the classic protocol, +1 the WebSocket older clients used, +2 the game's admin
  panel (on the machine only) and +3 the Game port, the page and `/ws`: the only one Pulsar opens in the firewall and
  the address it shows. The page the app loads, opened at `http://localhost:<port>/`, loaded whole and read the players
  online from the server.
- The game listens with Windows' HTTP Server API, which lets an account other than an administrator listen on every
  address only with a URL reservation. Before every start the agent reserves `http://+:<Game port>/` for the
  server's account (released when the server is deleted or the agent is unenrolled). A reservation it cannot make is
  said in the console, and the game then answers on the machine only.
- `CoverAO-win-x64/Server.ini` is the release's only kept file: written by the first install, never replaced or
  removed by an update. Two of its lines edited in the file manager stayed as edited
  through an update that downloaded and unpacked the whole release again (one of its files, deleted on purpose, came
  back) and through a move to a new version of the template.
- Backups take `data` (the SQLite database `coverao.db` with its `-wal` and `-shm` files), `gamedata` and `Server.ini`,
  without the game's own backups and logs in `data/backups` and `data/logs`, and without `data/*.lock`, which the game
  holds open while it runs. SQLite is the game's default.
- The characters and accounts can be in PostgreSQL instead (template 1.0.2, Pulsar 1.0.37): in the server's
  **Databases** tab, choose a PostgreSQL host for the game's database. Pulsar makes `s<short id>_game` there and starts
  the game with `COVERAO_BASEDEDATOS_TIPO`, `_SERVIDOR`, `_PUERTO`, `_BASE`, `_USUARIO` and `_CLAVE` in its environment,
  which the game reads instead of `Server.ini`'s `[BASEDEDATOS]` (it says so at start: `[ini] pisado por el entorno:`
  and the names, never the values); `Server.ini` keeps no password. At its first start on an empty database the game
  takes the characters of its SQLite file (`data/coverao.db`), which stays as it was. Set by hand in `Server.ini`
  instead (`Tipo=postgres` and the rest), the database is outside what Pulsar knows: its backups would not have it.
- With its database chosen in Pulsar, Pulsar's backups hold it (a `pg_dump` the panel makes, kept in the archive), and a
  full restore puts it back first. The game's own backups in `data/backups` stay out of Pulsar's either way.
- Checked with v1.0.10 on PostgreSQL 18: at its first start on the chosen database the game took the 6 characters and
  accounts of its SQLite file; a backup held the database (`.pulsar/database.dump`, 75 KB); after the characters were
  changed by hand, a full restore put them back as they were in the backup, and the game started on them again.
- An update unpacks the whole release again: a map in `gamedata` edited by hand is replaced by the release's (the
  game's own updater, `Actualizar.bat`, would keep it). `Server.ini` is the only file kept, so take a backup before
  updating a server whose maps were changed.
- The game reads no commands from its console: it is run from its admin panel and with the game masters' commands in
  the game. Its admin panel's token is printed in the console at every start, so whoever can read the server's console
  in Pulsar sees it; the panel answers on the machine only.

## On a development agent

A development agent runs games as the user who started it, and without elevation it cannot reserve URLs: on that check
the console said `http://+:<port>/ could not be reserved for the server's account (Acceso denegado.)` and the game
answered on `localhost` only, as it said itself. The agent of a node runs as LocalSystem and makes the reservation.
