# ADR-0012 — Zip backups stored locally on the node (v1)

Status: Accepted (2026-10-03) — implemented in Phase 7 (docs 03, 05 and 08), with the notes below.

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

## Implementation notes (Phase 7)
- Making and restoring a backup are operations; deleting one is a plain command (removing a file needs no progress), so there is no `backup_delete` operation. Downloads reuse the file manager's transfer tickets.
- Each file's first 256 KiB decides how it is stored: deflated at its fastest level when that makes the sample at least a tenth smaller (configuration shrinks to a few percent), stored as it is otherwise (maps, archives, media). Deflating incompressible data made it 5.5% larger and about six times slower: a 1.5 GB file took 18 s and grew to 1.7 GB, stored it takes 3 s. The SHA-256 is computed in a second pass over the finished archive (zip writers seek back to patch headers).
- A backup leaves out files the game holds open and reports how many; a restore writes over what is there and keeps files the backup does not have (a restore that first empties the folders the backup covers is post-v1).
- Deleting a server always deletes its backups: the panel drops their rows with it, so archives left on the node would be orphans no one could see.
- Retention is per server: a maximum number of unlocked backups; locked ones are kept besides.
- There is no `storage` column yet: it arrives with remote storage, when there is a second value for it.
