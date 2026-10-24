# Sons of the Forest

Checked on 2026-10-24 on Windows 11 Pro (build 26200), with the server of build 20228410 (app 2465200, about 3.5 GB),
through a development panel and agent:

- SteamCMD installs it anonymously. `-userdatapath` keeps its settings (`dedicatedserver.cfg`), its owners list and
  its saves (`userdata/Saves/.../0000000001` per slot, with the game's own backups) in the server folder.
- Its self tests stop the first starts with "Please restart the server" until `steam_appid.txt` (1326470) and
  `userdata/ownerswhitelist.txt` exist: the install writes both.
- Its public accessibility test asks Steam to reach the server's ports from outside and waited for minutes behind a
  router without forwarded ports: Pulsar turns it off (`SkipNetworkAccessibilityTest`).
- `dedicatedserver.cfg` gets only the settings Pulsar writes; the game reads the rest from its defaults. Set options
  are written with their JSON type (`"MaxPlayers": 4`, `"GameSettings": { "Gameplay.TreeRegrowth": false }`).
- Ready on `Dedicated server loaded.` in about 40 s. A2S on the query port answers with the name and the slots (also
  on loopback); it does not say whether a password is needed.
- It reads no commands from its console (`help` prints nothing). Ctrl+C saves the world (`Sigterm: Emergency
  Shutdown`, then `Autosave complete`) and exits with code 0 in about 4 s.
