# Spike 0002 — Launching game servers as dedicated per-server accounts

Status: **ready to run** — needs a disposable environment (Windows Sandbox or a VM) because it creates local users and changes user rights. Code: [`spikes/IsolatedLaunch`](../../spikes/IsolatedLaunch); instructions in [spikes/README](../../spikes/README.md).

## Questions

1. Can SYSTEM in session 0 start a process with `LogonUser` (batch) + `CreateProcessAsUser` + ConPTY + `PROC_THREAD_ATTRIBUTE_JOB_LIST`?
2. Is `LoadUserProfile` needed for a console process to start and behave (environment, `%APPDATA%`)?
3. Does the child fail with `0xC0000142` (`STATUS_DLL_INIT_FAILED`) without a dedicated window station/desktop?
4. Is the process confined: it can read its own server folder but not read or write the other server's folder or `C:\Windows`?
5. What does the token really contain?

## Hypothesis to verify (affects ADR-0005 details)

The design says the account is "removed from the Users group". `BUILTIN\Users` contains `NT AUTHORITY\Authenticated Users` by default, so **the token probably still carries the Users SID** through that nesting, which would make the removal ineffective. The probe prints `whoami /all` in every scenario to settle this. If confirmed, isolation must rely on directory ACLs (already the main control), and the design text will be corrected rather than relying on group membership.

## Results

_Pending._
