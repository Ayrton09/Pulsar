# ADR-0010 — Panel-side scheduler on PostgreSQL, without Quartz or Hangfire

Status: Accepted (2026-10-03) — implemented in Phase 8 (docs 02, 05, 07 and 08), with the notes below.

## Context
Typed scheduled tasks (restart, backup, update, console command) with cron and time zones are needed, without extra infrastructure.

## Decision
A `BackgroundService` in the panel: tasks with `next_run_at` (UTC), selection with `FOR UPDATE SKIP LOCKED`, waiting until the next run (woken when tasks change), occurrences computed with the Cronos library (IANA time zones and DST). Runs are recorded and audited. Runs missed while the panel was down are marked `skipped`.

## Alternatives
- Quartz.NET / Hangfire: mature, but they add tables, concepts and dependencies we do not need.
- Scheduler in the agent: survives panel outages, but duplicates logic and complicates auditing and the UI.

## Consequences
- If the panel is down, tasks do not run (documented).
- Already safe for several instances if the panel is ever scaled out.

## Implementation notes (Phase 8)
- Cronos 0.13 (MIT) with its standard format: five fields, no seconds. Time zones are IANA ids only (Windows ids exist only on Windows, and the panel may run on Linux); the older names browsers use for some zones (`America/Buenos_Aires`, `Asia/Calcutta`) are IANA links and are accepted. Occurrences are stored in UTC. Cronos runs a time that does not exist on the day clocks go forward when the gap ends, and a time that happens twice on the day they go back once.
- Each pass locks up to 50 due tasks (`SELECT … FOR UPDATE SKIP LOCKED` in a transaction), records their runs, moves `next_run_at` past now and commits; the actions run after the commit, each in a scope of its own acting as the schedule. Then the loop sleeps until the next task is due, a task changes, or a minute passes (a safety net for clock changes and edits made by another panel).
- Missed runs: an occurrence up to a minute late still runs; older ones become one skipped run with the number of occurrences it stands for, rather than a row each (a task every minute and a day down would be 1,440 rows).
- A run that starts an operation (backup, update) records it; its outcome is read from the operation, so nothing has to keep a copy in sync. Operations do not point back to tasks.
- The last 100 runs of each task are kept.

