# Counter-Strike 1.6 (cs16)

Checked on 2026-10-27 on Windows 11 Pro (build 26200), with HLDS 1.1.2.7 build 10210 (app 90, about 880 MB), through a
development panel and agent:

- SteamCMD installs HLDS anonymously; app 90 always brings Counter-Strike (`cstrike`) and Half-Life (`valve`). The
  server is the 32-bit `hlds.exe` with `-game cstrike`, a program with windows of its own: Pulsar runs it on the
  server's own desktop, so they never show on the node's screen.
- The console is `qconsole.log` (`-condebug`, in the server's folder) and commands go over GoldSrc's RCON, on UDP at
  the game port (`challenge rcon`, then `rcon <challenge> "<password>" <command>`); ready when RCON answers, in about
  2 s. `status`, cvar queries and `say` answered.
- HLDS writes every RCON command it gets to its console log, password included, and to `cstrike/logs` once logging is
  on (`log on`; checked). The console shows those lines with the password masked, and the file manager, SFTP, zips and
  backups never reach `qconsole.log` or `cstrike/logs` (Pulsar keeps them to itself, and `cstrike` cannot be
  renamed). The password is Pulsar's own (32 random letters and digits), and HLDS bans an address after 5 wrong ones
  for 30 minutes.
- The game port must be on every address or on a network address: on `127.0.0.1` HLDS cannot start Steam and stops
  with "Unable to initialize Steam".
- A2S answers on loopback and on the machine's addresses, in Source's format, with the name, the map, the game folder
  and the slots.
- The install's `cstrike/server.cfg` sets the name and the time limit; Pulsar patches only the options set in the
  panel. Time limit, friendly fire, buy time and the spectator camera were set on that server and the game reported
  each value back.
- `quit` over RCON stops it in about 2 s, with exit code 0xC0000409 as the Source games do; Pulsar records it as the
  requested stop.
- The maps: as_oilrig, cs_747, cs_assault, cs_backalley, cs_estate, cs_havana, cs_italy, cs_militia, cs_office,
  cs_siege, de_airstrip, de_aztec, de_cbble, de_chateau, de_dust, de_dust2, de_inferno, de_nuke, de_piranesi,
  de_prodigy, de_storm, de_survivor, de_torn, de_train, de_vertigo.

## Players

The Players card kicks a player by name (`kick "…"`); not run with players on.

## On a development agent

A development agent runs games as the user who started it. On that check, the first start of a fresh install, while
the user's own Steam client was open, stopped at the same "Unable to initialize Steam" dialog; every later start
worked. Nodes run each game as its own Windows account, with no Steam client of its own.
