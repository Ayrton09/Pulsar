# ADR-0013 — AGPL-3.0 for the core; MIT for manifests and schemas; DCO

Status: Accepted (2026-10-01)

## Context
The core is AGPL-3.0. Manifests are data that communities and hosting companies will want to create, adapt and sometimes keep private.

## Decision
- `src/`, `tests/`, `docs/`: `AGPL-3.0-only`.
- `games/` and `schemas/`: MIT, so manifest contributions are not discouraged and private manifests raise no licensing doubts.
- Contributions under the Developer Certificate of Origin (`Signed-off-by`), no CLA.

## Alternatives
- AGPL for everything: simpler, but adds friction for manifest authors.
- AGPL + CLA: allows relicensing later, at the cost of contributor friction.

## Consequences
- Relicensing later would require every contributor's consent: that is why it is decided before the first external pull request.
- `-only` vs `-or-later` can still be changed before external contributions arrive.
