# ADR-0001 — Modular monolith with a single panel instance

Status: Proposed

## Context
Pulsar must be easy to install (even on a home PC) and maintainable by contributors. The requirements rule out microservices and unnecessary infrastructure (Redis, queues, Kubernetes).

## Decision
A single ASP.NET Core process (`Pulsar.Api`) with internal modules (Access, Nodes, Templates, Servers, Operations, Files, Backups, Scheduling, Audit, Realtime, Settings) organized as folders inside the Domain/Application/Infrastructure layers. One panel instance in v1. Volatile state (agent connections, live metrics, console subscriptions) lives in memory.

## Alternatives
- Microservices: operational complexity without benefit at this scale.
- One project per module: stronger boundaries, but multiplies project files and contributor friction; a module can be extracted later if it needs it.
- Multi-instance from day one: requires a SignalR backplane (Redis) and distributed state.

## Consequences
- Simple installation: one executable + PostgreSQL.
- No panel high availability. If it goes down, servers keep running, but there is no UI or scheduler.
- To scale out later: a SignalR backplane and moving in-memory state to a shared store. The scheduler already uses `FOR UPDATE SKIP LOCKED` and needs no change.
