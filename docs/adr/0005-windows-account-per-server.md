# ADR-0005 — Agent as LocalSystem and one Windows account per game server

Status: Accepted (2026-10-01) — implemented in Phase 3, before the file manager.

## Context
Game servers run untrusted code (plugins, mods, Lua). The agent needs privileges to create accounts, ACLs and firewall rules, and to launch processes under another identity.

## Decision
- The agent service runs as LocalSystem.
- No game server runs as SYSTEM: each server has a local account `pulsar_<shortId>`, outside the Users group, with only the batch logon right, interactive/RDP/network logon denied, and Modify ACL only on its own directory.
- SteamCMD runs under its own `pulsar_steamcmd` account.
- The agent's file and backup operations run impersonating the server account.
- A "current user" mode exists only for development with the agent in a console, with explicit warnings.

## Alternatives
- Everything under one service account: no isolation between servers.
- One shared low-privilege account: protects the OS but not one server from another.
- AppContainer: stronger isolation, but breaks networking and paths of many games.

## Consequences
- A compromised plugin stays confined to its server.
- Real complexity: LSA rights, profiles, window station/desktop, DPAPI-encrypted passwords. Validated by a Phase 0 spike.
- Not supported on domain controllers.
- Doing it later would be expensive (ACLs and disk layout of existing servers), which is why it is decided now.
