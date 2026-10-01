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
| `POST /agent/v1/credentials/rotate` | Node key rotation. | JWT + proof with the new key |
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
| `ApplyServerSpecCommand` | New `ServerRuntimeSpec` (variables, ports, arguments, restart policy). |
| `InstallServerCommand` / `UpdateServerCommand` | ServerId, `Validate` (bool), `OperationId`. |
| `StartServerCommand` / `StopServerCommand` / `RestartServerCommand` / `KillServerCommand` | ServerId (+ optional stop timeout). |
| `SendConsoleInputCommand` | ServerId, `Line` (≤ 512 characters, no control characters). |
| `SubscribeConsoleCommand` / `UnsubscribeConsoleCommand` | ServerId, `FromSequence?` (backlog). |
| `DeleteServerCommand` | ServerId, `DeleteBackups` (bool). |
| `ListDirectoryCommand`, `ReadFileCommand`, `WriteFileCommand`, `CreateDirectoryCommand`, `RenamePathCommand`, `DeletePathsCommand` | ServerId + **relative path** + size limits (text ≤ 1 MiB inline). |
| `PrepareTransferCommand` | Ticket, direction (upload/download), relative path, maximum size. |
| `CreateBackupCommand` / `RestoreBackupCommand` / `DeleteBackupCommand` | BackupId, ServerId, OperationId. |
| `CancelOperationCommand` | OperationId. |
| `RotateCredentialCommand` | — (the agent starts the rotation). |
| `UpdatePanelKeysCommand` | List of panel keys signed by the currently pinned key. |

`ServerRuntimeSpec` contains the **manifest** (text + SHA-256; the agent caches by hash), variable values (including secrets), port assignments, `ExtraArguments` and the restart policy. The agent **renders** the command line itself from the manifest: the panel **never sends a command line or a free executable path**.

**Agent → panel (hub methods)**

```csharp
Task<HelloAccepted> Hello(AgentHello hello);          // first message after connecting
Task ServerStateChanged(ServerStateChanged e);
Task ConsoleOutput(ServerConsoleOutput e);             // only for subscribed servers
Task Metrics(MetricsBatch e);                          // every 5 s, one message per node
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

public sealed record ServerConsoleOutput(
    Guid ServerId, long Sequence, DateTime AtUtc, string Text, bool IsGapMarker);

public sealed record ServerMetrics(
    Guid ServerId, double CpuPercent, long MemoryBytes, int ProcessCount,
    int? MainProcessId, TimeSpan Uptime);

public sealed record OperationProgress(Guid OperationId, string Stage, double? Percent, string? Message);
public sealed record OperationCompleted(Guid OperationId, OperationOutcome Outcome,
    string? ErrorCode, string? Message, JsonElement? Result);
```

`HelloAccepted` returns the server time (to detect clock skew), the desired state of every server, active console subscriptions and the current panel keys. This drives **reconciliation** after every reconnect or panel restart (see doc 05).

Disk usage is not part of the periodic metrics: walking 60 GB of CS2 every 5 s would be absurd. It is computed on demand and cached (≥ 10 min).

### Console: flow and volume

- The agent keeps a **ring buffer** per server (e.g. 2 MiB) with a monotonic `Sequence`, and writes a rotated log on disk through a batching writer (channel + periodic flush, never one write per callback).
- Console output is only sent to the panel when at least one browser is watching (`SubscribeConsoleCommand`, reference-counted by the panel). Thirty noisy servers do not saturate the link if nobody watches them.
- The agent coalesces output (≤ 50 ms or 16 KiB per message). If the link backs up, the oldest output is dropped and an `IsGapMarker` is emitted.
- Browser reconnect: it sends its last `Sequence`; the panel asks the agent for the backlog from there. No silent gaps.
- UTF-8 decoding is *stateful* in the agent (chunks can split a multi-byte character); in `pipes` mode the encoding is declared by the manifest.

### Large transfers

The panel cannot connect to the agent (no inbound port), so:

1. Browser → `POST /api/v1/servers/{id}/files/upload` (streaming, authorized).
2. The panel creates a single-use **ticket** (256 random bits, 60 s TTL) and sends `PrepareTransferCommand`.
3. The agent opens `GET /agent/v1/transfers/{ticket}` and the panel **pipes** the browser stream into that response (`System.IO.Pipelines`, with backpressure, never staged on the panel's disk).
4. Downloads (files, backups): symmetric, with a `PUT` from the agent.

Size limits are configurable; the agent writes to a temporary file inside the server directory and renames it when done (atomic).

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

- Every node has an **ECDSA P-256 key pair generated on the node itself**. The private key **never leaves the node**: it is created through CNG as a **non-exportable** machine key, using the *Microsoft Platform Crypto Provider* (TPM) when present, or the *Software Key Storage Provider* otherwise.
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

- The agent rotates its key automatically every 90 days (configurable) or when the administrator asks (`RotateCredentialCommand`).
- Flow: generate a new key → `POST /credentials/rotate` with the current JWT + a proof signed with the new key → the panel creates a `pending` credential → the agent re-authenticates with the new key → the panel marks it `active` and the old one `superseded`. If anything fails, the old credential stays valid (a node can never be orphaned mid-rotation).
- Panel key rotation: the panel publishes the new key signed by the current one (`UpdatePanelKeysCommand`); agents pin both during the transition.

### Revocation and expiry

- Revoke (UI, permission `node.credentials.manage`): credential → `revoked`, the node's active connection is aborted immediately, any later challenge fails. Audited.
- Disabling a node: same effect without deleting the credential.
- Enrollment tokens expire (30 min by default). Session JWTs: 15 min. Node credentials: automatic rotation; optional hard expiry by policy (`expires_at`).
- Re-enrolling a node (e.g. Windows reinstall) = a new enrollment token for the same `Node`; the previous credential is revoked.

### Agent local storage

```
%ProgramData%\Pulsar\Agent\        ACL: SYSTEM + Administrators (inheritance disabled)
  config\agent.json                 panel URL, nodeId, credentialId, pinned panel keys
  state\servers\{id}.json           per-server spec; secrets encrypted with DPAPI (LocalMachine)
  state\outbox\                     OperationCompleted results awaiting ack
  steamcmd\                         managed SteamCMD installation
  console-logs\{shortId}\           rotated console logs (outside the server directory)
  backups\{shortId}\                local backups
  logs\                             agent logs (Serilog, rolling)
<ServersRoot>\{shortId}\            server directory (configurable, e.g. D:\PulsarServers)
```

The node's private key is not in this tree: it lives in the CNG/TPM key store.
