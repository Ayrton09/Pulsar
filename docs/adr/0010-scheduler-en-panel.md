# ADR-0010 — Scheduler en el panel sobre PostgreSQL, sin Quartz ni Hangfire

Estado: Propuesta

## Contexto
Se necesitan tareas programadas tipadas (restart, backup, update, comando de consola) con cron y zona horaria, sin infraestructura extra.

## Decisión
`BackgroundService` en el panel: tareas con `next_run_at` (UTC), selección con `FOR UPDATE SKIP LOCKED`, espera hasta la próxima ejecución (se despierta al cambiar tareas) y cálculo de ocurrencias con la librería Cronos (zonas IANA y DST). Las ejecuciones se registran y auditan. Las ejecuciones perdidas durante una caída del panel se marcan `skipped`.

## Alternativas
- Quartz.NET / Hangfire: maduros, pero añaden tablas, conceptos y dependencias que no necesitamos.
- Scheduler en el agent: sobrevive a caídas del panel, pero duplica lógica y dificulta la auditoría y la UI.

## Consecuencias
- Si el panel cae, no se ejecutan tareas (documentado).
- Ya es seguro para varias instancias si algún día se escala el panel.
