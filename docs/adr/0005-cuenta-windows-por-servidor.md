# ADR-0005 — Agent como LocalSystem y cada game server con su propia cuenta Windows

Estado: Propuesta

## Contexto
Los game servers ejecutan código no confiable (plugins, mods, Lua). El agent necesita privilegios para crear cuentas, ACLs, reglas de firewall y lanzar procesos con otra identidad.

## Decisión
- El servicio del agent corre como LocalSystem.
- Ningún game server corre como SYSTEM: cada servidor tiene una cuenta local `pulsar_<shortId>`, fuera del grupo Users, con solo derecho de logon batch, logons interactivo/RDP/red denegados, y ACL de Modify únicamente sobre su directorio.
- SteamCMD corre con su propia cuenta `pulsar_steamcmd`.
- Las operaciones de archivo y backup del agent se ejecutan impersonando la cuenta del servidor.
- Un modo "usuario actual" existe solo para desarrollo con el agent en consola, con advertencias explícitas.

## Alternativas
- Todo bajo una cuenta de servicio: sin aislamiento entre servidores.
- Una cuenta común de baja prioridad: protege el SO pero no a un servidor de otro.
- AppContainer: aislamiento más fuerte, pero rompe red y rutas de muchos juegos.

## Consecuencias
- Un plugin comprometido queda confinado a su servidor.
- Complejidad real: derechos LSA, perfiles, window station/desktop, contraseñas cifradas con DPAPI. Requiere spike (Fase 0).
- No soportado en controladores de dominio.
- Hacerlo después sería costoso (ACLs y layout de disco de servidores existentes); por eso se decide ahora.
