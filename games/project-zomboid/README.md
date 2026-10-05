# Project Zomboid

Checked on 2026-10-05 on Windows 11 Pro (build 26200), with the Steam build of that day (app 380870), through a
published panel and a development agent:

- SteamCMD installs it anonymously (about 7 GB). The manifest runs the Java runtime the game ships
  (`jre64/bin/java.exe`) with the arguments of `StartServer64.bat`, with the heap the server's settings ask for.
- Output and commands go through standard output and input (`pipes`, UTF-8); `quit` saves and stops it in about
  35 s with code 0.
- `-cachedir` keeps the world and settings in the server folder (`zomboid/`); the settings are
  `zomboid/Server/pulsar.ini`, `key=value`, where the game adds its other options itself.
- `-adminpassword` sets the password of the in-game `admin` account at every start; the game does not write it to its
  log.
- Ready on `*** SERVER STARTED`: about 2–3 minutes for a new world.
- It answers A2S on the game port, listed or not.
- Workshop mods: `WorkshopItems` (the items' ids) and `Mods` (their mod ids); the server downloads the items itself.
