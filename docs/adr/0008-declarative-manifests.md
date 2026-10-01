# ADR-0008 — Declarative manifests with closed steps and version pinning

Status: Proposed

## Context
The community must be able to add games without touching the core, but a manifest decides what gets downloaded and executed on nodes.

## Decision
- JSON manifest with `schemaVersion`, `key` and `version` (semver), validated with a JSON Schema and semantic rules by the shared `Pulsar.Manifests` library in both panel and agent.
- Closed set of steps (`steamcmd`, `ensureDirectory`, `writeFileIfMissing`; `download` with mandatory sha256 and `extractArchive` post-v1). No shell or scripts.
- Logic-free templates: only placeholders and one conditional; every element is exactly one argv.
- Typed variables; strings used in arguments or configuration files require a pattern or an enum.
- Servers are pinned to `key@version`; updating a template is an explicit action.
- Importing manifests requires `template.manage`.

## Alternatives
- Scripts (PowerShell/Lua) in manifests: maximum flexibility, but arbitrary execution.
- Games as C# code (plugins): requires compiling and couples the community to the core.

## Consequences
- Some exotic games will need new step types, discussed and reviewed in the core.
- Format v1 is a public contract: incompatible changes mean `schemaVersion` 2 while keeping support for 1.
