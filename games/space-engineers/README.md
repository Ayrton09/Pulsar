# Space Engineers

Checked on 2026-10-26 on Windows 11 Pro (build 26200), with the server of version 1.210.014 (app 298740, about 8.4 GB),
through a development panel and agent:

- SteamCMD installs it anonymously. Pulsar runs `DedicatedServer64/SpaceEngineersDedicated.exe -console -path
  {{server.dir}}/instance`: its settings (`SpaceEngineers-Dedicated.cfg`), saves (`Saves/<world>`), mods and logs are in
  the server's `instance` folder, not in the account's AppData.
- Run by the agent, it writes nothing to standard output after its first line: what its console window shows goes to
  its log, which it names by the time it started (`instance/SpaceEngineersDedicated_<date>_<time>.log`). That log is the
  server's console: Pulsar follows the newest one that appeared with the start (`logFile` with a `*`, agents of 1.0.12).
  It reads no commands.
- `SpaceEngineers-Dedicated.cfg` is patched element by element (the `xml` format, agents of 1.0.12): the address and
  ports, the name, the world's name, its scenario, the description and message of the day, pause when empty, and the
  session settings (slots, game mode, inventory size, the assembler, refinery, welder and grinder speeds, meteorites,
  autosave). Its remote API stays off. The session settings and the scenario are taken when a new world is made; the
  world keeps its own afterwards (`Saves/<world>/Sandbox_config.sbc`).
- The first start made the world from the Earth Planet scenario in about 30 s; later starts load it again in about
  17 s. Ready on `Game ready...`. The Steam query answers on the game port with the name, the world, the slots and the
  players.
- Ctrl+C, sent by the server's host to the console the game opened (agents of 1.0.12), saves the world and ends it with
  code 0 in 4 to 7 s.
- Backups take `instance/Saves` (without the game's own `Backup` folders) and the settings file.
