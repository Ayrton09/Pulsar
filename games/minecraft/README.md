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

## Options added in 1.0.8

Max tick time, signed chat keys, world border radius, entity range, pause when empty, and the resource pack (URL, SHA-1, required, message), in `server.properties`. The description of `pvp` now says it only applies to 1.21.8 and older: newer versions moved it to a game rule that Pulsar cannot patch.

They come from the Minecraft wiki's `server.properties` table; none was tried on a real server.
