# ADR-0002 — Agent-initiated persistent connection over SignalR (WebSockets)

Status: Proposed

## Context
Nodes often sit behind NAT, with dynamic IPs and Windows Firewall. Opening a port per agent is an installation hurdle and an attack surface. Many Windows users publish the panel behind IIS/ARR, nginx, Caddy or Cloudflare.

## Decision
The agent opens an outbound WebSocket connection (TLS) to the `/agent/v1/hub` hub using the .NET SignalR client, WebSockets transport only, JSON protocol. Command → ack through *client results*; long work is reported through events carrying an `OperationId`. Large transfers use HTTPS requests opened by the agent with single-use tickets. Major version in the path, additive changes within a version, capabilities announced in the `Hello`.

## Alternatives
- gRPC bidirectional streaming: better IDL, but needs HTTP/2 end to end; problematic behind IIS/ARR and common proxies.
- Raw WebSocket: reconnect, keep-alive and correlation would have to be reimplemented.
- Panel → agent (agent listening): requires open ports on every node.

## Consequences
- Zero inbound ports for Pulsar on nodes.
- The same real-time stack for browsers and agents.
- The contract is C# (method names + DTOs): it needs versioning discipline and N-1 compatibility tests.
