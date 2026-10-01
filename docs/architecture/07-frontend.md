# 07 — Arquitectura del frontend

> Estado: **propuesta para revisión** · Cubre el punto 12.

## Stack

| Necesidad | Elección | Motivo |
|---|---|---|
| Build | Vite + React + TypeScript (strict) | Requisito. Sin SSR. |
| Routing | React Router (modo SPA / librería) | Estándar, sin servidor Node. |
| Estado de servidor | TanStack Query | Caché, invalidación, deduplicación; los eventos SignalR escriben en la misma caché. |
| Estado de UI | `useState`/context | No hace falta Redux/Zustand. |
| Cliente API | `openapi-typescript` (tipos) + `openapi-fetch` (cliente ~mínimo) | Tipos generados del OpenAPI del backend; un check de CI falla si el cliente está desactualizado. |
| Tiempo real | `@microsoft/signalr` | Mismo protocolo que el backend. |
| Consola | `@xterm/xterm` + `@xterm/addon-fit`, `@xterm/addon-search`, `@xterm/addon-web-links` | Requisito. No se carga el addon de portapapeles (OSC 52). |
| Estilos | Tailwind CSS v4 | Sin runtime CSS-in-JS. |
| Componentes | Propios estilo shadcn (copiados al repo) sobre primitivas Radix | Accesibilidad sin depender de una librería de componentes completa. |
| Formularios | react-hook-form + zod | Validación de cliente (el servidor siempre revalida). |
| i18n | i18next (en + es desde el día 1) | Añadirlo después obliga a tocar cada pantalla. |
| Gráficas | uPlot (solo para CPU/RAM en vivo) | Muy ligero y rápido para series temporales. |
| Tests | Vitest + Testing Library; Playwright para E2E | Requisito. |

Regla: cada dependencia nueva de npm se justifica en el PR. Lockfile versionado, `npm ci` en CI, Dependabot.

## Estructura

```
src/Pulsar.Web/
├─ index.html
├─ vite.config.ts            proxy dev: /api y /hubs (ws) → backend
├─ src/
│  ├─ app/                   router, providers (Query, Realtime, Auth, i18n), layout, error boundaries
│  ├─ components/ui/         Button, Dialog, Table, Tabs, Badge, Toast… (sin lógica de negocio)
│  ├─ lib/
│  │  ├─ api/                cliente generado + wrapper (ProblemDetails → errores tipados, CSRF)
│  │  ├─ realtime/           conexión SignalR única, suscripciones con conteo de referencias
│  │  ├─ permissions/        usePermission('server.start', serverId)
│  │  └─ format/             bytes, duraciones, fechas (UTC → zona local del navegador)
│  ├─ features/
│  │  ├─ auth/               login, setup inicial, 2FA
│  │  ├─ dashboard/
│  │  ├─ servers/            lista, creación (wizard), páginas por pestaña
│  │  ├─ console/            Terminal (xterm), CommandInput, hooks de stream
│  │  ├─ files/              árbol, editor de texto (CodeMirror 6, carga diferida), subida/descarga
│  │  ├─ backups/  schedules/  network/  startup/  activity/
│  │  ├─ nodes/              lista, alta/enrolamiento, páginas por pestaña
│  │  └─ admin/              users, roles, templates, audit, settings
│  └─ locales/en, es
└─ e2e/                      Playwright
```

Cada `feature` contiene `api.ts` (queries/mutations), `components/`, `routes.tsx` y tests junto al código.

## Rutas

```
/login  /setup
/                                  Dashboard
/servers                           /servers/new
/servers/:id/{overview|console|files|backups|schedules|network|startup|settings|activity}
/nodes                             /nodes/new
/nodes/:id/{overview|servers|system|storage|network|activity|settings}
/admin/{users|roles|templates|audit|settings}
/account                           perfil, contraseña, 2FA
```

Las páginas de servidor y nodo usan un layout con cabecera fija (nombre, estado, acciones de power, CPU/RAM en vivo) y pestañas. Las pestañas sin permiso no se muestran.

## Tiempo real

- **Una conexión SignalR por pestaña** a `/hubs/panel`, autenticada por cookie.
- Suscripción explícita por recurso: `useServerChannel(serverId)` llama a `JoinServer(serverId)` (el hub autoriza `server.view`) y `Leave` al desmontar; conteo de referencias para no duplicar.
- Eventos → `queryClient.setQueryData(...)`: el estado del servidor, las métricas y el progreso de operaciones se actualizan **sin polling**.
- Al reconectar: re-join de los grupos activos e invalidación de las queries visibles (un único refetch para resincronizar).
- Dashboard: grupo `dashboard` con agregados que el panel emite a baja frecuencia (≈5–10 s).

## Consola

- El componente `Terminal` crea la instancia xterm **una vez** y escribe con `terminal.write()` desde el handler del evento, **sin pasar por estado de React** (un re-render por línea destruiría el rendimiento).
- `JoinConsole(serverId, fromSequence)` (requiere `server.console.view`) entrega backlog + stream.
- Scrollback 5 000 líneas (configurable). Búsqueda (addon-search). Botón "limpiar" = `terminal.clear()` local, no afecta al servidor.
- Timestamps opcionales: prefijo generado en el cliente a partir del timestamp de cada chunk (exacto en modo `pipes`, aproximado en `pty`; documentado).
- Entrada: campo de comando separado bajo la terminal, con historial (↑/↓). Solo visible con `server.console.write`. Es más claro para consolas de juego que escribir dentro de xterm y permite consola de solo lectura.
- Banner de reconexión y marcador visible cuando el agent reporta un hueco (`IsGapMarker`).
- Enlaces: solo `http/https`, `rel="noopener noreferrer"`.

## Seguridad en el cliente

- Autenticación con cookie `HttpOnly; Secure; SameSite=Strict`. **Ningún token en localStorage.**
- Protección CSRF: antiforgery de ASP.NET Core con cabecera en mutaciones.
- CSP estricta servida por el backend: `default-src 'self'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'none'`. Implica: sin scripts inline, sin CDNs, fuentes empaquetadas.
- Nunca `dangerouslySetInnerHTML`. El contenido de archivos se muestra en el editor, jamás como HTML.
- La UI oculta acciones sin permiso, pero **el backend es la única autoridad**.

## Integración con ASP.NET Core

- `npm run build` genera `dist/`; un target MSBuild de `Pulsar.Api` (solo en `Release`/`publish`) ejecuta el build y copia a `wwwroot`. En desarrollo se usan dos procesos (Vite con proxy + API).
- `app.MapStaticAssets()` (fingerprinting y compresión en .NET 9+) y `MapFallbackToFile("index.html")` **excluyendo** `/api`, `/hubs` y `/agent` (un 404 de API nunca devuelve el HTML de la SPA).
- Node.js solo se necesita para compilar; el panel publicado no lo requiere.

## Diseño visual

- Desktop primero (densidad de información adecuada, sidebar), responsive hasta tablet/móvil para acciones básicas (power, consola, estado).
- Tema oscuro por defecto + claro; tokens de diseño en variables CSS.
- Estados con color **y** texto/icono (accesibilidad).
- Dashboard v1: tarjetas de servidores online/offline, nodos conectados, CPU/RAM global, alertas (crash loop, nodo desconectado, disco bajo) y actividad reciente (audit). Nada más.
