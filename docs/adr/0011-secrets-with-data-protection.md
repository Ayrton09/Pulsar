# ADR-0011 — Secrets encrypted with ASP.NET Core Data Protection

Status: Proposed

## Context
Some secrets must be recoverable by the panel (GSLT, RCON passwords, the panel signing key), not just verifiable.

## Decision
Application-level encryption with Data Protection (separate purposes per secret type). Key ring persisted on the panel's disk, protected with machine DPAPI on Windows or with a certificate (Linux/portability). On nodes, specs containing secrets are encrypted with DPAPI LocalMachine. APIs treat secrets as write-only.

## Alternatives
- pgcrypto in the database: the key would travel to the database or live in the connection configuration.
- External Vault/KMS: infrastructure dependency not justified in v1 (can be added later as an alternative key ring repository/protector).

## Consequences
- A PostgreSQL backup without the key ring cannot decrypt secrets: the backup guide must include both.
- With machine DPAPI, moving the panel to another machine requires certificate protection.
