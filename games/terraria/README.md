# Terraria

Checked on 2026-10-26 on Windows 11 Pro (build 26200), with TShock 6.2.1 for Terraria 1.4.5.8 and the .NET 9.0.22
runtime, through a development panel and agent:

- The server is TShock, Terraria's community server (GPL-3.0): Terraria's own Windows server needs Microsoft's XNA
  Framework installed on the machine, and TShock adds accounts, groups, permissions and bans. Pulsar installs its
  `win-x64` release from GitHub, checked against the SHA-256 GitHub publishes (the `githubRelease` step, agents of
  1.0.12), and the .NET 9 runtime it runs on into the server's `dotnet` folder, from Microsoft, checked against the
  SHA-512 Microsoft lists (the `dotnetRuntime` step): the agent points `DOTNET_ROOT` there, so the node needs no .NET of
  its own. Both took about 12 s; an update with nothing new downloads nothing (2 s), and an update check names both
  (`v6.2.1 + .NET 9.0.22`).
- Pulsar runs `TShock.Server.exe` with the port, the slots, the world (`worlds/<world>.wld`, made when it does not exist,
  with its size and seed) and `-savedirectory save`, without which Terraria keeps its files in the account's Documents.
  The difficulty of a new world is in `serverconfig.txt` (`-config`): TShock has no argument for it.
- Its console reads commands only through a pseudo console (pty): with pipes it takes none. Commands are TShock's
  (`/version`, `/who`, `/kick`, `/ban`) or Terraria's (`playing`, `say`, `save`, `exit`).
- `tshock/config.json` takes the name (shown in place of the world's), the join password, PvP, spawn protection and the
  whitelist; TShock writes the rest of its settings there at its first start, around Pulsar's. Its REST API stays off.
- The first start made a medium world in about 65 s; later starts load it in about 5 s. Ready on `Server started`.
  Terraria has no Steam query. The console shows a setup code at every start until an admin uses it: joining and typing
  `/setup <code>` makes a TShock superadmin, as TShock's guide says.
- `exit` saves the world and ends it with code 0 in about 2 s. A backup first sends `save` and waits 5 s (12 files,
  8 MB with a medium world, TShock's database included).
- The Players card's `/kick "…"` and `/ban add "…"` take a player's name (spaces allowed); they were not tried with
  players on.
