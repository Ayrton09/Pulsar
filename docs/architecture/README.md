# Arquitectura de Pulsar v1

> Estado: **borrador para revisión**. Nada de esto está implementado todavía. Las decisiones difíciles de revertir están en [`../adr`](../adr) con estado *Propuesta* hasta que se aprueben.

## Resumen en diez líneas

1. **Monolito modular** ASP.NET Core (.NET 10) + PostgreSQL + SPA React servida por el propio panel. Una instancia, sin Redis ni colas externas.
2. **Pulsar Agent** como servicio Windows en cada nodo; **el agent inicia** una conexión WebSocket (SignalR) saliente hacia el panel. Los nodos no abren puertos para Pulsar.
3. **Identidad por nodo** con clave ECDSA P-256 no exportable (TPM si existe); autenticación por challenge firmado; **todos los comandos del panel van firmados** y con anti-replay.
4. **Operaciones tipadas** únicamente. No existe ejecución de shell en ninguna API.
5. **Juegos como manifests declarativos** con un conjunto cerrado de pasos; argumentos renderizados como argv sin shell y variables tipadas con validación estricta.
6. **Windows Job Objects** por servidor (`KILL_ON_JOB_CLOSE`, proceso creado dentro del Job) → sin procesos huérfanos.
7. **ConPTY o pipes** según el manifest, para capturar consolas de juegos Windows que no usan stdout.
8. **Una cuenta Windows por servidor**: el código de plugins nunca corre como SYSTEM ni ve otros servidores.
9. **RBAC con scopes** (global ⊃ nodo ⊃ servidor), permisos definidos en código, anti-escalada.
10. **Tiempo real por eventos** (agent → panel → navegador); métricas en memoria, no en PostgreSQL.

## Documentos

| # | Documento | Puntos del encargo |
|---|---|---|
| 01 | [Arquitectura, repositorio y dependencias](01-arquitectura-y-repositorio.md) | 1, 2, 3, 4 |
| 02 | [Entidades, permisos y esquema PostgreSQL](02-dominio-y-base-de-datos.md) | 5, 6 |
| 03 | [Protocolo Panel ↔ Agent y autenticación de nodos](03-protocolo-panel-agent.md) | 7, 8 |
| 04 | [Game Manifests](04-game-manifests.md) | 9 |
| 05 | [Ciclo de vida de un servidor](05-lifecycle-servidor.md) | 10 |
| 06 | [Procesos en Windows: Job Objects, ConPTY, aislamiento](06-windows-procesos.md) | 11 |
| 07 | [Frontend](07-frontend.md) | 12 |
| 08 | [Seguridad y riesgos](08-seguridad-y-riesgos.md) | 13, 14 |
| 09 | [Roadmap técnico](09-roadmap.md) | 15 |

## Decisiones difíciles de cambiar después

| ADR | Decisión | Por qué cuesta revertirla |
|---|---|---|
| [0001](../adr/0001-monolito-modular-instancia-unica.md) | Monolito modular, panel de instancia única | Estado en memoria (conexiones, métricas) asume un proceso. |
| [0002](../adr/0002-conexion-iniciada-por-agent-signalr.md) | Agent → Panel por SignalR/WebSockets | Todos los agents desplegados hablan este protocolo. |
| [0003](../adr/0003-identidad-de-nodo-y-comandos-firmados.md) | Claves por nodo + PoP + comandos firmados | Cambiar identidad = re-enrolar todos los nodos. |
| [0004](../adr/0004-uuidv7.md) | UUIDv7 generados en la aplicación | IDs en BD, URLs, protocolo y nombres de recursos. |
| [0005](../adr/0005-cuenta-windows-por-servidor.md) | Cuenta Windows por servidor, agent como SYSTEM | ACLs y layout de disco de todos los servidores existentes. |
| [0006](../adr/0006-kill-on-job-close.md) | Servidores mueren con el agent (v1) | Afecta expectativas operativas y diseño del agent. |
| [0007](../adr/0007-consola-conpty-pipes.md) | Consola ConPTY/pipes; no se guarda en PostgreSQL | Formato de stream y UX de consola. |
| [0008](../adr/0008-manifests-declarativos.md) | Manifests declarativos, pasos cerrados, pinning de versión | La comunidad escribe manifests contra este formato. |
| [0009](../adr/0009-identity-cookies-rbac-propio.md) | Identity + cookies; RBAC propio con scopes | Tablas de usuarios y modelo de permisos. |
| [0010](../adr/0010-scheduler-en-panel.md) | Scheduler en el panel sobre PostgreSQL | Semántica de tareas si el panel cae. |
| [0011](../adr/0011-secretos-data-protection.md) | Secretos con Data Protection | Key ring imprescindible para restaurar el panel. |
| [0012](../adr/0012-backups-locales-zip.md) | Backups zip locales al nodo (v1) | Formato de backup que los usuarios acumulan. |
| [0013](../adr/0013-licencias.md) | AGPL-3.0 para el core, MIT para manifests y schema | Re-licenciar requiere consentimiento de todos los contribuidores. |

## Preguntas abiertas para el mantenedor

1. **Idioma público del proyecto.** Recomiendo inglés para código, comentarios, commits, issues y documentación pública (alcance de contribuidores), con la UI en inglés y español desde el día 1. Estos documentos de diseño están en español para la revisión; se traducirían antes de publicar.
2. **Licencia de los manifests** (ADR-0013): ¿MIT para `games/` y `schemas/`?
3. **DCO vs CLA**: recomiendo DCO (`Signed-off-by`), más ligero; un CLA solo tiene sentido si se prevé re-licenciar.
4. **Panel en Linux**: el panel no tiene nada específico de Windows; propongo soportarlo también en Linux sin coste extra (el agent sigue siendo Windows-first). ¿De acuerdo?
5. **Windows 10**: fuera de soporte desde octubre de 2025. Propuesta: "best effort" en 22H2, soporte pleno en Windows 11 y Server 2019/2022/2025.
