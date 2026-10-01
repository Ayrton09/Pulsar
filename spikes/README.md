# Spikes (throwaway prototypes)

**Not product code.** These prototypes answer the Phase 0 questions of the [roadmap](../docs/architecture/09-roadmap.md) before the agent is built on assumptions. They are not part of `Pulsar.slnx`, are not covered by CI, and will be deleted once Phase 3 has replaced them with tested code. Findings are recorded in [`docs/spikes`](../docs/spikes).

| Spike | Folder | Question |
|---|---|---|
| 1 | `ConsoleCapture/`, `ConsoleProbe/` | How do `steamcmd.exe`, `srcds.exe` (CS:S, GMod) and `cs2.exe` behave under ConPTY vs redirected pipes? |
| 2 | `IsolatedLaunch/` | Can SYSTEM (session 0) launch a process as a dedicated per-server account, inside a Job, with a ConPTY, and is it isolated from other servers? |

## Spike 1 — console capture

`ConsoleCapture` launches one process inside a Job Object (`KILL_ON_JOB_CLOSE`, created through `PROC_THREAD_ATTRIBUTE_JOB_LIST` + `CREATE_SUSPENDED`) with its console attached to a ConPTY (`--mode pty`) or to redirected pipes (`--mode pipes`). It records every output chunk with timestamps and raw bytes to a JSONL file, can send input lines and Ctrl+C at given times, and prints a summary (first output, bytes, exit code, job process counts).

`ConsoleProbe` is a fake game server: it writes to stdout, stderr and directly to the console buffer (`CONOUT$`, like srcds), emits ANSI colors, echoes commands and can spawn a grandchild.

```powershell
dotnet build spikes/ConsoleCapture -c Release
dotnet build spikes/ConsoleProbe -c Release
spikes/ConsoleCapture/bin/Release/net10.0-windows/ConsoleCapture.exe --mode pty --usestdhandles `
    --exe spikes/ConsoleProbe/bin/Release/net10.0-windows/ConsoleProbe.exe --duration 6 --send 1:hello --send 2:spawn --ctrlc 4
```

Other options: `--detach-std` clears the tool's own standard handles before launching (what a Windows service looks like), `--snapshot <sec>` records the processes inside the Job. `rcon_probe.py` is a minimal Source RCON client used to validate the `logfile+rcon` console mode.

Real game servers: `run-spike1-games.ps1 -Root <folder> [-Games css,gmod]` downloads SteamCMD from Valve, installs CS:S and GMod anonymously (a few GB; `-IncludeCs2` adds ~60 GB) and captures them in both modes. Servers bind to `127.0.0.1` with `sv_lan 1`.

## Spike 2 — isolated launch

**Creates local users and changes user rights.** Run it only in a disposable environment.

1. On the host: `spikes/IsolatedLaunch/prepare-bundle.ps1` publishes a self-contained bundle to `artifacts/spike2-bundle` together with `spike2.wsb`.
2. Open `spike2.wsb` (requires the *Windows Sandbox* optional feature), or copy the bundle into a VM and run `run-spike2.ps1 -Bundle <bundle>` elevated.
3. The script runs `IsolatedLaunch` as SYSTEM through a temporary scheduled task (session 0, like the agent service), then writes `report/spike2.log` and one capture per scenario:
   - `s1-token-only`: `LogonUser` (batch) + `CreateProcessAsUser`, inherited desktop;
   - `s2-profile-and-environment`: + `LoadUserProfile` + `CreateEnvironmentBlock`;
   - `s3-profile-environment-desktop`: + a dedicated window station/desktop.

   In each scenario the probe prints its token (`whoami /all`), its environment, and tries to read its own file, read and write the other server's folder, and write into `C:\Windows`.
