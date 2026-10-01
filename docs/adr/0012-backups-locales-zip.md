# ADR-0012 — Backups zip almacenados localmente en el nodo (v1)

Estado: Propuesta

## Contexto
Backups por servidor con crear/listar/descargar/restaurar/eliminar, sin bloquear el sistema y sin infraestructura externa.

## Decisión
- Formato zip (Zip64) generado en streaming por el agent, con globs include/exclude del manifest (excluyendo por defecto el contenido reinstalable por SteamCMD).
- Almacenamiento en `%ProgramData%\Pulsar\Agent\backups\<shortId>` (ruta configurable por nodo).
- Ejecutado como operación cancelable con progreso; SHA-256 registrado; restauración solo con el servidor Offline y validación anti zip-slip.

## Alternativas
- tar.gz / 7z: zip se abre nativamente en el Explorador de Windows y está en la BCL.
- Almacenamiento remoto (S3-compatible): valioso, pero post-v1 (la columna `storage` ya lo prevé).
- Snapshots VSS para consistencia con el servidor en marcha: post-v1.

## Consecuencias
- Si se pierde el disco del nodo, se pierden sus backups (documentado; remoto en post-v1).
- Un backup con el servidor encendido puede capturar archivos a medio escribir: la UI lo advierte.
