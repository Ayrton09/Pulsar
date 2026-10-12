# ADR-0003 — Per-node asymmetric identity, proof of possession and signed commands

Status: Proposed

## Context
The agent runs processes: a flaw in panel ↔ agent authentication amounts to remote code execution. A unique identity per node, rotation, revocation and replay protection are required. Proxies that terminate TLS make mTLS fragile.

## Decision
- Each node generates an ECDSA P-256 key pair through CNG as a non-exportable machine key (TPM through the Platform Crypto Provider when available).
- Enrollment with a single-use token (256 bits, SHA-256 in the database, 30 minutes) and pinning of the panel signing key by fingerprint.
- Every connection: nonce challenge → signature covering nodeId, credentialId, nonce and panel origin → 15-minute session JWT valid only for agent endpoints.
- Every panel → agent command travels in an envelope signed by the panel, with NodeId, SessionId, sequence (anti-replay window), expiry and CommandId.
- Immediate revocation (database state + aborted connection); automatic rotation every 90 days.

## Alternatives
- Per-node API key (symmetric secret): simpler, but the secret exists on both sides and is exportable.
- mTLS with an own CA: strong, but breaks behind proxies that terminate TLS and requires running a CA/CRL. Kept as a possible future optional mode reusing the same key (added in Phase 16 without a CA: see the addendum).

## Consequences
- Robust even if TLS or a proxy is compromised.
- Clocks only matter as a secondary defense (the main anti-replay is session + sequence).
- Losing the panel signing key forces re-enrolling every node: it must be part of the panel backups.

## Addendum: optional client certificates (Phase 16, 2026-10-12)
- Each node presents its node key in a certificate it signs itself; the panel checks the key against the node's credential, never a chain. No CA, no CRL: revocation stays the credential's.
- A separate agents' port (Kestrel endpoint `Agents`) asks for the certificate and serves only the agent protocol, so browsers never see a certificate request and the main endpoint can stay behind a TLS-terminating proxy.
- A session opened there is bound to that key: its token is useless without the key.
- Agents move there on their own (a signed command; the old address stays as fallback). A switch, off by default, makes the main endpoint send agents there with a signed refusal.
- Details: docs/architecture/03, "Client certificates".
