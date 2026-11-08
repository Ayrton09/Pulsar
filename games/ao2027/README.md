# AO2027 (ao2027)

Checked on 2026-11-08 on Windows 11 Pro (build 26200), with CoverAO v1.0.6 (`CoverAO-servidor-win-x64.zip`, about
300 MB, 415 MB installed), through a development panel and agent:

- The install takes the latest release of `Ayrton09/CoverAO` from GitHub, checked against the SHA-256 GitHub publishes
  for the asset, and unpacks its `CoverAO-win-x64` folder (3,547 files) in about 30 s. The update check compares the
  latest release's asset with the installed one; the game's own `BuscarActualizaciones` only says in the console that
  there is a new version and downloads nothing.
- `Server.ini` (`[INIT]`) gets `StartPort` from the base port; the other options (player limit, experience and gold,
  new characters, game masters only) are written only when set in the panel, and every other line stays as it is.
- The game prints `Puertos:` when it is ready, about 2 s after it starts. Ctrl+C stops it in about 1 s ("Servidor
  detenido"), with exit code 0.
- Ports, all from `StartPort`: +0 is the classic protocol, +1 the WebSocket older web clients used, +2 the game's admin
  panel (on the machine only) and +3 the web client, page and `/ws`: the one players open, and the only one Pulsar
  opens in the firewall. The web client at `http://localhost:<port>/` loaded in a browser and read the players online
  from the server.
- The game listens with Windows' HTTP Server API, which lets an account other than an administrator listen on every
  address only with a URL reservation. Before every start the agent reserves `http://+:<web client port>/` for the
  server's account (released when the server is deleted or the agent is unenrolled). A reservation it cannot make is
  said in the console, and the game then answers on the machine only.
- `CoverAO-win-x64/Server.ini` is the release's only kept file: written by the first install, never replaced or
  removed by an update. Two of its lines edited in the file manager stayed as edited
  through an update that downloaded and unpacked the whole release again (one of its files, deleted on purpose, came
  back) and through a move to a new version of the template.
- Backups take `data` (the SQLite database `coverao.db` with its `-wal` and `-shm` files), `gamedata` and `Server.ini`,
  without the game's own backups and logs in `data/backups` and `data/logs`, and without `data/*.lock`, which the game
  holds open while it runs.
- The game reads no commands from its console: it is run from its admin panel and with the game masters' commands in
  the game. Its admin panel's token is printed in the console at every start, so whoever can read the server's console
  in Pulsar sees it; the panel answers on the machine only.

## On a development agent

A development agent runs games as the user who started it, and without elevation it cannot reserve URLs: on that check
the console said `http://+:<port>/ could not be reserved for the server's account (Acceso denegado.)` and the game
answered on `localhost` only, as it said itself. The agent of a node runs as LocalSystem and makes the reservation.
