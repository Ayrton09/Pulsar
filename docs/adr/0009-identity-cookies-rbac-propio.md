# ADR-0009 — ASP.NET Core Identity con cookies y RBAC propio con scopes

Estado: Propuesta

## Contexto
Se necesita autenticación robusta (hash, lockout, 2FA) y un RBAC granular por servidor/nodo, sin `Admin = true`.

## Decisión
- ASP.NET Core Identity (`IdentityUserContext`, sin roles de Identity) para usuarios, contraseñas, lockout y TOTP.
- Endpoints propios de login/logout (no `MapIdentityApi`). Autenticación por cookie `HttpOnly; Secure; SameSite=Strict` + antiforgery; sin tokens en el navegador.
- RBAC propio: permisos como catálogo en código; roles en BD con scope (global/nodo/servidor); asignaciones en `user_roles`, `node_access` y `server_access` con FKs que fijan el scope del rol.
- Sin reglas deny en v1. Rol de sistema Administrator con todos los permisos, anti-escalada y protección del último admin.

## Alternativas
- Implementar auth desde cero: más superficie de bugs en la parte más sensible.
- Roles/claims de Identity: modelo plano, sin scopes por recurso.
- JWT en el navegador: peor frente a XSS e innecesario para una SPA del mismo origen.

## Consecuencias
- Las entidades de usuario viven en Infrastructure (Identity); el dominio solo conoce `UserId`.
- Un futuro SSO (OIDC) encaja como login externo de Identity.
