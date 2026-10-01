# 06 — Gestión de procesos en Windows: Job Objects, ConPTY y aislamiento

> Estado: **propuesta para revisión** · Cubre el punto 11. Decisiones: ADR-0005, ADR-0006, ADR-0007.
> Todo lo marcado **[spike]** se valida con prototipo en la Fase 0 antes de construir encima.

## Por qué no `Process.Start()`

`System.Diagnostics.Process` no permite:

1. Crear el proceso **dentro** de un Job antes de que ejecute código (si se asigna después, los hijos creados en ese intervalo escapan).
2. Lanzar como otro usuario a partir de un token (`CreateProcessAsUser`).
3. Adjuntar una pseudo-consola (ConPTY).
4. Restringir qué handles hereda el hijo (con `UseShellExecute=false` y redirección, el hijo puede heredar handles heredables del agent).

Por eso `Pulsar.Agent.Windows` usa Win32 directamente, con P/Invoke generado por **CsWin32** (`Microsoft.Windows.CsWin32`, source generator oficial de Microsoft: firmas correctas, `SafeHandle`s, sin `DllImport` escritos a mano).

## Abstracción

```csharp
// Pulsar.Agent.Core
public interface IGameProcessManager
{
    Task<IGameProcess> StartAsync(ProcessLaunchSpec spec, CancellationToken ct);
}

public sealed record ProcessLaunchSpec(
    Guid ServerId,
    string ServerRoot,                     // directorio raíz autorizado
    string ExecutableRelativePath,
    IReadOnlyList<string> Arguments,       // argv ya renderizado por Pulsar.Manifests
    string WorkingDirectoryRelativePath,
    IReadOnlyDictionary<string, string> Environment,
    ConsoleMode ConsoleMode,               // Pty | Pipes
    ConsoleEncoding Encoding,
    ProcessIdentity Identity,              // cuenta dedicada del servidor
    ProcessLimits? Limits);                // reservado (memoria/CPU) post-v1

public interface IGameProcess : IAsyncDisposable
{
    int MainProcessId { get; }
    DateTime StartedAtUtc { get; }
    Task<ProcessExit> Exited { get; }                       // salida del proceso principal
    ChannelReader<ConsoleChunk> Output { get; }
    ValueTask WriteInputAsync(string text, CancellationToken ct);
    ValueTask SendInterruptAsync(CancellationToken ct);     // Ctrl+C (solo Pty)
    void TerminateTree(uint exitCode);                      // TerminateJobObject
    ProcessTreeUsage SampleUsage();                         // CPU acumulada, memoria, nº procesos
}
```

`WindowsGameProcessManager` implementa esto. Un futuro `LinuxGameProcessManager` (cgroups/pidfd/pty) implementaría la misma interfaz sin tocar `Agent.Core`.

## Secuencia de arranque (Windows)

```
 1. Resolver y validar rutas:
      exe = Path.GetFullPath(Combine(ServerRoot, ExecutableRelativePath))
      abrir handle al exe, GetFinalPathNameByHandle → debe estar bajo ServerRoot
      ningún componente de la ruta puede ser reparse point (junction/symlink)
 2. Job:
      CreateJobObject(sin nombre)
      SetInformationJobObject(JobObjectExtendedLimitInformation):
         JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
         JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION   (evita diálogos WER que cuelgan el server)
         (sin BREAKAWAY_OK ni SILENT_BREAKAWAY_OK: ningún hijo puede salir del Job)
      SetInformationJobObject(JobObjectBasicUIRestrictions): EXITWINDOWS, GLOBALATOMS,
         HANDLES, READCLIPBOARD, WRITECLIPBOARD, SYSTEMPARAMETERS, DESKTOP, DISPLAYSETTINGS
      SetInformationJobObject(JobObjectAssociateCompletionPortInformation) → IOCP compartido del agent
 3. Identidad:
      LogonUser(cuenta del servidor, LOGON32_LOGON_BATCH) → token
      LoadUserProfile(token)                                         [spike: necesidad real por juego]
      CreateEnvironmentBlock(token) + overrides del manifest + TEMP/TMP dentro del servidor
 4. Consola:
      Pty:   CreatePipe ×2 → CreatePseudoConsole(COORD{220, 50}, inRead, outWrite)
      Pipes: CreatePipe ×3 (stdin/stdout/stderr), solo los extremos del hijo heredables
 5. STARTUPINFOEX + lista de atributos:
      PROC_THREAD_ATTRIBUTE_JOB_LIST       → el proceso nace dentro del Job (Windows 10+)
      PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE  (Pty)  ó
      PROC_THREAD_ATTRIBUTE_HANDLE_LIST    (Pipes) → hereda SOLO esos handles
      lpDesktop = estación/escritorio dedicado del agent con DACL para el SID del servidor  [spike]
 6. CreateProcessAsUser(token, lpApplicationName = exe absoluto, lpCommandLine = argv citado,
      flags: EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED
             (+ CREATE_NO_WINDOW en Pipes))
 7. Verificar IsProcessInJob; ResumeThread; cerrar handle de hilo y extremos del hijo.
 8. Registrar espera sobre el handle del proceso principal (sin hilo bloqueado por servidor).
 9. Hilos/tareas de lectura de salida → decodificador UTF-8 stateful → ring buffer + log + suscriptores.
```

`CREATE_SUSPENDED` más `JOB_LIST` es redundante a propósito: si `JOB_LIST` fallara en alguna build, se hace `AssignProcessToJobObject` antes de `ResumeThread`, y en ningún caso el proceso ejecuta código fuera del Job.

## Consola: ConPTY vs pipes

| | **ConPTY** (`CreatePseudoConsole`, Windows 10 1809+) | **Pipes** (stdout/stderr/stdin) |
|---|---|---|
| Procesos que escriben con la API de consola de Windows (no stdout) | Funciona: conhost lo traduce a secuencias VT | No se captura nada o se captura mal |
| Colores ANSI en xterm.js | Nativo (VT) | Solo si el proceso emite ANSI |
| Entrada | VT; Ctrl+C = byte `0x03` → `CTRL_C_EVENT` | stdin; Ctrl+C no es fiable desde un servicio sin consola |
| Formato de salida | Pantalla renderizada (movimientos de cursor, redibujos, ajuste de línea al ancho) | Flujo de líneas limpio |
| Timestamps por línea | Aproximados (por chunk) | Exactos |

**Decisión:** el manifest elige (`console.mode`). Se espera `pty` para la familia Source (CS2, CS:S, GMod) y `pipes` para Java/Minecraft. **[spike]** Confirmar en Windows 11 y Windows Server 2022/2025 cómo se comportan `cs2.exe` y `srcds.exe` en cada modo, y la salida de `steamcmd.exe` (se reporta que con stdout redirigido su salida llega en bloques; ConPTY podría resolverlo).

Gotchas conocidos a cubrir con tests:

- `ClosePseudoConsole` puede bloquear en builds antiguas si nadie drena el pipe de salida: el lector se mantiene vivo hasta después del cierre.
- El ancho de la pseudo-consola afecta al ajuste de línea; se fija ancho amplio (≈220) y no se redimensiona por cliente (varios navegadores miran la misma consola).
- Las tareas de lectura nunca escriben a disco por cada callback: van a un canal con escritor en lote.

## Parada controlada

Windows no tiene SIGTERM. Pulsar ejecuta los pasos del manifest en orden:

1. `consoleCommand` (p. ej. `quit`, `stop`) escrito en la entrada.
2. `ctrlC` (solo Pty).
3. Espera hasta `timeoutSeconds` al proceso principal.
4. `TerminateJobObject` → mata **todo el árbol** de forma atómica.

Cuando el proceso principal termina, si el Job aún tiene procesos (crash handlers, helpers), se concede un margen de 5 s y se termina el Job. No quedan huérfanos.

## Detección de salida y eventos

- **Señal primaria**: espera sobre el handle del proceso principal (exit code fiable).
- **Señal secundaria**: IOCP del Job (`JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO`, `JOB_OBJECT_MSG_ABNORMAL_EXIT_PROCESS`, `JOB_OBJECT_MSG_NEW_PROCESS`). Microsoft documenta que la entrega de estos mensajes **no está garantizada**, así que nunca son la única fuente de verdad; se usan para diagnóstico y conteo.
- Un único hilo dedicado lee el IOCP de todos los Jobs (clave de completado = servidor). Sin polling.

## Métricas (cada 5 s, un solo temporizador para todo el nodo)

| Métrica | Fuente |
|---|---|
| CPU % | `QueryInformationJobObject(JobObjectBasicAccountingInformation)`: Δ(TotalUserTime + TotalKernelTime) / (Δt × núcleos lógicos) |
| Nº de procesos | `ActiveProcesses` de la misma consulta |
| Memoria | `JobObjectBasicProcessIdList` → por proceso `GetProcessMemoryInfo` (private bytes / working set). Se reutilizan handles abiertos con `PROCESS_QUERY_LIMITED_INFORMATION`. |
| PID principal, uptime | Del estado del supervisor |
| Disco | Bajo demanda con caché (nunca en el ciclo de 5 s) |

Coste: unas pocas syscalls por servidor cada 5 s. Sin contadores de rendimiento (PerfCounters), que son lentos y frágiles.

## Aislamiento: cuenta Windows por servidor (ADR-0005)

**El agent corre como LocalSystem; ningún game server corre como SYSTEM.** Un plugin malicioso de SourceMod o Lua de GMod que ejecutara como SYSTEM comprometería la máquina entera.

| Opción | Aislamiento entre servidores | Complejidad |
|---|---|---|
| A. Todos como el usuario del agent | Ninguno; si el agent es SYSTEM, catastrófico | Mínima |
| B. Una cuenta común de baja prioridad para todos | Protege el sistema, no a un servidor de otro | Baja |
| **C. Una cuenta local por servidor** | Cada servidor solo accede a su directorio | Media |
| D. AppContainer | Fuerte | Alta; rompe red y rutas de muchos juegos |

**Recomendación: C.** Detalle:

- Cuenta `pulsar_<shortId>` (≤ 20 caracteres) creada con `NetUserAdd`, contraseña aleatoria de 64 caracteres (cumple políticas de complejidad), `UF_DONT_EXPIRE_PASSWD | UF_PASSWD_CANT_CHANGE`, **retirada del grupo `Users`**. Contraseña guardada cifrada con DPAPI (LocalMachine) en el estado del agent.
- Derechos LSA (`LsaAddAccountRights`): `SeBatchLogonRight`; **denegados** `SeDenyInteractiveLogonRight`, `SeDenyRemoteInteractiveLogonRight`, `SeDenyNetworkLogonRight` (nadie puede usarla para RDP o SMB).
- ACLs: `<ServersRoot>` con herencia deshabilitada (solo SYSTEM y Administrators). Cada `<ServersRoot>\<shortId>`: SYSTEM y Administrators control total, `pulsar_<shortId>` Modify, cuenta de SteamCMD Modify. Un servidor no puede listar ni leer otro.
- Alternativa a evaluar en el spike: logon S4U (sin contraseña almacenada). Se elige `LogonUser` + contraseña por estar mejor documentado.
- Limitación conocida: en un **controlador de dominio** no existen cuentas locales; el agent se niega a instalarse en un DC con un mensaje claro.
- **[spike]** Window station / desktop: un proceso lanzado con otro token desde un servicio falla al inicializar user32 (`0xC0000142`) si su SID no tiene acceso a la window station/desktop. Se crea un desktop dedicado con DACL apropiada y se pasa en `lpDesktop`.

### SteamCMD

- Instalación única por nodo en `%ProgramData%\Pulsar\Agent\steamcmd`, ejecutada bajo una cuenta dedicada `pulsar_steamcmd` (no SYSTEM, no las cuentas de servidor: SteamCMD se autoactualiza y escribe en su directorio; si una cuenta de servidor pudiera escribir ahí, comprometería a todos los demás).
- Cola por nodo con concurrencia 1 en v1 (SteamCMD comparte `appcache`/config; ejecuciones concurrentes sobre la misma instalación son frágiles). La cola es visible en la UI. Concurrencia > 1 con instalaciones separadas: post-v1.
- Argumentos: `+force_install_dir <dir>` **antes** de `+login anonymous`, `+app_update <appId> [-beta …] [validate]`, `+quit`. Construidos como argv, nunca concatenados por shell.
- Descarga inicial desde la URL oficial de Valve sobre HTTPS; **[spike]** verificar firma Authenticode de `steamcmd.exe` (`WinVerifyTrust`) si Valve lo firma. El primer arranque se autoactualiza: tratado como etapa "bootstrap".
- Progreso por regex sobre líneas `Update state (0x…) downloading, progress: NN.NN (x / y)`, tolerante a cambios de formato (si no se reconoce, se muestra el log sin porcentaje).
- Reintentos con backoff para fallos transitorios conocidos (máx. 3); errores de espacio en disco o credenciales no se reintentan. Cancelación = terminar su Job.
- v1: solo `anonymous` (CS2, CS:S y GMod lo permiten). Login con cuenta Steam y Steam Guard: post-v1, con credenciales a nivel de nodo cifradas.

### Firewall de Windows

Opcional por nodo (`ManageFirewall`). El agent crea reglas de entrada por servidor (`Pulsar <shortId> <port> <proto>`) limitadas al ejecutable del servidor y a sus puertos, usando la API COM `INetFwPolicy2`. Se eliminan al borrar el servidor o cambiar puertos. Nunca se desactiva el firewall ni se crean reglas genéricas.

## Reinicio del agent y `KILL_ON_JOB_CLOSE` (ADR-0006)

| | **KILL_ON_JOB_CLOSE (recomendado v1)** | Servidores sobreviven al agent |
|---|---|---|
| Huérfanos | Imposibles: si el agent muere, el SO cierra el handle del Job y mata el árbol | Posibles |
| Consola tras reinicio del agent | N/A (todo reinicia limpio) | Se pierde: la ConPTY/pipes pertenecen al agent que murió; el juego queda escribiendo a un pipe roto |
| Actualizar el agent | Detiene los servidores (se programa) | Sin downtime |
| Complejidad | Baja | Alta: requiere un proceso *shim* por servidor que posea Job y consola, con IPC hacia el agent |

Se recomienda KILL_ON_JOB_CLOSE para v1 por fiabilidad. La interfaz `IGameProcess` permite que en el futuro la implementación sea un *shim* por servidor (patrón `containerd-shim`) sin cambiar el resto del agent. La parada controlada del servicio (`OnStop`) detiene los servidores ordenadamente (pasos del manifest) antes de salir, dentro del tiempo que concede el SCM.

## Límites futuros (preparados, no implementados en v1)

El Job ya existe por servidor, así que añadir límites es configuración, no rediseño: `JOB_OBJECT_LIMIT_JOB_MEMORY`, `JobObjectCpuRateControlInformation` (tope duro o pesos), `JobObjectNetRateControlInformation`, prioridad de E/S.

## Tests

`Pulsar.TestGameServer` es un ejecutable de prueba controlado por argumentos: imprime salida, lee stdin, crea hijos y nietos, ignora `quit`, crashea con un código dado, escribe a la API de consola, intenta crear un junction. `Pulsar.Agent.Windows.Tests` (solo en runners Windows) verifica:

- matar el Job no deja procesos vivos (incluidos nietos);
- matar el proceso del agent de test mata los servidores (KILL_ON_JOB_CLOSE);
- el hijo no hereda handles no listados;
- detección de crash y exit codes;
- parada por comando, por Ctrl+C y por timeout;
- el proceso corre con la cuenta esperada y no puede leer el directorio de otro servidor.
