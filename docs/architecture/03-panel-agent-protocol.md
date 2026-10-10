# 03 — Panel ↔ Agent protocol and node authentication

> Status: **proposal under review** · Decisions: ADR-0002, ADR-0003.

## Protocol design

### Transport: comparison

| Option | Pros | Cons |
|---|---|---|
| **SignalR (WebSockets) opened by the agent** | Same stack as the browser; automatic reconnect; typed hubs (`Hub<T>`); *client results* (.NET 7+) for acks; the .NET client sends `Authorization` as a header (not in the query string); WebSockets pass through every reverse proxy (IIS/ARR, nginx, Caddy, Cloudflare). | The contract is method names + DTOs (no IDL); versioning needs discipline. |
| gRPC bidirectional streaming | Protobuf IDL, excellent schema evolution. | Needs HTTP/2 end to end: poor support behind IIS/ARR and common Windows proxies; extra protobuf toolchain. |
| Raw WebSocket with custom framing | Full control. | Reimplementing reconnect, multiplexing, keep-alive and acks. More room for bugs. |

**Recommendation: SignalR over WebSockets** (transport pinned to WebSockets, no long-polling fallback). Typing is guaranteed in C# with shared contracts in `Pulsar.Contracts` and — key for security — **commands travel in signed envelopes**, so integrity does not depend on the transport.

Hub protocol: JSON (System.Text.Json with source generators). MessagePack only if console measurements justify it.

### Panel endpoints for agents

| Endpoint | Purpose | Auth |
|---|---|---|
| `GET  /agent/v1/panel-identity` | Panel public key(s) + fingerprint. | Public |
| `POST /agent/v1/enroll` | Enrollment with a single-use token. | Enrollment token |
| `POST /agent/v1/auth/challenge` | Requests a nonce. | None (rate limited) |
| `POST /agent/v1/auth/token` | Returns a session JWT (15 min) after verifying the signature. | Node signature |
| `POST /agent/v1/credentials/rotate` | Node key rotation. | Session token + proof with the new key |
| `WSS  /agent/v1/hub` | Persistent channel. | JWT (`aud=pulsar-agent`) |
| `GET/PUT /agent/v1/transfers/{ticket}` | Large transfers (uploads/downloads/backups). | JWT + single-use ticket |

All of these live outside `/api/v1` with a different authentication policy: **a browser credential never works on agent endpoints and vice versa**. `HttpConnectionDispatcherOptions.CloseOnAuthenticationExpiration = true` on the agent hub forces periodic re-authentication.

### Signed command envelope

The panel holds an ECDSA P-256 signing key (`panel_signing_keys`). Every panel → agent command:

```csharp
// Pulsar.Contracts
public sealed record SignedCommand(
    string KeyId,            // panel key used
    byte[] Payload,          // UTF-8 JSON of CommandEnvelope (exact signed bytes)
    byte[] Signature);       // ECDSA P-256 / SHA-256 over Payload

public sealed record CommandEnvelope(
    int ProtocolVersion,
    Guid CommandId,          // UUIDv7, idempotency
    Guid NodeId,             // recipient: the agent rejects it if it is not itself
    Guid SessionId,          // session issued by /auth/token
    long Sequence,           // monotonic per session (anti-replay)
    DateTime IssuedAtUtc,
    DateTime ExpiresAtUtc,   // secondary defense; tolerates configured clock skew
    Guid? OperationId,
    Guid? ActorUserId,       // for node-side logs/correlation
    AgentCommand Command);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(StartServerCommand), "server.start")]
[JsonDerivedType(typeof(StopServerCommand), "server.stop")]
// …one stable discriminator per command
public abstract record AgentCommand;
```

Verification in the agent (`CommandDispatcher`), in this order, rejecting on the first failure:

1. `KeyId` is a panel key pinned at enrollment.
2. Valid signature over `Payload` (verified **before** deserializing the command).
3. `NodeId` == self; `SessionId` == current session.
4. `Sequence` not seen and inside the anti-replay window (sliding window of 1024, as in IPsec; tolerates minor reordering without accepting repeats).
5. Not expired (with clock tolerance).
6. Compatible `ProtocolVersion`; known discriminator (otherwise `UnsupportedCommand`).
7. Semantic validation of the command (known IDs, paths, variables against the manifest, sizes).

So even if TLS or a reverse proxy is compromised, **commands cannot be injected or replayed**. ECDSA P-256 signing costs microseconds; command volume is low.

Agent → panel events are not signed individually: they travel through a session authenticated by proof of possession of the node key and their impact is informational (state, console, metrics). The panel still checks that every event's `ServerId` belongs to that node.

### Semantics: command → ack → events

- The panel invokes `IAgentClient.ExecuteAsync(SignedCommand)` with *client results* and waits for a `CommandAck` (10 s timeout). The ack only says **accepted/rejected**, not "finished".
- Long work is reported through events carrying an `OperationId`.
- Commands are **idempotent by `CommandId`** and by state (e.g. `Start` on a server that is already `Running` → `Accepted` with `AlreadyInState`).

```csharp
public sealed record CommandAck(Guid CommandId, AckStatus Status, string? ErrorCode, string? Message);
public enum AckStatus { Accepted, AlreadyInState, Rejected, Busy, UnsupportedCommand, InvalidSignature }
```

### v1 message catalog

**Panel → agent (commands)**

| Command | Main content |
|---|---|
| `ProvisionServerCommand` | ServerId, ShortId, relative directory, `ServerRuntimeSpec`. Creates the folder, Windows account and ACLs. |
| `ApplyServerSpecCommand` | New `ServerRuntimeSpec` (variables, ports, arguments, restart policy) for the next start. Same manifest and short id only. The panel sends it before every start and restart. |
| `InstallServerCommand` / `UpdateServerCommand` | ServerId (+ `Validate` for updates); the envelope carries the `OperationId`. |
| `StartServerCommand` / `StopServerCommand` / `RestartServerCommand` / `KillServerCommand` | ServerId (+ optional stop timeout). |
| `SendConsoleInputCommand` | ServerId, `Line` (≤ 512 characters, no control characters). |
| `SubscribeConsoleCommand` / `UnsubscribeConsoleCommand` | ServerId, `FromSequence?`: output after it (everything the agent holds when null), then new output. Repeating it moves the subscription back; the panel drops what it already has. |
| `DeleteServerCommand` | ServerId. Its backups go with it (Phase 7: the panel drops their rows too, so archives kept on the node would be orphans). |
| `ListDirectoryCommand`, `ReadFileCommand`, `WriteFileCommand`, `CreateDirectoryCommand`, `RenamePathCommand`, `DeletePathsCommand` | ServerId + **relative path** + size limits (text ≤ 1 MiB inline). |
| `PrepareTransferCommand` | Ticket, direction (upload/download), relative path, maximum size. |
| `CreateBackupCommand` / `RestoreBackupCommand` | BackupId, ServerId (+ the archive's SHA-256 for a restore); operations: the envelope carries the OperationId. |
| `DeleteBackupCommand` / `SendBackupCommand` | BackupId, ServerId (+ a transfer ticket to send it); answered in the ack. |
| `InstallAddonCommand` / `RemoveAddonCommand` | ServerId, `Key` of an addon of the server's manifest; operations (Phase 12). |
| `CancelOperationCommand` | OperationId. |
| `RotateCredentialCommand` | — (the agent starts the rotation). |
| `UpdatePanelKeysCommand` | List of panel keys signed by the currently pinned key. |

`ServerRuntimeSpec` contains the **manifest** (its exact text), variable values (secrets included: the signed envelope travels over TLS and the agent stores records encrypted), port assignments (`name → ip:port`), `ExtraArguments` and the restart policy. The agent **renders** the command line itself from the manifest: the panel **never sends a command line or a free executable path**. The RCON password of `logfile+rcon` games is generated by the agent and never leaves it.

**Agent → panel (hub methods)**

```csharp
Task<HelloAccepted> Hello(AgentHello hello);          // first message after connecting
Task ServerStateChanged(ServerStateChanged e);
Task ConsoleOutput(ServerConsoleOutput e);             // only for subscribed servers, batched
Task Metrics(MetricsBatch e);                          // every 5 s, one message per node (running servers only)
Task NodeMetrics(NodeMetrics e);                       // every 15 s
Task OperationProgress(OperationProgress e);
Task OperationCompleted(OperationCompleted e);         // retried from an outbox until acked

public sealed record AgentHello(
    string AgentVersion, int ProtocolVersion, IReadOnlyList<string> Capabilities,
    NodeInventory Inventory,                           // hostname, OS build, CPU, RAM, disks
    IReadOnlyList<ServerSnapshot> Servers,             // actual state of every known server
    IReadOnlyList<Guid> OperationsInFlight);

public sealed record ServerStateChanged(
    Guid ServerId, RuntimeState State, RuntimeState Previous,
    DateTime AtUtc, long StateSequence, int? ExitCode, StateChangeReason Reason);

public sealed record ServerConsoleOutput(Guid ServerId, IReadOnlyList<ConsoleEntry> Entries);
public sealed record ConsoleEntry(long Sequence, DateTimeOffset At, string Text, bool IsGap);

public sealed record MetricsBatch(DateTimeOffset At, IReadOnlyList<ServerMetrics> Servers);
public sealed record ServerMetrics(
    Guid ServerId, double CpuPercent, long MemoryBytes, int ProcessCount, TimeSpan Uptime,
    ServerQueryInfo? Query);   // Phase 11: what the game answered to its manifest's query
public sealed record ServerQueryInfo(DateTimeOffset At, string? Name, string? Map, int Players, int MaxPlayers,
    int Bots, string? Version, IReadOnlyList<QueryPlayer> PlayerList);

public sealed record OperationProgress(Guid OperationId, string Stage, double? Percent, string? Message);
public sealed record OperationCompleted(Guid OperationId, Guid? ServerId, OperationOutcome Outcome,
    string? ErrorCode, string? Message, CommandResult? Result);   // Result: a BackupResult for a backup
```

`HelloAccepted` returns the server time (to detect clock skew), the desired state of every server, the console subscriptions browsers are watching (`ConsoleSubscriptions`, with the panel's last sequence of each) and the current panel keys. This drives **reconciliation** after every reconnect or panel restart (see doc 05).

Disk usage is not part of the periodic metrics: walking 60 GB of CS2 every 5 s would be absurd. It is computed on demand and cached (≥ 10 min).

*Query (Phase 11):* a running server whose manifest has a `query` is asked every 10 seconds (every 2 while it starts) by its supervisor, not by the metrics timer, so a game slow to answer never delays the batch; the last answer goes with the server's metrics while it is less than 30 seconds old. Its texts come from the game and from its players: the agent removes control characters, direction overrides and Minecraft colour codes and cuts them to 128 characters and 256 players (`QueryText`), and the panel does the same again before pushing them to browsers, which show them as text.

### Console: flow and volume

- The agent keeps a **ring buffer** per server (e.g. 2 MiB) with a monotonic `Sequence`, and writes a rotated log on disk through a batching writer (channel + periodic flush, never one write per callback).
- Console output is only sent to the panel when at least one browser is watching (`SubscribeConsoleCommand`, reference-counted by the panel). Thirty noisy servers do not saturate the link if nobody watches them.
- The agent coalesces output (≤ 50 ms or 16 KiB per message). If the link backs up, the oldest output is dropped and an `IsGapMarker` is emitted.
- Browser reconnect: it sends its last `Sequence`; the panel asks the agent for the backlog from there. No silent gaps.
- UTF-8 decoding is *stateful* in the agent (chunks can split a multi-byte character); in `pipes` mode the encoding is declared by the manifest.

**Implementation (Phase 5).**

- *Agent buffer* (`ConsoleBuffer`, one per server, across restarts of its game): about one million characters, oldest out first. Small pieces of output join the last entry while it is younger than 50 ms, shorter than 16 KiB and nobody has read it; once read, an entry never changes ("seal on read"), so a sequence always means the same text. Sequences start at the buffer's creation time in microseconds since 1970 (below 2^53, safe in JavaScript): a restarted agent continues above every sequence the previous run used, so the panel and browsers can tell new output from output they have. This relies on the node's clock not going back between two runs.
- *Sources:* pseudo console and pipe output as is (VT sequences included, xterm renders them); for `logfile+rcon` the log file, followed for the whole run with a last read after the exit (what the game writes while quitting is kept). Console input: written to the console (`pty` echoes it; for `pipes` the agent adds `> command`), or sent over an RCON session kept per process. For RCON the agent adds `> command` and the answer, unless the manifest says the game logs commands itself (`rcon.logsCommands`, CS2); such a game logs some answers (`status`) but not others (cvar queries such as `sv_tags`), so the agent shows an answer whose first line does not appear in the log within 2 seconds (Phase 11). A session found closed is replaced once; a late answer is never retried (the game may have run the command). Failures appear in the console as a dimmed `[Pulsar]` note.
- *Relay:* one sender per subscription: up to 64 KiB of entries per message, at most one message per 50 ms, entries kept until sent while the link is down. A subscription that falls behind what the buffer still holds gets a gap entry (`IsGap`, the last missing sequence). Subscriptions end when the connection ends; the panel subscribes again in `HelloAccepted`.
- *History on disk:* `console-logs\{shortId}\console.log`, one line per console line prefixed with its UTC time, escape sequences removed, written in batches (at most once a second) by a background writer that drops output rather than slow the game; rotated at 10 MiB, four older files kept, deleted with the server.
- *Panel copy* (`ConsoleMirrors`): per watched server, the last 256 K characters, deduplicated by sequence. The first browser to watch subscribes the panel; later browsers and reconnects get what the copy holds after their last sequence, then the stream. Nobody watching for 30 s: the agent is unsubscribed; the copy goes after 10 minutes. After an agent reconnect or a panel restart the subscription starts again from the panel's last sequence (or from nothing: the agent then sends its whole buffer and browsers drop what they have), so neither loses output. Output from an agent about a server that is not one of its node's watched servers is dropped.
- *Metrics:* the agent samples each running server's Job every 5 s: CPU time of every process it ever held (as a share of the whole machine since the previous sample, like the node's CPU and Task Manager), working set of the processes alive in it (`conhost.exe` included), process count and uptime. The panel keeps the last sample per server in memory (`LiveMetrics`, older than 15 s counts as none) and pushes it to the pages watching that server.

### Large transfers

The panel cannot connect to the agent (no inbound port), so:

1. Browser → `POST /api/v1/servers/{id}/files/upload` (streaming, authorized).
2. The panel creates a single-use **ticket** (256 random bits, 60 s TTL) and sends `PrepareTransferCommand`.
3. The agent opens `GET /agent/v1/transfers/{ticket}` and the panel **pipes** the browser stream into that response (`System.IO.Pipelines`, with backpressure, never staged on the panel's disk).
4. Downloads (files, backups): symmetric, with a `PUT` from the agent.

Size limits are configurable; the agent writes to a temporary file inside the server directory and renames it when done (atomic).

**Implementation (Phase 6).**

- *What travels where:* listings, new folders, renames and deletions are signed commands that answer in their ack (`CommandAck.Result`, an additive field: `FileListResult`, `FileEntryResult`). File content never goes through the hub: editor reads and saves, uploads and downloads are all transfers. A listing is cut where its message could outgrow the hub's limit (2,000 entries at most, `Truncated`).
- *Commands:* `server.files_list`, `server.files_create_directory`, `server.files_move` (never replaces), `server.files_delete` (a folder with everything in it; links removed, never followed), `server.files_send` (the agent checks it can read the file, answers its size and version, then `PUT`s it) and `server.files_receive` (the agent checks the target and creates the temporary file, acks, `GET`s exactly the announced size and reports with `IAgentHub.TransferCompleted`). Changes are refused with `Busy` while an operation (provision, install, update, delete) runs on the server; reading is not.
- *Tickets* (`TransferTicket`): 256 random bits, at `/agent/v1/transfers/{ticket}` with the agent's session token. A ticket works once and only for the node it was issued to: one presented by another node is burnt; one nobody claims within 60 s expires. The panel's `FileTransfers` holds the browser's request until the agent's request arrives, then copies one into the other as data arrives: nothing is staged on the panel's disk or held in memory (moving an 841 MB map each way, the panel peaked at 235 MB of working set and the agent at 160 MB). A transfer whose other end sends or takes nothing for two minutes is abandoned by the agent, and by the panel too: an upload the agent claimed but stopped reading, or a download it claimed without sending a body within 60 s, fails (`files.transfer_failed`) instead of holding the browser's request. The editor's 1 MiB limit is checked again on the length the agent sends.
- *Writes* go to `.pulsar-upload-<random>.tmp` next to the target, with the space reserved up front (a full disk fails before the transfer), then replace the target in one rename. A short transfer, a target changed meanwhile or any failure removes the temporary file and leaves the target as it was. Versions (`FileVersion`: size and last write time) let the editor save only over what it opened (`files.changed`).

### Backups (Phase 7)

- *Operations:* `server.backup_create` and `server.backup_restore`. Their progress stages are `scanning`, `archiving` (percent of the bytes to read), `checking` (the SHA-256 pass) and `restoring` (percent of the bytes to write). A successful backup's `OperationCompleted` carries `Result` (additive, a `CommandResult`): `BackupResult(Size, FileCount, SkippedFiles, Sha256)`; the panel completes the backup's row with it.
- *Not exclusive:* while a backup runs the server can start, stop and restart, and its files can be edited (doc 05's matrix); another backup, an install, an update, a restore or a deletion gets `Busy`. A backup can be cancelled like an install (`CancelOperationCommand`): the partial archive is removed. A restore is exclusive, needs the server stopped (`server.running` otherwise) and cannot be cancelled from the panel: half a restore is worse than either side of it.
- *Plain commands:* `server.backup_delete` (`AlreadyInState` when the archive is already gone) and `server.backup_send`, which sends the archive like a file (`SendFileCommand`'s transfer ticket and `PUT`). An archive being read (a download, a restore) cannot be deleted (`backups.in_use`).
- *Storage:* `<backups root>\<shortId>\<backupId>.zip`, by default the agent's `backups` folder (SYSTEM and Administrators only: a game server can neither read nor change its backups), or the folder given at enrollment (`--backups-root`, restricted the same way; it must be empty or already the agent's). Written as `<backupId>.zip.partial` and renamed when complete; an agent that stopped halfway deletes the partial archives when it starts. Deleting a server deletes its backups.
- *Errors:* `backups.not_found` (the archive is not on the node), `backups.corrupt` (its SHA-256 changed, or an entry a restore may not write), `backups.in_use`; reading and writing server files fail with the file manager's codes (`files.*`).
- *Steam accounts (Phase 13):* `InstallServerCommand`, `UpdateServerCommand` and `RemoveAddonCommand` take an optional `Login`, a `SteamLogin(AccountId, UserName, Password, GuardCode?)` (additive; agents announce `steam.login`, and the panel refuses an account to agents that do not, `steam_account.unsupported`). The agent keeps none of it: it signs SteamCMD in with it and keeps only the login SteamCMD cached, per `AccountId` and tied to a hash of the name and password it came with, so a changed one drops it (doc 06). `SteamLogin.ToString` prints the password and the code as `***`. A game that needs an account and gets none fails with `steamcmd.account_required` on the agent too.
- *Remote copies (Phase 10):* the four backup commands take an optional `Remote`, a `RemoteBackupTarget(Endpoint, Region, Bucket, Key, AccessKeyId, SecretAccessKey, PathStyle)` the panel builds from the server's backup storage (additive; agents announce the `backups.remote` capability, and the panel sends none to agents that do not). After the archive is written the agent uploads it (stage `uploading`, multipart, the archive's SHA-256 as `pulsar-sha256` metadata) and reports `BackupResult.RemoteCopied` or `RemoteError`: a failed copy does not fail the backup. A restore of an archive the node no longer has downloads it first (stage `downloading`), checks it against the SHA-256 and keeps it; a download streams it from the storage; a delete removes the copy too (within 20 s, before the ack). The agent keeps nothing of the target: the secret arrives in each command and is not printed (`RemoteBackupTarget.ToString` leaves it out). Failures are `backups.remote_failed` with what the storage answered.

### Addons (Phase 12)

- *Operations:* `server.addon_install` and `server.addon_remove`, exclusive and only on a stopped, installed server (the panel answers `server.must_be_stopped` otherwise); an install can be cancelled. The key names an addon of the manifest the agent already holds: the panel never sends a URL, a repository or a file list. Progress stages: `preparing` (asking GitHub for the release), `downloading` (percent), `installing` (unpacking).
- *Results:* a successful operation's `OperationCompleted` carries `AddonsResult(Addons)`, a list of `AddonRelease(Key, Release)` (no release: removed); an update that reinstalls `reinstallAfterUpdate` addons reports their releases the same way. The panel keeps them in `server_addons`.
- *Spec:* `ServerRuntimeSpec.Addons` (additive) lists the installed keys; before each start the agent applies their `lineEdits`.
- *Capabilities:* `server.addon_install` and `server.addon_remove`; the panel refuses addons for agents without them (`addon.unsupported`).
- *Errors:* `addon.no_release`, `addon.no_asset` (not exactly one asset matches), `addon.no_checksum` (GitHub publishes no SHA-256), `addon.checksum_mismatch`, `addon.untrusted_host`, `addon.download_failed`, `addon.unsupported_source`, `addon.bad_archive` (an entry outside the target, or more than Pulsar unpacks), `addon.file_missing` (at a start: a file an installed addon edits is missing; a missing anchor is `config.invalid`).

### Protocol versioning

- Major version in the path (`/agent/v1/…`) and in `ProtocolVersion`.
- Within a major version only **additive** changes: new optional fields, new commands announced through `Capabilities`. The panel never sends a command the agent does not announce.
- Panel N supports agents on protocol N and N-1 for at least one minor release, so nodes can be upgraded gradually.
- Incompatibility → the panel rejects the `Hello` with an explicit error ("Agent 1.0 is not compatible with panel 2.0; update the agent"), visible in the node UI.

### Limits and robustness

- Bounded `MaximumReceiveMessageSize` (e.g. 1 MiB) on both hubs.
- Reconnect with exponential backoff + jitter (max 60 s); challenge re-authentication on every reconnect.
- SignalR keep-alive; a node without a connection for > 30 s shows as "disconnected" in the UI.
- If the panel goes down, **game servers keep running**; crash auto-restart and boot auto-start are local to the agent.

## Node registration and authentication

### Identity

- Every node has an **ECDSA P-256 key pair generated on the node itself**. The private key **never leaves the node**: it is created through CNG as a **non-exportable** machine key, using the *Microsoft Platform Crypto Provider* (TPM) when present, or the *Software Key Storage Provider* otherwise. (Checked with a virtual TPM on Windows Server 2022, rotation included: `scripts/hyperv`.)
- The panel only knows the public key (`node_credentials`). There is no global API key; each node has its own revocable, rotatable credential.
- The panel has its own signing key; the agent **pins** it at enrollment (by fingerprint) and thereby authenticates the panel independently of the TLS certificate (useful with proxies that terminate TLS or self-signed certificates).

Comparison with mTLS:

| | **Application-level proof of possession (recommended)** | mTLS |
|---|---|---|
| Behind a reverse proxy that terminates TLS | Works | Breaks (or requires forwarding the certificate in a header, fragile) |
| Own CA, issuance, CRL | Not needed | Needed |
| Immediate revocation | Yes (database state + abort connection) | Needs CRL/OCSP or custom logic |
| Robustness against TLS MITM | Signed commands + pinned panel key | Strong at the transport level |

**Recommendation:** application-level proof of possession with mandatory TLS. The identity (node key pair) is the same one mTLS would use, so an optional mTLS mode (the panel signs a certificate for the same key) can be added later without re-enrolling nodes.

### Enrollment

```
Admin (UI)                  Panel                                   Windows node
   │ POST /api/v1/nodes ───►│ creates Node(status=pending_enrollment)
   │                        │ token = 32 random bytes (CSPRNG)
   │                        │ stores SHA-256(token), expires in 30 min, single use
   │◄── token (shown ONCE) + panel key fingerprint + ready-to-run command:
   │
   │   PulsarAgent.exe enroll --panel https://panel.example.com
   │                          --token <token> --panel-key sha256:<fingerprint>
   │                                                              │ (run as administrator)
   │                        │◄── GET /agent/v1/panel-identity ────│
   │                        │─── panel public key ──────────────►│ checks fingerprint == --panel-key
   │                                                              │ generates ECDSA P-256 pair (CNG, non-exportable, TPM if present)
   │                        │◄── POST /agent/v1/enroll ───────────│ { token, publicKey (SPKI), keyStorage,
   │                        │                                     │   hostname, osBuild, agentVersion,
   │                        │                                     │   proof = Sign("pulsar-enroll-v1"‖token‖publicKey) }
   │                        │ validates: hash exists, not expired, not consumed;
   │                        │ verifies proof with publicKey;
   │                        │ atomic consumption: UPDATE … SET consumed_at=now() WHERE consumed_at IS NULL
   │                        │ creates NodeCredential(active); Node → active; audits "node.enrolled"
   │                        │─── { nodeId, credentialId, panelKeys } signed with the panel key ──►│
   │                                                              │ verifies signature; writes config with ACL
   │                                                              │ (SYSTEM + Administrators); installs and starts the service
   │◄── the UI shows the node "Connected" + the node key fingerprint (verifiable on the node with `PulsarAgent.exe status`)
```

The enrollment endpoint is rate limited. A leaked token works at most once and for 30 minutes; the administrator sees the hostname and fingerprint of the node that consumed it.

### Bulk enrollment (Phase 14)

To enroll many machines with one command (the agent MSI deployed through Intune or a group policy, or a script), an administrator with `node.credentials.manage` held globally creates a **reusable enrollment token** (`/api/v1/enrollment-tokens`): a name, a number of uses (1 to 1000), an expiry (1 hour to 90 days) and whether the nodes it creates wait for approval (on by default). The value (`pulsar_enroll_` and 32 random bytes) is shown once; only its SHA-256 is stored, with its first characters to recognize it. At most 50 usable tokens exist at a time, and a token can be revoked whenever.

The agent enrolls with it exactly as with a single-use token (the same endpoint, proof and signed answer). The panel looks the hash up among single-use tokens first, then among reusable ones, and then:

- counts the use atomically (`UPDATE … SET uses = uses + 1 WHERE uses < max_uses AND revoked_at IS NULL AND expires_at > now`), so machines racing for the last use cannot all enroll;
- creates a **new node** named after the machine (with `-2`, `-3`… when the name is taken; a save that loses a race to the same name, a unique violation of the node's name, retries with a suffix). A use never re-enrolls an existing node: a leaked token cannot take one over. The host name the agent reports (here and at every `Hello`) loses control and format characters (zero-width spaces, direction overrides) and line and paragraph separators, so a node cannot pass for another, and is cut without splitting a character. A request whose key is registered already (a replayed one) is refused without using the token;
- audits `node.created` and `node.enrolled` with the token's name, and answers with `awaitingApproval` when the token requires approval, which `PulsarAgent enroll` reports. A refused use (expired, revoked or used-up token, a replayed key) is audited `node.enrollment_failed` with the token's name and id.

Enrollments are limited per client address (`RateLimiting:Auth:EnrollPermitLimit`, 10 a minute by default); the panel's 429 says when to come back (`Retry-After`). Machines enrolling together behind one NAT address share that limit, so `PulsarAgent enroll` waits as told, plus a random share of up to 10 s, and tries again for up to 10 minutes; it never retries a refused token. A larger rollout from one site raises the limit for its duration (docs/guides/install.md).

A node waiting for approval (`pending_approval`) is enrolled but may not authenticate: its agent retries with its usual backoff and connects within a minute of the approval. Disabling and enabling such a node does not skip the approval. Approving takes `node.manage` (`POST /nodes/{id}/approve`, or `POST /nodes/approve` with up to 200 nodes, held globally), audited `node.approved`; deleting the node turns the machine away. The node list shows, for each node, the reusable token that created it and its active key's fingerprint, so a machine can be checked before it is approved. Approving (or enabling) a node starts the "node offline" notification's grace period again: its agent was turned away until then.

### Authentication on every connection

```
Agent                                         Panel
  │── POST /auth/challenge {nodeId, credentialId} ─►│ 32-byte nonce, 60 s TTL, single use (in memory)
  │◄─ {challengeId, nonce, panelOrigin} ────────────│
  │── POST /auth/token {challengeId,                │
  │     signature = Sign("pulsar-agent-auth-v1" ‖ nodeId ‖ credentialId ‖ nonce ‖ panelOrigin)} ─►│
  │                                                 │ verifies signature with the registered key,
  │                                                 │ credential active, node not revoked/disabled,
  │                                                 │ consumes the nonce; updates last_used_at
  │◄─ JWT {sub=nodeId, cid=credentialId, sid=sessionId, aud=pulsar-agent, exp=+15 min} ─│
  │── WSS /agent/v1/hub  (Authorization: Bearer …) ─►│ OnConnected: re-checks credential/node in the database;
  │                                                 │ registers the connection (closes a previous one for the same node)
  │── Hello(AgentHello) ───────────────────────────►│ reconciliation
  │◄─ HelloAccepted ────────────────────────────────│
```

- Including `panelOrigin` in the signed data prevents replaying a response against another panel.
- **Implementation note (Phase 2):** the session is an opaque random token (stored as SHA-256, in memory) rather than a JWT. The panel is a single instance in v1 and keeps per-session state anyway (command sequence, immediate revocation), so a JWT would add signing-key management without removing that state. A panel restart ends every session; agents re-authenticate on reconnect.
- The session token only works for the agent hub and transfers, lasts 15 minutes, and the connection closes when it expires (the agent re-authenticates transparently).
- One active connection per node.

### Rotation

**Node key** — automatically once the key is 90 days old (checked at every connection), or when an administrator asks from the node page (`RotateCredentialCommand`, permission `node.credentials.manage`):

1. The agent creates a new key in the same scope and provider preference (TPM first) and calls `POST /agent/v1/credentials/rotate` within its current session, with `proof = Sign_new("pulsar-rotate-credential-v1" ‖ nodeId ‖ currentCredentialId ‖ sessionId ‖ newPublicKey)`: the session proves the old key, the proof the new one, both bound to this node and session.
2. The panel registers a `pending` credential (an earlier pending one is superseded) and answers with `CredentialRotation(nodeId, credentialId, keyFingerprint)` signed by a pinned panel key (`pulsar-rotation-result-v1`). The agent verifies the signature, the node and that the fingerprint is the new key's (so a signed answer to another rotation, replayed or mixed up, is refused), then saves the new key as `pendingRotation` in its configuration before using it. `keyFingerprint` is an optional field added within v1 (see "Protocol versioning"): an answer from an older panel has none and is still accepted, signed for this node; a credential registered for another key would then fail at its first sign-in, and the agent keeps its current key.
3. The agent authenticates with the pending credential (challenge and token accept `pending` credentials). That first authentication makes the panel activate it and supersede the previous one in one transaction, and end the sessions of the old key; the agent's new connection replaces the old one at `Hello`. The agent then switches its configuration to the new key and deletes the old one.

At any interruption one of the two keys works: before step 3 the current credential is still active; after it, `pendingRotation` is still in the configuration, so the next start authenticates with it first. If the panel refuses a pending key (a later rotation superseded it, or it was revoked) the agent deletes it and keeps the current one. A failed rotation is retried after an hour.

**Panel key** — from the Nodes page (permission `settings.manage`): rotating creates a new active key and turns the previous one into `retiring`; the panel accepts both and signs with the active key.

- Every `Hello` carries `pinnedPanelKeys`. The panel stores them on the node and signs that connection's commands with the active key if the agent pins it, otherwise with the newest retiring key it pins.
- When the pins differ from the panel's keys, the panel sends `UpdatePanelKeysCommand` (to connected agents right after the rotation, to the others when they connect), signed with a key the agent already trusts. The agent requires every key id to be its fingerprint and the list to include the key that signed it, so an update can never lock it out.
- Retiring the old key (only one key can be retiring at a time) sends the shortened list to connected agents. A node that never connected during the rotation pins only a retired key: the panel can no longer sign anything it accepts, the UI flags it as "old panel key", and it has to be enrolled again. The Nodes page shows how many nodes trust each key before retiring.

### Revocation and expiry

- Revoke (UI, permission `node.credentials.manage`): credential → `revoked`, the node's active connection is aborted immediately, any later challenge fails. Audited.
- Disabling a node: same effect without deleting the credential.
- Enrollment tokens expire (30 min by default). Session JWTs: 15 min. Node credentials: automatic rotation; optional hard expiry by policy (`expires_at`).
- Re-enrolling a node (e.g. Windows reinstall) = a new enrollment token for the same `Node`; the previous credential is revoked, and so is a pending one from an unfinished rotation.

### Agent local storage

```
%ProgramData%\Pulsar\Agent\        ACL: SYSTEM + Administrators (inheritance disabled)
  config\agent.json                 panel URL, nodeId, credentialId, pinned panel keys
  state\servers\{id}.json           per-server spec, RCON password and whether it should run, encrypted with DPAPI (machine scope)
  state\outbox\                     OperationCompleted results awaiting ack
  steamcmd\steamcmd.exe             the verified SteamCMD bootstrapper (each server runs its own copy, doc 06)
  console-logs\{shortId}\           rotated console logs (outside the server directory)
  backups\{shortId}\{backupId}.zip  backups (another folder with enroll --backups-root)
  logs\                             agent logs (Serilog, rolling)
<ServersRoot>\{shortId}\            server directory (configurable, e.g. D:\PulsarServers)
```

The node's private key is not in this tree: it lives in the CNG/TPM key store.

## Agent implementation (Phase 2)

How `Pulsar.Agent.Core` (portable protocol logic), `Pulsar.Agent.Windows` (CNG, inventory, ACLs, service control) and the `PulsarAgent` executable put the design above into practice.

### Command line

```
PulsarAgent enroll --panel <url> --token <token> --panel-key <fingerprint> [--servers-root <dir>] [--no-service] [--dev] [--data-dir <dir>] [--log-file <file>]
PulsarAgent status [--check] [--data-dir <dir>]
PulsarAgent run [--data-dir <dir>]
PulsarAgent service install|uninstall|start|stop [--stop-servers] [--data-dir <dir>]
PulsarAgent unenroll [--data-dir <dir>]
PulsarAgent host --server <id> --pipe <name> [--data-dir <dir>]   (started by the agent, not listed by help)
```

- The service runs the executable it was installed from as SYSTEM, so `enroll` and `service install` refuse when anyone other than SYSTEM, Administrators or TrustedInstaller could modify it or its folder, or rename a folder above it (a download folder, or a folder created directly under `C:\`, fails; `C:\Program Files\Pulsar\Agent` passes). The check runs before enrolling, so a refusal does not consume the token. `service start` checks again, and so does the agent itself when it runs as the service (started by the service control manager, or as SYSTEM): it logs the reason and exits.
- `enroll` must run elevated. It pins the panel key, creates the node key, enrolls, writes `config\agent.json`, and installs and starts the `PulsarAgent` service (LocalSystem, automatic start; the service control manager restarts it after 5 s, 15 s, then every 60 s, also when it stops itself with an error). Re-running it on an enrolled node stops the service first and deletes the old key once the new enrollment is saved.
- `status` shows the enrollment and the node key fingerprint (the one the panel shows for the credential); `--check` also authenticates against the panel.
- `--dev` is for development only: the key goes to the current user's store, the state directory also grants the current user, and no service is installed. The service runs as SYSTEM and could not open such a key. A development enrollment never runs as the service either: `service install` and `service start` refuse it, and the agent refuses to run it as the service, where its servers (started as the current user) would run as SYSTEM.
- `unenroll` removes the service, the node key and the configuration (logs stay). The node must also be deleted, or re-enrolled, in the panel. Since Phase 14 it stops the node's game servers first (they would otherwise keep running in their host processes); hosts the service was not running to stop are ended.
- `service stop` leaves the game servers running in their hosts (Phase 14, doc 06 "Server hosts"); `--stop-servers` stops them with the service, and they start again with it.
- `enroll --log-file` also writes what it prints to a file: the agent MSI runs it as a custom action, which has no console.
- `host` is the process a game server runs under (doc 06, "Server hosts"); its log is `logs\hosts\host-<server>-<date>.log`.
- The value after an option is always taken as its value: enrollment tokens are base64url and can start with `-`.
- The process list shows command lines: `--token -` reads the token from standard input, and without `--token` it is taken from the `PULSAR_ENROLLMENT_TOKEN` environment variable.

### Panel address

The panel URL must be an origin (`https://panel.example.com[:port]`), without path, query or credentials. HTTPS is required; plain HTTP is accepted only for a loopback panel, because the session token would otherwise travel in clear. The agent compares the origin the panel binds into enrollment results and challenges with the one it was configured with, and refuses to continue on a mismatch: behind a reverse proxy, the proxy must forward the original host and scheme.

### Node key

ECDSA P-256 created through CNG with export policy *none*, in the TPM (Microsoft Platform Crypto Provider) when it can create the key, otherwise in the software key storage provider; the result is reported to the panel (`tpm`/`software`) and shown in the UI. Production keys are machine keys whose DACL grants only SYSTEM and Administrators. The state directory (`%ProgramData%\Pulsar\Agent` by default) gets a protected DACL with the same two principals and Administrators as owner. What the agent's own folders in it already hold (`config`, `logs`, `state`, `steamcmd`, `console-logs`, `backups`: a folder someone created before the agent) is reset to inherit that DACL, owned by Administrators; anything else in it (a development agent's `servers`) keeps its own rules; a junction or symbolic link at its top or in the agent's folders is refused (other reparse points, such as deduplicated files or OneDrive placeholders, are ordinary files, as in the file manager). A `--backups-root` gets the same DACL, but one that already holds something is refused unless it is already restricted to those accounts (an earlier enrollment): restricting it would change who can open the files there, and the agent cannot tell its backups from planted ones; and enrollment refuses a state directory (or backups folder) below a folder that an account other than SYSTEM, Administrators or TrustedInstaller owns or could rename or replace, before anything is written.

### Connection loop

1. Challenge/response → session token; the agent checks the challenge's `panelOrigin` before signing.
2. Hub over WebSockets only (no negotiation), `Authorization: Bearer` header, then `Hello` with the capabilities, inventory and agent version. `HelloAccepted.ServerTime` gives the panel clock offset; a difference above one minute is logged as a warning.
3. Node metrics right after `Hello` and every 15 s.
4. **Session hand-over:** one minute before the session expires (or at half its lifetime if it is shorter), the agent authenticates again and opens a second connection; its `Hello` makes the panel close the old one. Commands always arrive on the current connection, and the node never shows as offline because its session ended.
5. A lost connection is retried with exponential backoff (1 s, 2 s, 4 s… up to 60 s), randomized to 50–100 % of the value so many agents do not reconnect in lockstep after a panel restart. A rejected credential (revoked, node disabled or deleted) waits the maximum between attempts.

The SignalR client's automatic reconnect is not used: every connection needs a fresh challenge and a new `Hello`, which the loop already does.

### Command verification

One verifier per session, in this order (the first failure answers the command):

| Check | Ack status | Error code |
|---|---|---|
| `KeyId` is a pinned panel key | `InvalidSignature` | `command.unknown_key` |
| Signature over the payload, for the `pulsar-command-v1` purpose | `InvalidSignature` | `command.invalid_signature` |
| Envelope readable | `Rejected` | `command.malformed` |
| `NodeId` and `SessionId` are this node and session | `Rejected` | `command.wrong_recipient` |
| Sequence new and inside the 1024-wide window | `Rejected` | `command.replayed` |
| Inside `IssuedAt`–`ExpiresAt` on the panel's clock, ±30 s | `Rejected` | `command.expired` |
| Supported `ProtocolVersion` | `Rejected` | `command.unsupported_version` |
| Command type handled by this agent | `UnsupportedCommand` | `command.unsupported` |

The verifier reads the pinned panel keys on every command, so an `UpdatePanelKeysCommand` applies to the next command. That command is also refused (`command.invalid_panel_keys`) when a key id is not its fingerprint, a key is not P-256, or the list leaves out the key that signed it.

Routing checks run on the envelope without its command, so an unknown command type is reported as unsupported rather than malformed. A sequence number is consumed as soon as the command is authentic and addressed to this session, even if a later check refuses it.

## Agent implementation (Phase 3): servers

- **Commands:** `server.provision`, `server.install` and `server.delete` are operations: the envelope carries the operation id, the ack only says accepted, and the result follows as `OperationCompleted`, kept in `state\outbox` and delivered until the hub call succeeds. One operation runs per server; another one gets `Busy`. `server.start`, `server.stop`, `server.restart` and `server.kill` answer `Accepted` or `AlreadyInState` from the server's state machine (doc 05).
- **Events:** `ServerStateChanged` (sequence per server, increasing while the agent runs) is sent without waiting for an answer; the panel checks that the server belongs to the reporting node and drops older changes. `OperationProgress` is advisory. `OperationCompleted` is idempotent on the panel.
- **Hello** adds `Servers` (each server's state) and `OperationsInFlight` (running operations, plus finished ones whose result is still in the outbox) for reconciliation.
- **Capabilities** list the server commands, plus `server.test_game` when the agent has Pulsar's test game server.
- **Servers root:** the folder chosen at enrollment, otherwise `%SystemDrive%\PulsarServers` (`<data-dir>\servers` for a development agent). It is reported in the inventory.
