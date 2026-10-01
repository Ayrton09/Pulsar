# 08 — Modelo de seguridad y riesgos principales

> Estado: **propuesta para revisión** · Cubre los puntos 13–14.

## Fronteras de confianza

```
[Internet / jugadores] ──UDP/TCP──► game servers (cuenta por servidor, Job)
[Navegador] ──HTTPS + cookie──► Panel ──► PostgreSQL
                                   ▲
[Agent (SYSTEM)] ──WSS saliente, PoP + comandos firmados──┘
[Plugins / mods / archivos subidos] = código NO confiable dentro del game server
```

Supuestos explícitos:

1. **El panel es el plano de control.** Quien controle el panel con permisos de administrador controla los nodos *dentro de las operaciones soportadas*. Pulsar limita qué puede pedir el panel, pero no puede proteger a un nodo de un panel totalmente comprometido.
2. **Un game server ejecuta código no confiable** (plugins de SourceMod/CounterStrikeSharp, Lua de GMod, DLLs subidas). Quien puede subir archivos y arrancar un servidor puede ejecutar código **como la cuenta de ese servidor**. Esto es inherente al hosting de juegos; el objetivo es que ese código no salga de su caja (cuenta + ACL + Job).
3. El administrador local del nodo es de plena confianza.

## Controles por capa

### Usuarios y web

- ASP.NET Core Identity: hash de contraseñas (PBKDF2 por defecto de Identity), lockout, security stamp, TOTP 2FA (exigible por rol en ajustes). Recuperación con recovery codes.
- Setup inicial protegido: si no hay usuarios, `/setup` exige un **token de un solo uso impreso en el log/consola del panel** al arrancar (evita que otro en la red cree el primer admin antes que tú).
- Cookies `HttpOnly; Secure; SameSite=Strict`, antiforgery en mutaciones, CSP estricta, `X-Content-Type-Options`, `Referrer-Policy`, HSTS.
- Rate limiting integrado (`Microsoft.AspNetCore.RateLimiting`) en login, setup, enrolamiento y challenge de agents.
- Autorización por recurso en **todos** los endpoints y métodos de hub (test que lo garantiza), IDs de recursos ajenos → 404.
- Anti-escalada en RBAC (doc 02).

### Panel ↔ Agent

- TLS obligatorio (el agent rechaza `http://` salvo `localhost` en modo desarrollo).
- Identidad por nodo con clave no exportable (TPM si existe), prueba de posesión por challenge, JWT de 15 min, revocación inmediata, rotación.
- Comandos firmados por el panel, clave del panel fijada, anti-replay (sesión + secuencia + expiración), validación fuerte antes de ejecutar.
- No existe un comando de shell. El ejecutable siempre sale del manifest y del directorio del servidor.

### Nodo

- Agent sin puertos de escucha.
- Cuentas por servidor sin pertenencia a `Users`, sin logon interactivo/RDP/red.
- ACLs: cada servidor solo ve su directorio; los directorios del agent (`%ProgramData%\Pulsar\Agent`) solo SYSTEM/Administrators.
- Jobs con restricciones de UI y sin breakaway.
- Handles heredados explícitos (`PROC_THREAD_ATTRIBUTE_HANDLE_LIST`).

### File manager (crítico)

El file manager del agent corre en un proceso SYSTEM operando sobre directorios **escribibles por código no confiable**. Defensa en profundidad:

1. **Validación léxica** de la ruta relativa recibida: rechazar raíces, unidades (`C:`), UNC (`\\server`), rutas de dispositivo (`\\?\`, `\\.\`), `..`, `:` (ADS), nombres reservados (`CON`, `NUL`, `AUX`, `PRN`, `COM1-9`, `LPT1-9`, también con extensión), puntos/espacios finales, caracteres de control, longitudes excesivas.
2. **Normalización** con `Path.GetFullPath(Path.Join(root, rel))` y comprobación de prefijo con separador final (`C:\srv\a\` no debe aceptar `C:\srv\ab`) y comparación ordinal case-insensitive.
3. **Reparse points**: el código del juego puede crear un *junction* (no requiere privilegios) que apunte a `C:\`. Toda operación abre primero un handle y verifica con `GetFinalPathNameByHandle` que el destino real sigue dentro de la raíz; luego opera **sobre ese handle** (sin TOCTOU entre comprobación y uso). La enumeración no sigue reparse points; se muestran como enlaces no navegables.
4. **Impersonación**: las operaciones de archivo se ejecutan impersonando la cuenta del servidor (`WindowsIdentity.RunImpersonated` con el token de la cuenta). Aunque las capas 1–3 fallaran, el SO aplica las ACL de esa cuenta: no puede leer fuera de su directorio. Esto también neutraliza hard links a archivos ajenos (la ACL vive en el archivo, no en el enlace).
5. Límites: tamaño de lectura/edición inline (1 MiB), tamaño de subida configurable, cuota de disco opcional.
6. Escritura atómica (temporal + rename) y auditoría de borrados/renombrados/subidas.

Tests de path traversal: tabla extensa de entradas maliciosas (más de 50 casos Windows específicos) + tests de integración con junctions, symlinks y hard links reales.

### Backups y restauración

- Creación: no sigue reparse points (si no, un junction a `C:\Users` exfiltraría datos dentro del zip); lectura impersonando a la cuenta del servidor.
- Restauración: cada entrada del zip pasa por el mismo validador (anti zip-slip), sin crear reparse points, escritura impersonada, solo con el servidor `Offline`.
- Descarga de backups solo con `server.backups.view` y vía ticket de transferencia.

### Manifests

Importación privilegiada, pasos cerrados, regex con tiempo acotado, variables tipadas, argv sin shell, encoders por formato de configuración (doc 04).

### Secretos

| Secreto | Dónde | Protección |
|---|---|---|
| Contraseñas de usuarios | `users.password_hash` | Hash de Identity |
| Variables secretas (GSLT, RCON) | `server_variables.protected_value` | ASP.NET Core Data Protection |
| Clave de firma del panel | `panel_signing_keys` | Data Protection |
| Key ring de Data Protection | Disco del panel | DPAPI (LocalMachine) en Windows; certificado en Linux o para portabilidad |
| Clave privada del nodo | Almacén CNG/TPM | No exportable |
| Specs con secretos en el nodo | `state\servers\*.json` | DPAPI LocalMachine + ACL |
| Tokens de enrolamiento / tickets | BD | Solo SHA-256 |

- Las APIs devuelven secretos como `"***"` (write-only). Serilog con filtros que redactan propiedades conocidas y nunca destructura DTOs completos. Ni `Authorization` ni cookies en logs de requests.
- **Consecuencia operativa importante:** un backup de PostgreSQL sin el key ring de Data Protection no permite descifrar los secretos. La guía de backup del panel lo documentará.
- Limitación aceptada: un secreto pasado por línea de comandos (p. ej. GSLT con `+sv_setsteamaccount`) es visible para administradores locales del nodo. Si un juego permite pasarlo por archivo, el manifest debería preferirlo.

### Auditoría

Append-only desde la aplicación, sin secretos, con IP y actor. Acciones mínimas: login correcto/fallido (agregado por ventana para no inundar), logout, 2FA cambiado, usuario/rol/acceso creado-modificado-borrado, nodo creado/enrolado/revocado/rotado, servidor creado/borrado, start/stop/restart/kill, comando de consola (redactado), archivo editado/subido/borrado/renombrado, backup creado/restaurado/borrado/descargado, tarea programada creada/modificada y sus ejecuciones, plantilla importada, ajustes cambiados.

### Cadena de suministro y releases

- Central Package Management + lock files de NuGet, `npm ci` con lockfile, Dependabot, CodeQL, dependency review en PRs.
- Releases construidos en CI con attestations de procedencia (GitHub artifact attestations) y checksums SHA-256 publicados. Firma Authenticode de binarios Windows cuando el proyecto disponga de certificado (SmartScreen lo agradecerá).
- Sin auto-actualización del agent en v1: un canal de auto-update es un canal de ejecución remota y requiere diseño propio (firma de paquetes verificada por el agent).

## Amenazas (resumen STRIDE)

| # | Amenaza | Mitigación principal |
|---|---|---|
| T1 | Suplantar un agent con clave robada | Clave no exportable/TPM, revocación inmediata, fingerprint visible, rotación |
| T2 | Token de enrolamiento filtrado | Un solo uso, 30 min, hash en BD, nodo visible con hostname/fingerprint |
| T3 | MITM o proxy comprometido inyecta comandos | Comandos firmados + clave del panel fijada |
| T4 | Replay de comandos | Sesión + secuencia con ventana + expiración + idempotencia por CommandId |
| T5 | Inyección de argumentos/comandos de consola vía variables | Tipado, regex obligatoria, sin `-`/`+` iniciales, argv sin shell, encoders |
| T6 | Path traversal / junctions / ADS / dispositivos | Validación léxica + handle final path + impersonación + ACL |
| T7 | Zip slip en restore | Validador por entrada |
| T8 | Plugin malicioso escala a SYSTEM o a otros servidores | Cuenta por servidor, ACL, Job, sin Users group |
| T9 | Manifest malicioso | Importación privilegiada, pasos cerrados, revisión de capacidades |
| T10 | Escalada de privilegios en RBAC / IDOR | Evaluador único por recurso, anti-escalada, tests de matriz de autorización |
| T11 | XSS / CSRF / clickjacking | React escapado, CSP, SameSite + antiforgery, frame-ancestors |
| T12 | Fuerza bruta de login | Lockout, rate limiting, 2FA |
| T13 | Secuencias de escape maliciosas en consola | xterm sin addon de portapapeles, links solo http/https |
| T14 | DoS por flood de consola/subidas | Coalescing, ring buffers acotados, límites de mensaje y tamaño |
| T15 | Fuga de secretos en logs/auditoría | Redacción, secretos write-only, prefijos sensibles en consola |
| T16 | Primer admin creado por un tercero | Token de setup impreso localmente |
| T17 | Repudio | Auditoría con actor, IP, correlación |

## Riesgos principales del proyecto

| Riesgo | Prob. | Impacto | Mitigación |
|---|---|---|---|
| R1. Comportamiento de consola de `cs2.exe`/`srcds.exe`/`steamcmd.exe` en Windows (stdout vs API de consola) | Alta | Alto | Spike Fase 0 con ConPTY y pipes antes de diseñar la UI de consola sobre supuestos. Plan B: RCON para comandos. |
| R2. Complejidad de cuentas por servidor (perfiles, window stations, derechos LSA, ediciones de Windows, antivirus) | Media | Alto | Spike Fase 0; implementación aislada en `Agent.Windows`; tests de integración en Windows Server y Windows 11. |
| R3. Reiniciar/actualizar el agent detiene los servidores | Cierta | Medio | Documentado; parada ordenada; auto-start; shim post-v1. |
| R4. Fragilidad de SteamCMD (formato de salida, errores transitorios, bootstrap) | Alta | Medio | Parser tolerante, reintentos acotados, logs completos visibles. |
| R5. Fricción de instalar PostgreSQL en Windows para usuarios domésticos | Alta | Medio | Guía paso a paso, comprobación de conexión con mensajes claros, instalador que detecta un PostgreSQL local. Sin fallback a SQLite (decisión explícita). |
| R6. Panel de instancia única (sin HA) | Cierta | Medio | Aceptado para v1; scheduler ya seguro para multi-instancia (`FOR UPDATE SKIP LOCKED`); backplane SignalR sería el cambio necesario. |
| R7. Pérdida del key ring de Data Protection o de la clave del panel | Baja | Alto | Documentar backup; la clave del panel perdida obliga a re-enrolar nodos. |
| R8. Deriva de versiones panel/agent | Media | Medio | Versionado de protocolo, capabilities, soporte N-1. |
| R9. Windows Defender ralentiza instalaciones de 60 GB | Alta | Bajo | Documentar exclusiones; **no** añadirlas automáticamente (decisión de seguridad del admin). |
| R10. Alcance (feature creep) | Alta | Alto | Roadmap por fases con "definición de hecho"; nada fuera del flujo v1 hasta cerrarlo. |
| R11. Windows 10 fuera de soporte (fin de soporte en oct. 2025) | Cierta | Bajo | Soporte "best effort" para Windows 10 22H2; foco en Windows 11 y Server 2022/2025. |
