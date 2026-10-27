# Arma Reforger

Checked on 2026-10-24 on Windows 11 Pro (build 26200), with the server of build 24870635 (app 1874900, about 10 GB),
through a development panel and agent:

- SteamCMD installs it anonymously. The server does not write its configuration: the install writes `server.json`
  with the game's required settings, and Pulsar sets its members before each start (`-config`). `-profile profile`
  keeps its logs, mods, saves and identity (`profile/profile/ownerToken.bin`) in the server folder.
- The server checks that file against its own schema and refuses to start on a member it does not know: the
  `persistence` section of older guides is refused by this build ("Param #/persistence is not allowed").
- The scenarios offered are the official multiplayer ones `-listScenarios` printed for this build.
- Ready when A2S answers on the query port, in 60 to 90 s (it registers with Bohemia's backend first). A2S gives the
  name, the scenario and the slots; it does not say whether a password is needed.
- Ctrl+C stops it with exit code 0 in about 4 s.

## Options added in 1.0.8

In `server.json`: network view distance, minimum grass distance, fast validation, voice UI options and cross-faction radios (all in `game.gameProperties`, which the shipped file already has), and player save period, AI limit, disable AI and slot reservation timeout (in `operating`, an object Pulsar adds to the file when one of them is set).

Their paths are those of AMP's template for the game. None was tried on a real server: as noted above, the server refuses to start on a member it does not know, so if a start fails after setting one of them, clear it.

## Mods (1.0.14)

The Mods setting lists mods of Arma Reforger's Workshop by id. In the panel it is an editor that searches the Workshop
and shows each mod by name, author, version and size; an id (16 characters) can also be typed. Checked on 2026-10-27
with build 24870635:

- The server downloads the mods itself when it starts, into `profile/addons`, and the mods they need with them: a
  list with only "Where Am I CombatOps Support" also brought "Where Am I", and the server loaded both and was ready in
  about 100 s.
- Pulsar writes the list into `game.mods` of `server.json` as `{ "modId": "..." }` objects, merged with what the file
  has: mods added to the file by hand stay, and so does a `version` (or `name`) added by hand to a mod the panel lists.
  A mod the panel wrote and no longer lists is taken out, with whatever was added to it.
- A mod that does not compile with the current build of the game ("Can't compile "Game" script module" in the
  console) leaves the server starting until its readiness times out: take it out of the list and start again.
- Backups leave `profile/addons` out; the server downloads the mods again.
