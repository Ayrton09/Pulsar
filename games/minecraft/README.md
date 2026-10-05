# Minecraft: Java Edition

Checked on 2026-10-04 on Windows 11 Pro (build 26200), with Minecraft 26.3 and Eclipse Temurin 25.0.4.1+1, through a
development panel and agent:

- The `minecraftServer` step downloads `server.jar` from Mojang and the Java runtime that version asks for from
  Adoptium, checked against their published checksums, in about 10 s.
- Ready on `Done (...)! For help, type` in under 20 s with a new world. `stop` saves and exits with code 0 in about 2 s.
- The console is standard output and input (`pipes`); `-Dstdout.encoding=UTF-8` keeps accents readable.
- A Server List Ping answers with the version, the slots and the message of the day set in Pulsar.
- `eula.txt` is written only when the `eula` variable says the terms were accepted.
