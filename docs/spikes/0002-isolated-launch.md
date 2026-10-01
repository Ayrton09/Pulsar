# Spike 0002 — Launching game servers as dedicated per-server accounts

Status: **done** for a console process (the fake server). A real game server (`srcds.exe`, which creates its own console window) has not been launched this way yet.

Environment: Windows Sandbox on a Windows 11 Pro 25H2 host; guest Windows 11 Enterprise 24H2 (build 26100, Spanish locale), no networking. .NET 10 self-contained, CsWin32 0.3.335. Code: [`spikes/IsolatedLaunch`](../../spikes/IsolatedLaunch) (reuses the launcher of spike 1); instructions in [spikes/README](../../spikes/README.md). `run-spike2.ps1` runs the tool as **SYSTEM in session 0** through a temporary scheduled task, which is what the agent service looks like. Two runs; raw captures kept locally under `artifacts/spike2-run1` and `artifacts/spike2-bundle/report` (not committed).

## Questions

1. Can SYSTEM in session 0 start a process with `LogonUser` (batch) + `CreateProcessAsUser` + ConPTY + `PROC_THREAD_ATTRIBUTE_JOB_LIST`?
2. Is `LoadUserProfile` needed for a console process to start and behave (environment, `%APPDATA%`)?
3. Does the child fail with `0xC0000142` (`STATUS_DLL_INIT_FAILED`) without a dedicated window station/desktop?
4. Is the process confined: it can read its own server folder but not read or write the other server's folder or `C:\Windows`?
5. What does the token really contain?

## Setup

Two accounts `pulsar_spk_a` and `pulsar_spk_b` created with `NetUserAdd` (`USER_PRIV_USER`), `SeBatchLogonRight` granted and interactive/RDP/network logon denied through LSA. `C:\PulsarSpike2\servers` with inheritance disabled (SYSTEM + Administrators), one subfolder per server with Modify for its account. The fake server runs a fixed script: `whoami /all`, print environment, read its own file, read and write the other server's folder, write into `C:\Windows`, `C:\ProgramData` and `C:\Users\Public`, spawn a grandchild, `quit`.

| Scenario | Account | `LoadUserProfile` + `CreateEnvironmentBlock` | Restricted token | Desktop |
|---|---|---|---|---|
| s1-token-only | a | no | no | inherited (SYSTEM's, session 0) |
| s2-profile-and-environment | a | yes | no | inherited |
| s3-profile-environment-desktop | a | yes | no | dedicated window station + desktop with a DACL for the account |
| s4-restricted-token | a | yes | `DISABLE_MAX_PRIVILEGE` | inherited |
| s5-other-server-shared-locations | b | yes | `DISABLE_MAX_PRIVILEGE` | inherited |

## Results

All five scenarios started, ran the whole script and exited with code 0. Every run cleaned up completely: no accounts, profiles, folders, shared files or processes left behind.

### R1. The launch sequence works from session 0 (question 1: yes)

`LogonUser(LOGON32_LOGON_BATCH)` → `CreateProcessAsUser` with `PROC_THREAD_ATTRIBUTE_JOB_LIST` + `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE` + `CREATE_SUSPENDED` works as SYSTEM. The child is in the Job before running, its stdout, stderr, `CONOUT$` output and input go through the ConPTY, and terminating the Job killed the grandchild that was still alive after `quit` (`jobTotalProcesses=3`, `activeAtEnd=0`). First output arrived after ~15 ms (220 ms on the very first, cold launch).

### R2. `LoadUserProfile` is required (question 2)

Without it (s1) the process starts, but inherits SYSTEM's environment: `USERPROFILE=C:\Windows\system32\config\systemprofile`, `APPDATA` under it, `TEMP=C:\Windows\TEMP`, `USERNAME=<machine>$`. A game writing its settings or caches there would fail or, worse, mix data of every server. With `LoadUserProfile` + `CreateEnvironmentBlock` (s2) the environment is the account's own (`C:\Users\pulsar_spk_a`, its own `TEMP`). The first `LoadUserProfile` of an account creates the profile (~400 ms); later ones take ~15 ms. `DeleteProfile` must run when the account is deleted.

### R3. No `0xC0000142` for a console process (question 3: not reproduced)

s1, s2 and s4 used the desktop inherited from the SYSTEM task in session 0 (no access was granted to the account) and the fake server started normally. The dedicated window station/desktop (s3) also works but made no difference here. The failure is documented for processes that initialize user32 (create windows); `srcds.exe` does create a console window (spike 0001), so **the dedicated desktop stays in the design until srcds is launched this way**.

### R4. Confinement works through directory ACLs (question 4: yes)

In every scenario the process read its own file and got `Access denied` reading or writing the other server's folder and writing into `C:\Windows`.

### R5. The token still contains `BUILTIN\Users` (question 5; hypothesis confirmed)

`NetUserAdd` does not make the account a direct member of Users (`NetLocalGroupDelMembers` returns `1377`, `ERROR_MEMBER_NOT_IN_ALIAS`), yet `whoami /groups` shows `BUILTIN\Users` (`S-1-5-32-545`) enabled, because Users contains `NT AUTHORITY\Authenticated Users`. Other groups: Everyone, BATCH, CONSOLE LOGON, Authenticated Users, This Organization, Local account, LOCAL, NTLM Authentication; Medium integrity level. **"Removing the account from Users" has no effect and is dropped from the design.** Disabling the Users SID in a restricted token is not an option either: system folders such as `C:\Windows` grant read/execute to Users, not to Authenticated Users, so the game could not load system DLLs (not tested; follows from the default ACLs).

### R6. The account holds `SeShutdownPrivilege` — removed with a restricted token

On client editions the default "Shut down the system" right includes Users, so the server's token has `SeShutdownPrivilege` (disabled, but any process can enable its own privileges with `AdjustTokenPrivileges`). Untrusted plugin code could shut the node down; the Job's `JOB_OBJECT_UILIMIT_EXITWINDOWS` only blocks `ExitWindowsEx`, not `InitiateSystemShutdownEx`/`NtShutdownSystem`. Tokens also had `SeUndockPrivilege`, `SeIncreaseWorkingSetPrivilege` and `SeTimeZonePrivilege`.

`CreateRestrictedToken(DISABLE_MAX_PRIVILEGE)` (s4, s5) leaves only `SeChangeNotifyPrivilege`, and the process ran exactly as before. The profile is loaded with the full token; only the child gets the restricted one.

### R7. Locations writable by Users are shared between servers (residual risk)

Server a could create files in `C:\ProgramData` and `C:\Users\Public` (s1–s4). Server b (s5) **could read them**, could not overwrite them (the creator owns them), and could not read a file in server a's profile (`%TEMP%`). So a server's profile is private, but anything a game or plugin writes into Users-writable system locations is readable by every other server — and by any other local user, as on any Windows machine. Mitigations: point `TEMP`/`TMP` into the server directory (already in the design), keep game data under the server directory, document the limitation. Tightening the system ACLs of `C:\ProgramData` is out of scope (changes the administrator's machine); AppContainer is the long-term answer (post-v1).

### Not answered

- `srcds.exe` (and `cs2.exe`) launched as a dedicated account: window/desktop requirements and whether `-condebug` + RCON behave the same. To be checked in Phase 3 integration tests (the CS:S install from spike 0001 can be copied into a sandbox).
- S4U logon (no stored password) was not evaluated; `LogonUser` with a stored password is kept.

## Decisions and changes

- **ADR-0005**: decision kept; "outside the Users group" replaced by the restricted token and the reliance on ACLs.
- **Doc 06 start sequence**: `LoadUserProfile` + `CreateEnvironmentBlock` are mandatory; `CreateRestrictedToken(DISABLE_MAX_PRIVILEGE)` is added; the dedicated desktop stays marked `[spike]` until a real game is launched this way.
- **Doc 08**: node controls corrected; residual risk R7 recorded.
