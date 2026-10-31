# Connecting a billing system

Pulsar 1.0.24 and later can be driven by a billing system (WHMCS, Blesta, Paymenter or your own) through its REST API:
when a customer pays, the billing system creates their account and their server; when an invoice is overdue, it suspends
the server; when the service is cancelled, it deletes it. This guide lists the calls such a module makes. The API is
described in full at `/api/v1/openapi.json` on your panel.

## The key

Create an API key from **My account → API keys** while signed in as an administrator, and give it to the billing
module. Requests carry it as `Authorization: Bearer pulsar_…`. Keep it like a password: it acts as you.

A key can be narrowed to some permissions, but a module that gives servers to customers needs a key that holds, on
the servers it creates, everything an owner may do (giving someone more than you hold is refused): use an
administrator's key without a permission list, or one that lists every `server.*` permission and `user.view`,
`user.manage`, `node.view`.

## Ids

Every user and server can carry an **external id**: what the billing system calls it (`whmcs:client-42`,
`service-7`), 1 to 128 letters, digits and `. _ - : @`. It is unique: creating a second user or server with the same
one answers `409` (`user.external_id_taken`, `server.external_id_taken`), so a request retried after a timeout never
makes two. Find them again with:

```http
GET /api/v1/users?externalId=whmcs:client-42
GET /api/v1/servers?externalId=whmcs:service-7
```

Each answers a list with that one user or server, or an empty list. Change or remove an external id with
`PUT /api/v1/users/{id}/external-id` or `PUT /api/v1/servers/{id}/external-id` and `{"externalId": "…"}` (or `null`).

## A new service

1. **The customer**, unless they have an account already:

   ```http
   POST /api/v1/users
   {"userName": "alice", "password": "…", "email": "alice@example.com", "displayName": "Alice", "externalId": "whmcs:client-42"}
   ```

2. **The server**, on the node and game of the plan; its ports are free allocations of the node unless you choose them:

   ```http
   POST /api/v1/servers
   {"nodeId": "…", "templateId": "…", "name": "Alice's Valheim", "externalId": "whmcs:service-7"}
   ```

   `GET /api/v1/templates` lists the games (`key` and `version`; take the newest of the key you sell). The server
   installs by itself; `GET /api/v1/servers/{id}` says when it is `installed`.

3. **The plan's caps**: `PUT /api/v1/servers/{id}/limits` with `{"cpuPercent": 100, "memoryMb": 4096, "databases": 1}`.

4. **Give it to the customer**: `PUT /api/v1/servers/{id}/owner` with `{"userId": "…"}`. They run it as theirs (start,
   stop, console, files, backups, schedules, settings of the game they may change, deleting it) but never change its
   caps, its name, its ports or its suspension. `{"userId": null}` takes it back.

Instead of steps 2 to 4, a plan can be a **quota**: `PUT /api/v1/users/{id}/quota` with
`{"maxServers": 2, "maxMemoryMb": 8192, "maxCpuPercent": 200, "nodeIds": ["…"]}` lets the customer create their own
servers within it, from the panel (docs/architecture/02, "Quotas").

## Overdue, paid, upgraded, cancelled

| When | Call |
|---|---|
| An invoice is overdue | `POST /api/v1/servers/{id}/suspend` with `{"reason": "Invoice 1234 is overdue"}`: the server stops and its customer sees only the reason |
| It is paid | `POST /api/v1/servers/{id}/unsuspend`: it stays stopped until they start it |
| The plan changes | `PUT /api/v1/servers/{id}/limits` (applied at its next start), or the quota |
| The service is cancelled | `DELETE /api/v1/servers/{id}`: its files and the backups on its node go |
| The customer leaves | `DELETE /api/v1/users/{id}` (their remaining servers stay, nobody's) |
| The customer forgot their password | `POST /api/v1/users/{id}/password` |

Every call is in the audit log as the key's user, with the key named.
