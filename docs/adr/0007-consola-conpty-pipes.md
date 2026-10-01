# ADR-0007 — Consola por ConPTY o pipes según el manifest; sin persistencia en PostgreSQL

Estado: Propuesta

## Contexto
Muchos servidores Windows (familia Source) escriben con la API de consola de Windows y no por stdout; la redirección clásica podría no capturar su salida de forma fiable (a confirmar en spike). La consola debe ser en tiempo real, con scrollback y reconexión.

## Decisión
- El manifest declara `console.mode`: `pty` (ConPTY) o `pipes`.
- El agent mantiene un ring buffer por servidor con secuencia monotónica y un log rotado en disco escrito en lotes.
- La consola solo se transmite al panel cuando hay espectadores; los navegadores reconectan pidiendo el backlog desde su última secuencia.
- La salida de consola no se guarda en PostgreSQL.

## Alternativas
- Solo pipes: probablemente insuficiente para los juegos prioritarios.
- Solo RCON: no da salida completa y depende de cada juego; queda como plan B para enviar comandos.
- Guardar consola en PostgreSQL: volumen alto y sin valor proporcional.

## Consecuencias
- ConPTY entrega una "pantalla" (secuencias VT), por lo que los timestamps por línea son aproximados.
- Requiere Windows 10 1809+ / Server 2019+.
