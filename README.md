# Pulsar

Pulsar is an open source web panel for managing game servers, designed with **Windows as a first-class platform** (Windows 10/11 and Windows Server).

> **Status: pre-alpha, in design.** Nothing is usable yet. The architecture is being reviewed in [`docs/architecture`](docs/architecture/README.md) and the key decisions are recorded as [ADRs](docs/adr/README.md).

## What Pulsar aims to do

- Create, install (via SteamCMD), start, stop, restart and update game servers from a web UI.
- Real-time console, CPU/RAM/uptime metrics, file manager, backups and scheduled tasks.
- Multiple nodes: each machine runs a lightweight **Pulsar Agent** (a Windows service) that connects *outbound* to the panel, so nodes don't need to open ports for Pulsar.
- Fine-grained role-based access control and audit logs.
- Game support defined by declarative **manifests**, so new games can be added without changing the core.

Initial games: Counter-Strike 2, Counter-Strike: Source and Garry's Mod.

## Architecture in short

| Component | Technology |
|---|---|
| Panel | ASP.NET Core (.NET 10), modular monolith, SignalR, OpenAPI |
| Database | PostgreSQL (panel data only) |
| Frontend | React + TypeScript + Vite, served by the panel |
| Agent | .NET 10 Windows service using Job Objects and ConPTY |

## Repository layout

```
docs/      architecture, ADRs and guides
games/     official game manifests (MIT licensed)
schemas/   public JSON schemas (MIT licensed)
src/       panel, agent and frontend source
tests/     automated tests
```

## Building

Requirements: .NET SDK 10.0.400+ and Node.js 22+.

```bash
dotnet build
dotnet test
```

The frontend lives in `src/Pulsar.Web`; see [CONTRIBUTING.md](CONTRIBUTING.md) for the full development workflow.

## Contributing

Contributions are welcome once the architecture settles. Please read [CONTRIBUTING.md](CONTRIBUTING.md) and the [Code of Conduct](CODE_OF_CONDUCT.md). Report security issues privately as described in [SECURITY.md](SECURITY.md).

## License

Pulsar is licensed under the [GNU Affero General Public License v3.0](LICENSE) (`AGPL-3.0-only`).
Game manifests in [`games/`](games/) and schemas in [`schemas/`](schemas/) are licensed under the MIT License.
