# ADR-0010 — Panel-side scheduler on PostgreSQL, without Quartz or Hangfire

Status: Proposed

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
