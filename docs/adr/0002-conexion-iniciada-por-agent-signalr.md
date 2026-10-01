# ADR-0002 — Conexión persistente iniciada por el Agent sobre SignalR (WebSockets)

Estado: Propuesta

## Contexto
Los nodos están a menudo detrás de NAT, con IP dinámica y Windows Firewall. Abrir un puerto por agent es una barrera de instalación y una superficie de ataque. Muchos usuarios Windows publican el panel detrás de IIS/ARR, nginx, Caddy o Cloudflare.

## Decisión
El agent abre una conexión WebSocket saliente (TLS) al hub `/agent/v1/hub` usando el cliente .NET de SignalR, solo con transporte WebSockets y protocolo JSON. Comando → ack mediante *client results*; el trabajo largo se reporta por eventos con `OperationId`. Las transferencias grandes van por HTTPS iniciado por el agent con tickets de un solo uso. Versión mayor en la ruta, cambios aditivos dentro de una versión, capabilities en el `Hello`.

## Alternativas
- gRPC bidi streaming: mejor IDL, pero exige HTTP/2 extremo a extremo; problemático detrás de IIS/ARR y proxies comunes.
- WebSocket crudo: habría que reimplementar reconexión, keep-alive y correlación.
- Panel → Agent (agent escuchando): requiere puertos abiertos en cada nodo.

## Consecuencias
- Cero puertos de entrada para Pulsar en los nodos.
- Mismo stack de tiempo real para navegador y agents.
- El contrato es C# (nombres de método + DTO): requiere disciplina de versionado y tests de compatibilidad N-1.
