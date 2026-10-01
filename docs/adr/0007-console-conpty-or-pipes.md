# ADR-0007 — Console via ConPTY or pipes chosen by the manifest; not persisted in PostgreSQL

Status: Proposed — to be confirmed by the Phase 0 spikes.

## Context
Many Windows game servers (Source family) write through the Windows console API rather than stdout; classic redirection may not capture their output reliably (to be confirmed by spike). The console must be real time, with scrollback and reconnect.

## Decision
- The manifest declares `console.mode`: `pty` (ConPTY) or `pipes`.
- The agent keeps a ring buffer per server with a monotonic sequence and a rotated log on disk written in batches.
- Console output is only streamed to the panel while someone watches; browsers reconnect asking for the backlog from their last sequence.
- Console output is not stored in PostgreSQL.

## Alternatives
- Pipes only: probably insufficient for the priority games.
- RCON only: no complete output and game-specific; kept as plan B for sending commands.
- Storing the console in PostgreSQL: high volume without proportional value.

## Consequences
- ConPTY delivers a "screen" (VT sequences), so per-line timestamps are approximate.
- Requires Windows 10 1809+ / Windows Server 2019+.
