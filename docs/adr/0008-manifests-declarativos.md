# ADR-0008 — Manifests declarativos con pasos cerrados y fijación de versión

Estado: Propuesta

## Contexto
La comunidad debe poder añadir juegos sin tocar el core, pero un manifest decide qué se descarga y ejecuta en los nodos.

## Decisión
- Manifest JSON con `schemaVersion`, `key` y `version` (semver), validado con JSON Schema y reglas semánticas por la librería compartida `Pulsar.Manifests` en panel y agent.
- Conjunto cerrado de pasos (`steamcmd`, `ensureDirectory`, `writeFileIfMissing`; `download` con sha256 obligatorio y `extractArchive` post-v1). Sin shell ni scripts.
- Plantillas sin lógica: solo placeholders y un condicional; cada elemento es exactamente un argv.
- Variables tipadas; los strings usados en argumentos o configs requieren pattern o enum.
- Los servidores quedan fijados a `key@version`; actualizar una plantilla es una acción explícita.
- Importar manifests requiere `template.manage`.

## Alternativas
- Scripts (PowerShell/Lua) en manifests: máxima flexibilidad, pero ejecución arbitraria.
- Juegos en código C# (plugins): exige compilar y acopla la comunidad al core.

## Consecuencias
- Algunos juegos exóticos necesitarán nuevos tipos de paso, discutidos y revisados en el core.
- El formato v1 es un contrato público: los cambios incompatibles implican `schemaVersion` 2 manteniendo soporte del 1.
