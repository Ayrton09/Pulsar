# Contributing to Pulsar

Thanks for your interest in Pulsar! This document explains how to propose changes.

## Before you start

- For anything larger than a small fix, open an issue first so the approach can be discussed.
- Architectural changes need an [ADR](docs/adr/README.md). If you think an existing decision is wrong, open an issue describing the problem, its impact, the proposed solution, alternatives and trade-offs.
- Security issues must **not** be reported in public issues. See [SECURITY.md](SECURITY.md).

## Developer Certificate of Origin (DCO)

Every commit must be signed off, certifying that you wrote the change or have the right to submit it under the project's license ([developercertificate.org](https://developercertificate.org/)):

```bash
git commit -s -m "feat(agent): add Windows process manager"
```

This adds a `Signed-off-by: Your Name <you@example.com>` line. Pull requests with unsigned commits cannot be merged.

## Commit messages

We use [Conventional Commits](https://www.conventionalcommits.org/):

```
feat(agent): add Windows process manager
fix(files): prevent path traversal
refactor(auth): simplify permission evaluation
test(agent): add process cleanup tests
docs(adr): accept ADR-0005
```

Keep commits focused: one coherent change per commit, no unrelated features bundled together.

## Development setup

Requirements:

- .NET SDK 10.0.400 or newer (see `global.json`)
- Node.js 22 or newer
- Windows 10 1809+ / Windows 11 / Windows Server 2019+ to work on the agent's Windows code

PostgreSQL 15 or newer is required by the panel and by the database integration tests. Any installation works; for a throwaway local one without a Windows service, extract the [PostgreSQL binaries zip](https://www.enterprisedb.com/download-postgresql-binaries) and run:

```powershell
.\scripts\dev\postgres.ps1 -Action Init  -PgHome <extracted>\pgsql -Root <folder>   # once: creates the cluster and roles
.\scripts\dev\postgres.ps1 -Action Start -PgHome <extracted>\pgsql -Root <folder>   # after a reboot
```

`<folder>\connection.txt` holds the generated connection strings. The panel reads `ConnectionStrings:Pulsar` (use `dotnet user-secrets` in `src/Pulsar.Api`), and the integration tests read `PULSAR_TEST_POSTGRES` (a connection string without a database; each test run creates and drops its own databases). Without that variable the database tests are skipped, unless `PULSAR_REQUIRE_DATABASE=true` is set, as in CI.

The remote backup tests need an S3-compatible server: set `PULSAR_TEST_S3` to `endpoint;access key;secret key` (for
example a local SeaweedFS, `weed server -s3 -s3.config=<identities.json>`, which CI runs in Docker). Without it they are
skipped.

Backend:

```bash
dotnet restore
dotnet build
dotnet test
dotnet format --verify-no-changes
```

Frontend (`src/Pulsar.Web`):

```bash
npm ci
npm run lint
npm run typecheck
npm test
npm run build
npm run dev   # Vite dev server, proxies /api, /hubs and /agent to the panel
```

The frontend's API types are generated from the panel's OpenAPI document. After changing an endpoint or a DTO:

```powershell
$env:PULSAR_UPDATE_OPENAPI = 'true'; dotnet test --project tests/Pulsar.Api.Tests   # rewrites src/Pulsar.Web/src/api/openapi.json
cd src/Pulsar.Web; npm run api:generate                                            # regenerates src/api/schema.d.ts
```

CI fails when either file is stale.

End-to-end tests use Playwright (`npm run e2e`) against a published panel; set `PULSAR_E2E_POSTGRES` to a connection string for a disposable database (for example `pulsar_e2e`).

Running an agent against a local panel (Windows): add a node in the UI, then run its enrollment command in development mode, which needs no administrator rights and installs no service:

```powershell
dotnet build src/Pulsar.Agent
cd src/Pulsar.Agent/bin/Debug/net10.0-windows
.\PulsarAgent.exe enroll --panel http://localhost:5173 --token <token> --panel-key <fingerprint> --dev --data-dir <folder>
.\PulsarAgent.exe run --data-dir <folder>
```

`--dev` keeps the node key in your user's key store and lets you read the state folder; never use it on a real node. Plain HTTP is only accepted for a panel on the same machine. Delete the node in the UI and the folder when you are done.

## Code guidelines

- Nullable reference types are enabled and warnings are errors: keep the build clean.
- Use `async`/`await` correctly and pass `CancellationToken` where it applies.
- Use dependency injection and the options pattern; no service locator, no unnecessary static state.
- Never swallow exceptions, never use `Thread.Sleep`, avoid aggressive polling.
- Don't log secrets or whole request/response objects.
- No magic strings for critical states; use the types and constants that exist for them.
- Add tests with your change. Authorization, path handling, process lifecycle and agent communication changes always need tests.
- Avoid adding dependencies "just in case". Every new package must be justified in the pull request.

## Game manifests

Manifests in `games/` are licensed under MIT. Each manifest must validate against `schemas/manifest.v1.schema.json`; the test suite checks every manifest in the repository.

## Pull requests

- Fill in the pull request template.
- Make sure CI is green.
- Update documentation when behavior changes.
