# Spike 0001 — Console capture: ConPTY vs pipes (and what srcds actually needs)

Status: **done for SteamCMD and Counter-Strike: Source.** CS2 and Garry's Mod not tested yet (CS2 is a different engine; GMod is srcds-based and expected to behave like CS:S).

Environment: Windows 11 Pro 25H2 (build 26200), .NET 10.0.12, CsWin32 0.3.335, SteamCMD version 1788292693, CS:S dedicated server (app 232330, server version 11003710). Tools: [`spikes/ConsoleCapture`](../../spikes/ConsoleCapture) (capture), [`spikes/ConsoleProbe`](../../spikes/ConsoleProbe) (fake server), `rcon_probe.py` (minimal Source RCON client). Raw captures were kept locally under `artifacts/spike1` (not committed).

## Summary

| Process | Pipes | ConPTY | Works with |
|---|---|---|---|
| Fake server (stdout + `CONOUT$`) | `CONOUT$` output lost | Everything captured | **ConPTY** |
| `steamcmd.exe` | Output block-buffered: progress arrives all at once at the end | Live output | **ConPTY** |
| `srcds.exe -console` (CS:S) | Live stdout, then **fatal error** after ~5 s (needs console input) | **0 bytes**: srcds allocates its own console | **Neither → log file + RCON** |

## Findings

### F1. Job Objects work as designed (confirms ADR-0006)

- Processes created with `PROC_THREAD_ATTRIBUTE_JOB_LIST` + `CREATE_SUSPENDED` are inside the Job before running (`IsProcessInJob = true`).
- Terminating the Job killed the whole tree every time: the fake server's grandchild, and srcds together with the `conhost.exe` it creates. No process was ever left behind.
- Job accounting (total/active processes, CPU time) and `JobObjectBasicProcessIdList` work as expected.

### F2. Pipes lose console-API output

With redirected pipes (`CREATE_NO_WINDOW`), stdout, stderr and stdin work, but output written directly to the console buffer (`CONOUT$`) is lost: the process gets a hidden console of its own.

### F3. ConPTY needs a caller without standard handles

A child attached to a ConPTY **inherits the caller's standard handles** if the caller has them: stdout bypassed the pseudo console and stdin was closed. When the caller has **no** standard handles — which is what a Windows service has, reproduced with `SetStdHandle(…, NULL)` (`--detach-std`) — the child gets the pseudo console's handles and everything works: stdout, stderr, `CONOUT$`, input, UTF-8 and Ctrl+C. Setting `STARTF_USESTDHANDLES` with null handles also worked for the fake server.

**Design consequence:** the agent service has no standard handles; when it runs from a console for development it must clear them (or pass explicit handles) before creating ConPTY children.

### F4. ConPTY output details

- UTF-8 VT stream. Characters the application cannot represent in its own code page are lost by the application before the ConPTY sees them (`€` → `?` with CP850).
- conhost re-renders colors (`ESC[32m … ESC[m`), emits mode sequences (`ESC[?9001h ESC[?1004h ESC[?25l ESC[2J ESC[H`) and window titles (`ESC]0;…BEL`). xterm.js handles them; plain-text logs must strip VT sequences.
- **Input is echoed** by the console in cooked mode: the UI must not echo commands a second time.
- `ClosePseudoConsole` took 0.5–30 ms after the process exited; no blocking on this build.

### F5. SteamCMD

- `steamcmd.exe` has a **valid Authenticode signature** (`CN=Valve, O=Valve, S=Washington, C=US`): the agent can verify it after downloading.
- First run self-updates (≈30 MB + ≈43 MB) and **exits with code 7**; the next run is the real one (exit 0). The agent must treat the bootstrap as its own step.
- Messages are **localized to the OS language** for part of the run ("Buscando actualizaciones…", "Verificando instalación…"), then English. Parsers must rely on the language-neutral lines (`Update state (0x…)`, `Success!`/`ERROR!`) and on exit codes, not on translated text.
- **Pipes are block-buffered:** in a 140 s install/validate run, all progress from 1 s to 112 s arrived in a single 4 KB block at 112 s and the rest at exit. **ConPTY delivers output live.** → SteamCMD runs under ConPTY.
- Progress format: ` Update state (0x61) downloading, progress: 9.44 (238319757 / 2524405728)`; `0x81` = verifying, `0x3` = reconfiguring. Bytes downloaded/total are available, so the UI can show real progress.
- The first `app_update 232330` **failed after 14 s** with `ERROR! Failed to install app '232330' (Missing configuration)`, exit code 8. The immediate retry succeeded. → bounded retries with backoff are required, not optional.
- CS:S dedicated server: ≈2.52 GB.

### F6. srcds (CS:S) needs log file + RCON, not a console stream

- **Pipes:** stdout is live (first lines after 0.1 s), but after ~5 s srcds dies with `FATAL ERROR: CTextConsoleWin32::GetLine: !GetNumberOfConsoleInputEvents` (exit code 100): with `-console` it requires its standard input to be a real console.
- **ConPTY:** srcds produces **nothing** on the pseudo console and ignores input. A job snapshot shows a second process, `conhost.exe`, and srcds owning a console window titled "Counter-Strike: Source": srcds **allocates its own console** and stops using the one it was given. This happened with every variant tried (null standard handles, caller without standard handles).
- **What works:**
  - **Output:** `-condebug` makes srcds write its console to `cstrike/console.log`, flushed live (boot lines appeared while starting). The agent tails this file.
  - **Commands:** the **Source RCON protocol** (TCP, game port, `rcon_password`) authenticated and answered `status`, `echo` and cvar queries in **12–28 ms**.
  - **Graceful stop:** `quit` over RCON → srcds exited with **code 0** in about 1 s, together with its own `conhost.exe`.
  - **Caveat:** commands executed over RCON (including `say`) are answered over RCON but **not** written to `console.log`. The console UI shows RCON responses inline next to the log stream.
- srcds still needs *a* console to avoid the fatal input error, so it is started under a ConPTY (or any console); in a service (session 0) its own console window is invisible.

## Design consequences

1. **Console modes become three** (doc 04, ADR-0007):
   - `pty` — ConPTY stream (SteamCMD, console applications that use the console normally);
   - `pipes` — redirected stdio (e.g. Java/Minecraft);
   - `logfile+rcon` — output by tailing a log file declared by the manifest, input and graceful stop through RCON (Source engine servers on Windows).
2. **RCON is managed by the agent.** It generates a random RCON password per server (secret, never shown by default), passes it at launch and keeps the RCON session. It is a credential exposed on the game's TCP port, so it must be long and random, and Source's RCON ban settings (`sv_rcon_maxfailures`, `sv_rcon_banpenalty`, …) should be set by the manifest. Added to the threat model.
3. **Log tailing** must handle the file being truncated/recreated at start, partial lines, and unbounded growth (`-condebug` appends forever: the agent rotates it before each start).
4. **Readiness for Source**: a log pattern (`Server is hibernating`, or the first successful RCON `status`).
5. SteamCMD: ConPTY, bootstrap step, language-neutral parsing, retries (F5).

## Not covered

- **CS2** (Source 2): not installed (≈60 GB). It may or may not behave like srcds; it needs its own run before the CS2 manifest is written.
- **Garry's Mod**: expected to behave like CS:S (same srcds launcher); to confirm when its manifest is written.
- Running under a dedicated per-server account in session 0: spike 0002.
