# Spike 0003: SFTP server for Phase 17

Date: 2026-10-16. Code: `spikes/SftpSpike` (outside `Pulsar.slnx`). Server on 127.0.0.1 only.

## Question

Can the panel host the Phase 17 SFTP service (docs/architecture/09-roadmap.md, "Phase 17") with `Microsoft.DevTunnels.Ssh` and an SFTP v3 handler of our own? The service has to cover:

- panel user name and password;
- two-factor authentication by keyboard-interactive;
- `name` and `name.<shortId>` user names;
- the sftp subsystem only.

## Answer

Yes. DevTunnels.Ssh plus our own SFTP v3 handler works with:

- Windows OpenSSH (`sftp.exe` 9.5p2);
- OpenSSH 10.5p1;
- SSH.NET 2026.0.0.

The spike's handler is about 250 lines. No Go sidecar or third-party SFTP server is needed. The library has two defects; each has a simple workaround (see "Library gaps").

Packages:

- `Microsoft.DevTunnels.Ssh`, `.Keys` and `.Tcp` 3.12.42: MIT, no NuGet dependencies, netstandard2.1.
- `SSH.NET` 2026.0.0: tests only. It pulls BouncyCastle and `Microsoft.Extensions.Logging.Abstractions`.

## What was verified

### Host key

The host key is ECDSA P-256, saved as PKCS#8 (`KeyPair.ExportPrivateKeyFile`) and reloaded on restart, so its fingerprint stays the same across runs. Its `SHA256` fingerprint is the SHA-256 of the public key blob in unpadded base64, which matches what `sftp -v` prints.

### Sign-in

Only `password` and `keyboard-interactive` are offered. `none` and `publickey` are refused.

OpenSSH prefers keyboard-interactive when both are offered, so in practice everyone signs in through it:

1. The client sends `none`. The server answers that `password,keyboard-interactive` can continue.
2. Round 1 has a single prompt, `Password: ` with echo off. Unknown users get the same prompt, so user names cannot be enumerated.
3. For a user with two-factor authentication, round 2 has a single prompt, `Verification code: `. Any other user succeeds after round 1.
4. In server code this is `SshAuthenticationType.ClientInteractive`:
   - On the first call `InfoResponse` is null, so the handler sets `e.InfoRequest`. Later calls carry the response.
   - Per-session state, such as "password already verified", lives in a `ConditionalWeakTable` keyed by the session.
   - Setting `e.AuthenticationTask` to the principal means success. Leaving it null means failure.
5. The `password` method stays for clients that only know it, such as SSH.NET's `PasswordAuthenticationMethod`. For a user with two-factor authentication it always fails. Such a client can never sign that user in, which is the safe outcome.

Partial success (RFC 4252: password, then keyboard-interactive for the code) is not available. `SshAuthenticatingEventArgs` has no way to report it.

### User names and channels

- `bob.srv1` gets that server's folder as `/`.
- An unknown short id signs in, but the sftp subsystem request then fails.

Only `session` channels are accepted, and on them only `subsystem sftp`. Everything else was refused, and port forwarding is refused as long as `PortForwardingService` is not added to the configuration.

| Kind | Refused |
|---|---|
| OpenSSH commands | `ssh` (shell), `ssh -tt`, `ssh whoami` (exec), `-L`, `-R` |
| Channel requests (raw probe) | `x11-req`, `auth-agent-req@openssh.com`, `env`, `pty-req`, `shell`, `exec`, `window-change`, `break`, `xon-xoff`, any other subsystem |
| Global requests | `tcpip-forward`, `cancel-tcpip-forward`, `streamlocal-forward` |
| Channel opens | `direct-tcpip`, `x11`, `forwarded-tcpip`, `auth-agent@openssh.com`, `direct-streamlocal` |

A refused `env` is harmless: `sftp -o SendEnv=LANG` still works.

### SFTP v3

The handler implements:

- INIT/VERSION, REALPATH, STAT/LSTAT/FSTAT;
- OPENDIR/READDIR/CLOSE, OPEN/READ/WRITE/CLOSE;
- REMOVE, MKDIR, RMDIR;
- RENAME, which refuses an existing target;
- SETSTAT/FSETSTAT, accepted and ignored.

Anything else answers OP_UNSUPPORTED. It allows 32 handles per session and packets of up to 256 KiB, and `ls -l` long names render correctly in `sftp.exe`.

### Jail

These attempts are refused: `../../x`, `/../x`, `..\..\x`, `ls ../..`, an upload to `../../escaped.txt`, `/a:b` (an NTFS stream), backslashes, a trailing dot or space, and device names. `sftp.exe` reports the refused downloads as "not found", because its glob step swallows PERMISSION_DENIED.

The spike does not handle 8.3 short names or reparse points. In Phase 17 the agent's path rules stay the authority.

### Throughput

Loopback, 50 MB, including connect and sign-in. The SHA-256 of the round trip matched.

| Client | Upload | Download |
|---|---|---|
| `sftp.exe` | ~104 MB/s | ~87 MB/s |
| SSH.NET | 105–110 MB/s | ~80 MB/s |

Neither the library nor a synchronous handler is a bottleneck.

### Algorithms

| Client | Key exchange | Host key | Cipher and MAC |
|---|---|---|---|
| `sftp.exe` 9.5p2 | ecdh-sha2-nistp256 | ecdsa-sha2-nistp256 | aes256-ctr, hmac-sha2-256-etm@openssh.com |
| OpenSSH 10.5p1 | ecdh-sha2-nistp256 | ecdsa-sha2-nistp256 | aes256-gcm@openssh.com |
| SSH.NET | ecdh-sha2-nistp256 | ecdsa-sha2-nistp256 | aes256-ctr, hmac-sha2-256 |

Restricting the server's offer to GCM only, group16 only or nistp384 only still worked with `sftp.exe`. CBC alone fails. Phase 17 should drop `aes256-cbc` and the non-ETM MACs from the offer.

The library has no curve25519, ed25519, chacha20-poly1305 or post-quantum key exchange. Interop does not need them, but OpenSSH 10.1 and later prints a warning that the connection does not use post-quantum key exchange. The user guide should mention it.

## Library gaps

1. **No SFTP and no `subsystem` request type.** Both are expected. A 10-line `ChannelRequestMessage` subclass adds the request: `e.Request.ConvertTo<SubsystemRequestMessage>()`.
2. **Two prompts in one keyboard-interactive request break OpenSSH and SSH.NET** ("incomplete message", "Index was out of range"). Use one prompt per round, as above.
3. **No partial success.**
4. **Refusing a channel open makes OpenSSH drop the whole connection.** That is fine for forwarding attempts. Keep it in mind before refusing a `session` channel because of a limit.
5. **A malformed request of a type the library knows**, such as `signal` with an empty payload, closes the session with a protocol error. This is harmless.
6. **Authentication is enforced by the library.** `e.Principal` in the channel handler carries what the authentication handler returned.
7. **Limits are ours to write.** The library offers none of these:
   - sessions per user and per address;
   - handles per session (done in the spike);
   - an idle timeout (the library only has keep-alive);
   - upload size.

   Lockout must count each password check, not each connection.
8. **Create the `SshStream` inside the channel request handler** before answering success.
9. **Library tracing above Critical is noisy.** It logs a stack trace for every dropped connection.

## Test notes

- `sftp.exe -b` forces `BatchMode=yes`, which disables password prompts. Pass `-o BatchMode=no`.
- `SSH_ASKPASS=<file>.cmd` with `SSH_ASKPASS_REQUIRE=force` works on Windows.
- Prefix batch lines with `-` to keep going after an expected failure.
- SSH.NET's `SshClient.CreateCommand(...).Execute()` hangs when exec is refused, so tests need timeouts.

## Not verified

- WinSCP and FileZilla. Both support keyboard-interactive; the user tests them later.
- Transfers of 2 GB and resume.
- A root that lists several servers.
- Load and limits.
- The real path through the panel and the agent.
- The maintenance status of the GitHub project.
