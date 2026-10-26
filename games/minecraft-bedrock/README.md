# Minecraft: Bedrock Edition

Checked on 2026-10-24 on Windows 11 Pro (build 26200), with Bedrock Dedicated Server 1.26.52.3, through a
development panel and agent:

- The `minecraftBedrock` step asks Mojang's download list for the latest Windows build
  (`bedrock-server-1.26.52.3.zip`, 86 MB), downloads it from www.minecraft.net and checks that `bedrock_server.exe`
  carries a valid Authenticode signature of Microsoft Corporation's before anything is installed. Unpacking its
  3,000 files took about 2 minutes; the console showed the download every 10 % and each stage.
- An update keeps `server.properties`, `allowlist.json`, `permissions.json` and `packetlimitconfig.json` (the worlds
  are not in the download), and the same version is not downloaded again (`.pulsar/bedrock-version.txt`).
- This release accepts players only over NetherNet: with `transport=raknet` it starts but says players cannot connect.
  NetherNet listens on one dual-stack TCP socket on the game port (`Accepting clients on [::]:19132`) for the
  handshake; the game traffic then goes over UDP, on any free port unless **UDP ports** (`server-udp-ports`) sets a
  range. Behind a router or a firewall, players from outside need the TCP port and that UDP range opened there:
  Pulsar's firewall rules cover the game port only.
- LAN visibility takes UDP port 7551 whatever the game port is, so it is off by default: turn it on for one server of
  the machine at most.
- Ready on `Server started.` in about 4 s; `stop` saves and exits with code 0 in about 2 s. The console is a pseudo
  console (`pty`); commands typed in the panel run as in the game's own window.
- The server signs in to Microsoft's signaling service at start, so it needs the internet.
- There is no query: the classic RakNet ping is gone with NetherNet, so the panel shows no player count.

The EULA option records that whoever creates the server accepts Mojang's terms; Bedrock's server has no file for it.

## Players (1.0.11)

The Players card kicks (`kick "…"`), adds to and takes off the allowlist (`allowlist add|remove "…"`, which applies once `allow-list` is on in the server's settings) and makes operators (`op`/`deop`); gamertags may have spaces. It shows `allowlist.json`. Bedrock has no ban command, and keeps its operators in `permissions.json` by Xbox user id only, so that list is not shown. Bedrock answers no query listing its players, so the card acts by name. From the game's and hosting providers' documentation; not run on a server yet.
