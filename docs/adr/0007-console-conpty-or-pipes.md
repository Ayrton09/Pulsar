# ADR-0007 — Console modes chosen by the manifest (ConPTY, pipes, log file + RCON); not persisted in PostgreSQL

Status: Accepted (2026-10-01) — revised after [spike 0001](../spikes/0001-console-capture.md).

## Context
The console must be real time, with scrollback, commands and reconnect. Spike 0001 showed that no single capture mechanism works for every process on Windows:

- SteamCMD's output is block-buffered on pipes but live on a ConPTY;
- output written through the console API is lost on pipes;
- `srcds.exe -console` (Source engine) crashes on pipes (it requires a console for input) and, given a ConPTY, allocates its own console and writes nothing to the one it was given.

## Decision
- The manifest declares `console.mode`:
  - `pty` — ConPTY stream (SteamCMD and well-behaved console applications);
  - `pipes` — redirected stdout/stderr/stdin (e.g. Java);
  - `logfile+rcon` — output by tailing a log file declared by the manifest, input and graceful stop through RCON (Source engine servers on Windows). The process still gets a ConPTY so it has a console.
- RCON credentials are generated and held by the agent per server (random, secret).
- The agent keeps a ring buffer per server with a monotonic sequence and a rotated log on disk written in batches, whatever the mode; RCON responses are inserted in the same stream.
- Console output is only streamed to the panel while someone watches; browsers reconnect asking for the backlog from their last sequence.
- Console output is not stored in PostgreSQL.

## Alternatives
- One mode for everything: impossible on Windows (see context).
- Scraping the game's own console buffer (`AttachConsole` + `ReadConsoleOutput`): fragile screen scraping, one helper process per server; rejected while log file + RCON works.
- Storing the console in PostgreSQL: high volume without proportional value.

## Consequences
- ConPTY delivers a "screen" (VT sequences): per-line timestamps are approximate in `pty` mode, exact in `pipes` and `logfile+rcon`.
- In `logfile+rcon` mode, RCON listens on the game's TCP port: the password must be long and random, and RCON ban settings applied (threat model T18).
- Commands executed over RCON are not written to the game's log file; the console shows their responses inline.
- Requires Windows 10 1809+ / Windows Server 2019+ (ConPTY).
