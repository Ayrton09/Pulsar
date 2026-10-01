# Spike 0001 — Console capture: ConPTY vs pipes

Status: **in progress** — tool validated against a fake server; real game servers pending (needs SteamCMD downloads).

Environment: Windows 11 Pro 25H2 (build 26200), .NET 10.0.12, CsWin32 0.3.335. Tool: [`spikes/ConsoleCapture`](../../spikes/ConsoleCapture), fake server: [`spikes/ConsoleProbe`](../../spikes/ConsoleProbe).

## Findings so far (fake server)

### F1. Job Objects work as designed

- Processes created with `PROC_THREAD_ATTRIBUTE_JOB_LIST` + `CREATE_SUSPENDED` are inside the Job before running (`IsProcessInJob = true`).
- Terminating the Job killed the process **and the grandchild it spawned**; no `ConsoleProbe` process remained in any run.
- Job accounting reports total/active processes and CPU time as expected (`TotalProcesses = 3` with a grandchild).

### F2. Pipes lose output written through the console API

With redirected pipes (`CREATE_NO_WINDOW`), stdout, stderr and stdin work, but a line written directly to the console buffer (`CONOUT$`) **is lost**: the process gets a hidden console of its own. Any game server that writes through the console API (the suspected srcds behavior) cannot be captured with pipes.

### F3. ConPTY: the child must not inherit the caller's standard handles

Without `STARTF_USESTDHANDLES`, a child attached to a ConPTY **inherited the caller's redirected stdin/stdout/stderr**: stdout bypassed the pseudo console and stdin was closed immediately. Only `CONOUT$` output went through the ConPTY.

With `STARTF_USESTDHANDLES` and **null** standard handles (`--usestdhandles`), the child used the pseudo console for everything: stdout, stderr, `CONOUT$`, input and Ctrl+C.

**Design consequence:** in `pty` mode the agent always sets `STARTF_USESTDHANDLES` with null handles. The agent runs as a service and normally has no standard handles, but it must not depend on that (and it does have them when started from a console during development).

### F4. ConPTY output details

- The output is UTF-8 VT: `ñ á ü` arrive as valid UTF-8. Characters the application cannot represent in its own code page are lost by the application (`€` → `?` with CP850) before the ConPTY sees them.
- ANSI colors are re-rendered by conhost (`ESC[32m … ESC[m`), and the stream starts with mode sequences (`ESC[?9001h ESC[?1004h`, `ESC[?25l ESC[2J ESC[H`) and window title changes (`ESC]0;…BEL`). xterm.js handles all of these; anything storing plain-text logs must strip VT sequences.
- **Input is echoed** by the console in cooked mode: a command sent by the agent appears in the output stream before the game's response. The console UI must not echo commands a second time.
- Writing `0x03` delivered Ctrl+C: the probe's pending `ReadLine` was interrupted. Whether real servers shut down cleanly on Ctrl+C is pending.
- `ClosePseudoConsole` took 3–30 ms after the process exited (no blocking on this build).

## Pending (real servers)

Run with `spikes/ConsoleCapture/run-spike1-games.ps1` (see [spikes/README](../../spikes/README.md)):

- [ ] SteamCMD: bootstrap behavior, progress line format, exit codes, output buffering in pipes vs pty, Authenticode signature.
- [ ] `srcds.exe` (CS:S, GMod): which mode captures the console, colors, commands, `quit`, Ctrl+C, readiness lines.
- [ ] `cs2.exe`: same questions (≈60 GB download, separate step).
