# 04 — Game Manifests

> Estado: **propuesta para revisión** · Cubre el punto 9. Decisión: ADR-0008.

## Objetivos

1. Añadir un juego sin tocar Pulsar Core.
2. **Declarativo y cerrado**: un manifest describe *qué* hacer con un conjunto finito de pasos tipados; nunca contiene shell, scripts ni expresiones arbitrarias.
3. Validado igual en panel y agent (`Pulsar.Manifests`), con JSON Schema público para editores y CI de la comunidad.
4. Versionado e inmutable: un servidor queda fijado a `key@version`; actualizar el manifest no cambia servidores existentes sin acción explícita.

## Modelo de confianza

Un manifest decide qué ejecutable se lanza y qué se descarga. **Importar un manifest equivale a instalar software en todos los nodos.** Por eso:

- Solo `template.manage` (global) puede importar; la UI muestra un resumen de "capacidades" (ejecutables, dominios de descarga, puertos) y el diff con la versión previa.
- Los manifests oficiales vienen empaquetados con el panel (`games/`) y se registran como `source = bundled`.
- (Post-v1) firma opcional del catálogo oficial y política local por nodo para aceptar solo manifests firmados.

## Estructura

```
games/<key>/manifest.json      un solo archivo = un solo hash
games/<key>/README.md          notas para mantenedores (no se carga)
schemas/manifest.v1.schema.json
```

## Ejemplo: Counter-Strike 2

> Ilustrativo. Los argumentos exactos, patrones de readiness y comportamiento de consola se confirman en los spikes de la Fase 0.

```json
{
  "$schema": "https://raw.githubusercontent.com/<org>/pulsar/main/schemas/manifest.v1.schema.json",
  "schemaVersion": 1,
  "key": "cs2",
  "version": "1.0.0",
  "name": "Counter-Strike 2",
  "description": "Servidor dedicado de Counter-Strike 2.",
  "authors": ["Pulsar contributors"],
  "platforms": ["windows-x64"],

  "requirements": { "minDiskGb": 60, "minMemoryMb": 4096 },

  "variables": [
    { "key": "hostname", "label": "Nombre del servidor", "type": "string",
      "default": "Pulsar CS2", "maxLength": 64, "pattern": "^[^\"\\r\\n;]{1,64}$" },
    { "key": "map", "label": "Mapa inicial", "type": "string",
      "default": "de_dust2", "pattern": "^[A-Za-z0-9_]{1,64}$" },
    { "key": "maxPlayers", "label": "Slots", "type": "integer", "default": 10, "min": 2, "max": 64 },
    { "key": "gameMode", "label": "Modo", "type": "enum", "default": "competitive",
      "options": [
        { "value": "casual",      "label": "Casual",      "map": { "gameType": "0", "gameMode": "0" } },
        { "value": "competitive", "label": "Competitivo", "map": { "gameType": "0", "gameMode": "1" } }
      ] },
    { "key": "rconPassword", "label": "Contraseña RCON", "type": "string", "secret": true,
      "pattern": "^[A-Za-z0-9_\\-]{8,64}$", "required": false },
    { "key": "gslt", "label": "Game Server Login Token", "type": "string", "secret": true,
      "pattern": "^[A-Za-z0-9]{1,64}$", "required": false }
  ],

  "ports": [
    { "name": "game", "label": "Juego", "protocols": ["udp", "tcp"], "default": 27015, "required": true },
    { "name": "tv",   "label": "CSTV",  "protocols": ["udp"],        "default": 27020, "required": false }
  ],

  "install": {
    "steps": [
      { "type": "steamcmd", "appId": 730, "login": "anonymous" }
    ]
  },
  "update": { "steps": [ { "type": "steamcmd", "appId": 730, "login": "anonymous" } ] },

  "runtime": {
    "windows-x64": {
      "executable": "game/bin/win64/cs2.exe",
      "workingDirectory": "game/bin/win64",
      "arguments": [
        "-dedicated",
        "-usercon",
        "-port", "{{port.game}}",
        "-maxplayers", "{{var.maxPlayers}}",
        "+game_type", "{{var.gameMode.gameType}}",
        "+game_mode", "{{var.gameMode.gameMode}}",
        "+map", "{{var.map}}",
        { "if": "var.gslt", "then": ["+sv_setsteamaccount", "{{var.gslt}}"] }
      ],
      "allowExtraArguments": true,
      "environment": {},
      "console": {
        "mode": "pty",
        "inputNewline": "\r\n",
        "sensitiveCommandPrefixes": ["rcon_password", "sv_password", "sv_setsteamaccount"]
      }
    }
  },

  "configFiles": [
    { "path": "game/csgo/cfg/server.cfg", "format": "source-cfg", "mode": "patch",
      "set": {
        "hostname": "{{var.hostname}}",
        "rcon_password": "{{var.rconPassword}}"
      } }
  ],

  "readiness": { "type": "logPattern", "pattern": "<a confirmar en spike>", "timeoutSeconds": 300 },

  "stop": {
    "steps": [ { "type": "consoleCommand", "command": "quit" } ],
    "timeoutSeconds": 30
  },

  "backup": {
    "include": ["game/csgo/cfg/**", "game/csgo/addons/**", "game/csgo/gameinfo.gi"],
    "exclude": ["**/*.log", "**/*.dmp"]
  }
}
```

## Secciones y reglas

### Identidad y versionado

- `schemaVersion`: versión del **formato** (lo interpreta `Pulsar.Manifests`). Un formato nuevo implica nuevo schema y validador; el viejo se sigue leyendo.
- `key` (`^[a-z0-9][a-z0-9-]{1,63}$`) + `version` (semver) identifican una plantilla inmutable.

### Variables

| Tipo | Restricciones obligatorias |
|---|---|
| `string` | `pattern` **obligatorio** si se usa en `arguments` o `configFiles` (lo exige el linter); `maxLength`. |
| `integer` | `min` y `max`. |
| `boolean` | — |
| `enum` | `options` cerradas; cada opción puede mapear sub-valores (`{{var.gameMode.gameType}}`). |

Atributos: `secret` (cifrada en BD, write-only en la API, enmascarada en UI y logs), `required`, `userEditable` (si `false`, solo quien tiene `server.settings.edit` a nivel nodo/global la modifica).

Validación por defecto adicional para `string` usadas como argumento: sin espacios en blanco al inicio, sin caracteres de control, y **no pueden empezar por `-` ni `+`** (evita que un valor se convierta en flag o comando de consola de Source). Las regex se evalúan con `RegexOptions.NonBacktracking` y timeout (sin ReDoS desde un manifest).

### Plantillas de argumentos

- Placeholders permitidos: `{{var.<key>}}`, `{{var.<key>.<sub>}}`, `{{port.<name>}}`, `{{port.<name>.ip}}`, `{{server.shortId}}`. Nada más: sin funciones, sin expresiones, sin acceso a entorno.
- **Cada elemento del array es exactamente un argumento (argv) después de sustituir.** Un valor jamás se divide en varios argumentos.
- Condicional único: `{ "if": "var.x", "then": [...] }` (verdadero si boolean `true` o string no vacío).
- El agent construye la línea de comandos Windows con el algoritmo de quoting de MSVCRT/`CommandLineToArgvW` (comillas y backslashes correctamente escapados) y la pasa a `CreateProcessAsUser` con `lpApplicationName` explícito. Nunca vía `cmd.exe`.
- `allowExtraArguments`: si `true`, los usuarios con `server.startup.edit` pueden añadir argumentos (cada uno un argv, con la misma validación de caracteres de control). Si `false`, no.

### Ejecutable y rutas

- `executable` y `workingDirectory`: rutas **relativas** al directorio del servidor, con `/`, sin `..`, sin raíz, sin unidad, sin `:` (ADS), sin nombres reservados (`CON`, `NUL`, `COM1`…), sin puntos/espacios finales.
- Solo extensión `.exe`. Prohibidos `.bat`, `.cmd`, `.ps1`, `.vbs`, `.js`, `.msi`: implican intérpretes/shell.
- (Post-v1) `executable: { "runtime": "java-21", "path": "bin/java.exe" }` para runtimes gestionados por el agent (Minecraft). El hueco está previsto en el schema pero no se implementa en v1.

### Entorno

`environment` permite fijar variables con placeholders. Lista de denegación: `PATH`, `PATHEXT`, `ComSpec`, `SystemRoot`, `windir`, `PSModulePath`, `TEMP`/`TMP` (los fija el agent dentro del directorio del servidor), `USERPROFILE`, `APPDATA`, `LOCALAPPDATA`.

### Pasos de instalación/actualización (conjunto cerrado)

| Tipo | v1 | Parámetros |
|---|---|---|
| `steamcmd` | Sí | `appId`, `login` (`anonymous` en v1), `beta?`, `betaPasswordVar?` (secreta), `validate?`, `platform?` |
| `ensureDirectory` | Sí | `path` |
| `writeFileIfMissing` | Sí | `path`, `content` (plantilla con encoder del `format`) |
| `download` | Post-v1 | `url` (**solo https**), `sha256` **obligatorio**, `path` |
| `extractArchive` | Post-v1 | `archive`, `destination`, protección zip-slip |

No hay paso `run`/`exec`/`script`. Si un juego necesitara un instalador propio, se discutirá como paso tipado nuevo con su propio análisis de seguridad.

### Archivos de configuración

- `mode: "patch"`: el agent modifica solo las claves declaradas y conserva el resto del archivo (el usuario puede seguir editándolo en el file manager). `mode: "managed"`: el archivo se regenera completo; la UI avisa de que las ediciones manuales se pierden.
- Formatos v1: `source-cfg` (`cvar "valor"` por línea) y `properties` (`clave=valor`). `ini` y `json` post-v1.
- Cada formato tiene un **encoder** que rechaza lo que rompería el formato: en `source-cfg` se rechazan `"`, `;`, `\r`, `\n` (en Source, `;` separa comandos: `hostname "x"; rcon_password y` sería una inyección).
- Variables vacías no secretas → la línea se elimina en modo patch; secretos vacíos → la línea no se toca.

### Consola

- `mode`: `pty` (ConPTY; necesario para procesos que escriben en la consola Windows en vez de stdout) o `pipes` (stdout/stderr/stdin redirigidos; p. ej. Java).
- `encoding` (solo `pipes`): `utf-8` (defecto), `oem`, `ansi`.
- `inputNewline`, `sensitiveCommandPrefixes` (los comandos que empiezan así se auditan como `rcon_password ***`).

### Readiness y parada

- Readiness: `logPattern` (regex sobre la salida), `processAlive` (`graceSeconds`), post-v1 `query` (A2S para Source). Pasa `Starting → Running`.
- Parada: pasos `consoleCommand`, `ctrlC` (solo `pty`), `wait`; tras `timeoutSeconds` el agent **termina el Job** (kill del árbol completo).

### Backups

Globs (`Microsoft.Extensions.FileSystemGlobbing`) relativos al directorio del servidor. **Por defecto se excluye el contenido reinstalable vía SteamCMD**: un backup de CS2 debe ocupar megas de configuración, no 60 GB de binarios. Sin `backup` declarado, se respalda todo el directorio.

### Reservado para versiones posteriores

`query` (protocolo de consulta), `healthChecks` adicionales, `databases` (solicitar una base MySQL/MariaDB al módulo de game databases), `runtime` gestionados, `linux-x64`.

## Validación

`Pulsar.Manifests` expone:

```csharp
ManifestParseResult ManifestParser.Parse(ReadOnlySpan<byte> utf8Json);   // JSON Schema + modelo tipado
IReadOnlyList<ManifestError> ManifestValidator.Validate(GameManifest m);  // reglas semánticas
Result<LaunchPlan> LaunchPlanRenderer.Render(GameManifest m, Platform p,
    IReadOnlyDictionary<string, VariableValue> values, PortAssignments ports, IReadOnlyList<string> extraArgs);
```

Reglas semánticas (además del schema): placeholders referencian variables/puertos existentes; strings usados en argumentos tienen `pattern` o son `enum`; rutas válidas; ejecutable `.exe`; puertos sin duplicar; regex compilables en modo NonBacktracking.

Un test en el repo carga **todos** los `games/*/manifest.json` y falla si alguno es inválido. Los tests de renderizado incluyen casos de inyección (`map = "de_dust2 +rcon_password x"`, valores con comillas, `;`, saltos de línea, `-insecure`).
