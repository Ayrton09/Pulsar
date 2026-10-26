# Minecraft: Java Edition

Checked on 2026-10-04 on Windows 11 Pro (build 26200), with Minecraft 26.3 and Eclipse Temurin 25.0.4.1+1, through a
development panel and agent:

- The `minecraftServer` step downloads `server.jar` from Mojang and the Java runtime that version asks for from
  Adoptium, checked against their published checksums, in about 10 s.
- Ready on `Done (...)! For help, type` in under 20 s with a new world. `stop` saves and exits with code 0 in about 2 s.
- The console is standard output and input (`pipes`); `-Dstdout.encoding=UTF-8` keeps accents readable.
- A Server List Ping answers with the version, the slots and the message of the day set in Pulsar.
- `eula.txt` is written only when the `eula` variable says the terms were accepted.

Checked again on 2026-10-05 (Phase 11): the panel shows the message of the day, the version (26.3) and the player
count from the Server List Ping the agent sends every 10 seconds (`query`, on the game port).

## Server types (1.0.9)

`serverType` installs Mojang's server, Paper, Purpur, Fabric, Forge or NeoForge (doc 04, "Server types"), and
`loaderVersion` pins Fabric's loader or Forge's or NeoForge's version. Checked on 2026-10-25 on Windows 11 Pro through a
development panel and a real agent, one server moved through every type with updates:

- Paper 26.3 build 223 (its newest stable build): ready in 11 s; Paper downloads Mojang's jar at its first start.
- Fabric loader 0.20.1 on 26.3: `Loading Minecraft 26.3 with Fabric Loader 0.20.1`, ready. Paper's world did not load
  on it (`Missing data pack paper`), so the world was moved aside first; the description of `serverType` warns about it.
- Forge 66.1.0 (26.3's recommended), its installer run as the server's account: ready; its `win_args.txt` starts a
  shim jar, which the arguments file covers. Forge 58.1.26 on Minecraft 1.21.8: Java 21 instead of 25, ready.
- NeoForge 26.3.0.99 and Purpur 26.3 build 2654: ready.
- Each one's update check named what was installed ("26.3 (Paper 223)") and found nothing newer.
- Under the server's account the game's system report logs warnings about Windows performance counters it cannot read;
  they are harmless.

## Mods and plugins (1.0.9)

The template's `mods` section takes plugins from Modrinth on Paper (`paper`, `spigot`, `bukkit` projects, into `plugins`) and Purpur (`purpur` as well), and mods on Fabric, Forge and NeoForge (into `mods`); Mojang's own server takes neither. The Mods tab searches the catalog for the server's game version (the one its last install or update reported) and installs a project with the projects it requires, while the server is stopped (doc 04, "Mods"). Backups include `plugins`, `mods` and `config`.

Modrinth's modpacks (`.mrpack`) install too (`modpacks`, `loaderVersion`): a pack for Fabric, Forge or NeoForge installs the server again as that type, its Minecraft version and the loader version its index names, then its server files and overrides (doc 04, "Modpacks"). Quilt packs are not offered.

CurseForge is the second catalog (`curseforge`): mods by its loader types, Bukkit plugins on Paper and Purpur, and modpacks through the server files their authors publish. It needs the administrator's own API key, set on the Games page.

## Options added in 1.0.8

Max tick time, signed chat keys, world border radius, entity range, pause when empty, and the resource pack (URL, SHA-1, required, message), in `server.properties`. The description of `pvp` now says it only applies to 1.21.8 and older: newer versions moved it to a game rule that Pulsar cannot patch.

They come from the Minecraft wiki's `server.properties` table; none was tried on a real server.

## Players (1.0.11)

The Players card kicks, bans and unbans (`pardon`), adds to and takes off the whitelist, and makes and unmakes operators (`op`, `deop`) by name, and shows `banned-players.json`, `whitelist.json` and `ops.json` with a button that takes a player off each. The whitelist applies once **Whitelist** is on in the server's settings. Checked on 2026-10-26 on Windows 11 Pro with a Mojang 26.3 server, a real agent and the published panel: whitelist, op and ban from the card's field reached the console ("Added Notch to the whitelist", "Made Notch a server operator", "Banned Notch"), the three lists showed Notch, and unban, deop and a whitelist add from the card emptied and filled them again; a kick of a player who was not there answered "No player was found". Every action is in the audit log.

## Backups of a running server (1.0.11)

Newer versions print the save's confirmation as `System chat: Saved the game`: the template takes both forms (still not a player's chat line), so a backup goes on as soon as the game saved instead of waiting its two minutes. Read live (no snapshot), a backup or a zip leaves out `world/session.lock`, which the game keeps locked. Checked on 2026-10-26 with a Mojang 26.3 server and the dev agent (no snapshot): the save was confirmed, 31 files backed up and 1 left out, in a third of a second; the world zipped from the file manager while the server ran (25 files, `session.lock` left out) and unzipped into another folder.
