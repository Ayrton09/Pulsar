# ADR-0006 — Job Objects con KILL_ON_JOB_CLOSE: los servidores no sobreviven al agent (v1)

Estado: Propuesta

## Contexto
Hay que evitar procesos huérfanos y garantizar la limpieza del árbol de procesos. Si el agent muere, la consola (ConPTY/pipes) que posee también se pierde.

## Decisión
Un Job Object sin nombre por servidor, con `KILL_ON_JOB_CLOSE`, `DIE_ON_UNHANDLED_EXCEPTION`, restricciones de UI y sin breakaway. El proceso se crea dentro del Job (`PROC_THREAD_ATTRIBUTE_JOB_LIST` + `CREATE_SUSPENDED`). Si el agent termina, Windows mata todos los servidores. Al arrancar, el agent reinicia los que tengan `auto_start` o estuvieran en ejecución antes de una parada ordenada.

## Alternativas
- Servidores que sobreviven al agent (Jobs con nombre y reattach): la consola se pierde de todos modos y aparecen estados ambiguos.
- Proceso shim por servidor que posea Job y consola: la solución correcta a largo plazo, pero añade IPC y otro binario. Pospuesto; `IGameProcess` está diseñado para permitirlo.

## Consecuencias
- Garantía fuerte de que no hay huérfanos y de que el estado es siempre coherente.
- Actualizar o reiniciar el agent implica downtime de los servidores del nodo: debe programarse y documentarse.
