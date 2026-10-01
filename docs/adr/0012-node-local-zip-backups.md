# ADR-0012 — Zip backups stored locally on the node (v1)

Status: Proposed

## Context
Per-server backups with create/list/download/restore/delete, without blocking the system and without external infrastructure.

## Decision
- Zip format (Zip64) produced as a stream by the agent, with include/exclude globs from the manifest (excluding by default content reinstallable through SteamCMD).
- Stored in `%ProgramData%\Pulsar\Agent\backups\<shortId>` (path configurable per node).
- Executed as a cancellable operation with progress; SHA-256 recorded; restore only while the server is Offline and with anti zip-slip validation.

## Alternatives
- tar.gz / 7z: zip opens natively in Windows Explorer and is in the BCL.
- Remote storage (S3-compatible): valuable, but post-v1 (the `storage` column already allows for it).
- VSS snapshots for consistency while the server runs: post-v1.

## Consequences
- If the node's disk is lost, its backups are lost too (documented; remote storage post-v1).
- A backup taken while the server runs can capture half-written files: the UI warns about it.
