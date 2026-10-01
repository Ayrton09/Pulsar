# ADR-0005 — Agent as LocalSystem and one Windows account per game server

Status: Accepted (2026-10-01) — implemented in Phase 3, before the file manager.

## Context
Game servers run untrusted code (plugins, mods, Lua). The agent needs privileges to create accounts, ACLs and firewall rules, and to launch processes under another identity.

## Decision
- The agent service runs as LocalSystem.
- No game server runs as SYSTEM: each server has a local account `pulsar_<shortId>` with only the batch logon right, interactive/RDP/network logon denied, and Modify ACL only on its own directory. Its profile is loaded, and the game runs with a restricted token without privileges (`DISABLE_MAX_PRIVILEGE`).
- Isolation relies on directory ACLs. The token always contains `BUILTIN\Users` (through `Authenticated Users`), so membership in Users is not a control ([spike 0002](../spikes/0002-isolated-launch.md)).
- SteamCMD runs under its own `pulsar_steamcmd` account.
- The agent's file and backup operations run impersonating the server account.
- A "current user" mode exists only for development with the agent in a console, with explicit warnings.

## Alternatives
- Everything under one service account: no isolation between servers.
- One shared low-privilege account: protects the OS but not one server from another.
- AppContainer: stronger isolation, but breaks networking and paths of many games.

## Consequences
- A compromised plugin stays confined to its server's directory and cannot use privileges such as `SeShutdownPrivilege`. It can still read what other servers leave in Users-writable system locations (`C:\ProgramData`, `C:\Users\Public`); accepted for v1.
- Real complexity: LSA rights, profiles, window station/desktop, DPAPI-encrypted passwords. Validated by [spike 0002](../spikes/0002-isolated-launch.md) for console processes; a real game launched this way is checked in Phase 3.
- Not supported on domain controllers.
- Doing it later would be expensive (ACLs and disk layout of existing servers), which is why it is decided now.
