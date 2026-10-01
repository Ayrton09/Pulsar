# ADR-0001 — Monolito modular con panel de instancia única

Estado: Propuesta

## Contexto
Pulsar debe instalarse fácilmente (incluso en un PC doméstico) y ser mantenible por contribuidores. Los requisitos prohíben microservicios e infraestructura innecesaria (Redis, colas, Kubernetes).

## Decisión
Un único proceso ASP.NET Core (`Pulsar.Api`) con módulos internos (Access, Nodes, Templates, Servers, Operations, Files, Backups, Scheduling, Audit, Realtime, Settings) organizados como carpetas dentro de las capas Domain/Application/Infrastructure. Una sola instancia del panel en v1. El estado volátil (conexiones de agents, métricas en vivo, suscripciones de consola) vive en memoria.

## Alternativas
- Microservicios: complejidad operativa sin beneficio a esta escala.
- Proyecto por módulo: límites más fuertes, pero multiplica csproj y fricción para contribuidores; se puede extraer más adelante si un módulo lo necesita.
- Multi-instancia desde el día 1: exige backplane SignalR (Redis) y estado distribuido.

## Consecuencias
- Instalación simple: un ejecutable + PostgreSQL.
- Sin alta disponibilidad del panel. Si cae, los servidores siguen funcionando, pero no hay UI ni scheduler.
- Para escalar horizontalmente en el futuro: backplane SignalR y mover el estado en memoria a un almacén compartido. El scheduler ya usa `FOR UPDATE SKIP LOCKED` y no necesita cambios.
