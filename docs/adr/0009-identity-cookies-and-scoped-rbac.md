# ADR-0009 — ASP.NET Core Identity with cookies and an own scoped RBAC

Status: Proposed

## Context
Robust authentication (hashing, lockout, 2FA) and granular RBAC per server/node are required, without `Admin = true`.

## Decision
- ASP.NET Core Identity (`IdentityUserContext`, no Identity roles) for users, passwords, lockout and TOTP.
- Own login/logout endpoints (not `MapIdentityApi`). Cookie authentication `HttpOnly; Secure; SameSite=Strict` + antiforgery; no tokens in the browser.
- Own RBAC: permissions as a catalog in code; roles in the database with a scope (global/node/server); assignments in `user_roles`, `node_access` and `server_access` with foreign keys that pin the role scope.
- No deny rules in v1. A system Administrator role with every permission, anti-escalation rules and last-admin protection.

## Alternatives
- Implementing authentication from scratch: more room for bugs in the most sensitive part.
- Identity roles/claims: a flat model without per-resource scopes.
- JWT in the browser: worse against XSS and unnecessary for a same-origin SPA.

## Consequences
- User entities live in Infrastructure (Identity); the domain only knows `UserId`.
- Future SSO (OIDC) fits as an Identity external login.
