# Insurgency: Sandstorm

Checked on 2026-10-26 on Windows 11 Pro (build 26200), with the server of build 25534773 (app 581330, about 4.8 GB),
through a development panel and agent:

- SteamCMD installs it anonymously. Pulsar runs `Insurgency/Binaries/Win64/InsurgencyServer-Win64-Shipping.exe` itself;
  its log on standard output is the server's console (pipes).
- The first argument is the travel URL: the level, its scenario, the lighting, the slots and the join password
  (`Canyon?Scenario=Scenario_Crossing_Checkpoint_Security?Lighting=Day?MaxPlayers=10?Password=…`). A scenario on another
  level than its own loads the game's default map instead, so each scenario of the list carries its level. The list is
  the game's own (`scenarios` over RCON: 253, the shooting range and a test scenario left out).
- RCON is turned on in `Insurgency/Saved/Config/WindowsServer/Game.ini` (`[Rcon]`, on the loopback) rather than with
  `-Rcon -RconPassword`, which the log would show. `help`, `listplayers`, `maps`, `scenarios`, `say` and
  `gamemodeproperty` answer; `quit` ends it with code 0 in about 4 s. Once, its first stop after the install hung after
  `OnHandleSystemError` and was killed at the stop's timeout (30 s): it keeps no world, so nothing is lost.
- Its rules go into the sections of `Game.ini` the game modes read, and were checked with `gamemodeproperty` on a
  Checkpoint (co-op) and a Push (versus) scenario: friendly fire and its damage (`[/Script/Insurgency.INSMultiplayerMode]`
  for versus, `[/Script/Insurgency.INSCoopMode]` for co-op, whose own defaults win over the first), dead players' voice,
  team auto balance and map voting (versus), and the co-op enemies and friendly bots. `TeamKillLimit` and `bKillFeed`
  written there kept the game's values, so the template does not offer them.
- Ready when the Steam query answers, 12 to 45 s after the start; A2S on the query port answers with the name, the
  level, the slots and whether a password is needed (set by `?Password=` in the travel URL).
- Without a Game Server Login Token (for app 581320) the server runs but is not listed in the game's browser; no token
  was tried. The Players card's `kick` and `permban` take a name or a player id; they were not tried with players on.
