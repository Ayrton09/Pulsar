; What PulsarSetup.iss and PulsarAgentSetup.iss share (Phase 18, docs/guides/install.md "Easy install"): the directives,
; the messages and the code that runs Install-Pulsar.ps1 -Unattended and reads its progress. Build-Release.ps1 compiles
; both with Inno Setup 7:
;   ISCC /DVersion=<version> /DNumericVersion=<a.b.c.0> /DRelease=<staged release folder> /O<output> <setup>.iss
;
; How a setup installs (docs/architecture/08-security-and-risks.md, T39):
; - Its payload (the release's programs and Install-Pulsar.ps1) goes to {app}\payload, {app} being
;   C:\Program Files\Pulsar\Setup: a folder made in Program Files takes its permissions as it is made, so only SYSTEM,
;   Administrators and TrustedInstaller can change it, which Install-Pulsar.ps1 checks before it runs (never {tmp},
;   which is in the user's profile, whose owner could replace what is in it).
; - Install-Pulsar.ps1 runs as the payload's last file is in place (AfterInstall), in 64-bit Windows PowerShell, with
;   -Unattended and the choices that are not secret on its command line. The secrets (passwords, a connection string,
;   the node code) reach it through this elevated process's environment, which it inherits: set just before it starts
;   and removed as soon as it has (its first line of output), never on a command line, in a file, the registry, Inno
;   Setup's log or the wizard's saved settings (no SetPreviousData, no [Registry] or [INI] entries).
; - Its progress lines (PULSAR-STEP) move the progress bar; PULSAR-LOG, PULSAR-PANEL-URL and PULSAR-ERROR are kept
;   for the end. Done (0, or 3010 for a restart): the Finished page offers the panel's sign-in page. Nothing changed
;   (2 to 6): the setup rolls back its own files too and says why. Anything else: the setup's entry in Apps stays, so
;   that removing it removes what was left.
; - The uninstaller runs Install-Pulsar.ps1 -Unattended -Uninstall (data kept: the script has no option to delete it).

#ifndef Version
  #define Version "0.0.0-dev"
#endif
#ifndef NumericVersion
  #define NumericVersion "0.0.0.0"
#endif
#ifndef Release
  #error Give the staged release folder: ISCC /DRelease=<folder> (Build-Release.ps1 does).
#endif
; One application for both setups: either installs Pulsar with Install-Pulsar.ps1, which finds what is installed by its
; services, and one entry in Apps removes it.
#define AppGuid "8D0B5E7C-4A61-4F2B-9C3E-2B7D1A9F6E54"

[Setup]
AppId={{{#AppGuid}}
AppName=Pulsar
AppVersion={#Version}
AppVerName=Pulsar {#Version}
AppPublisher=Pulsar contributors
AppPublisherURL=https://github.com/Ayrton09/Pulsar
AppSupportURL=https://github.com/Ayrton09/Pulsar/issues
AppUpdatesURL=https://github.com/Ayrton09/Pulsar/releases
AppCopyright=Pulsar contributors, GNU AGPL v3
VersionInfoVersion={#NumericVersion}
VersionInfoTextVersion={#Version}
VersionInfoProductName=Pulsar
VersionInfoProductTextVersion={#Version}
VersionInfoCompany=Pulsar contributors
UninstallDisplayName=Pulsar
LicenseFile={#Release}\LICENSE
; Administrators, 64-bit Windows 10 22H2 (build 19045), Windows Server 2022 or later, as docs/guides/install.md says. A
; 64-bit setup: {sys} is the 64-bit System32 and the registry its 64-bit view.
PrivilegesRequired=admin
SetupArchitecture=x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045
; The payload's folder, always this one (PrepareToInstall refuses another, from /DIR= say).
DefaultDirName={commonpf64}\Pulsar\Setup
DisableDirPage=yes
UsePreviousAppDir=no
DisableProgramGroupPage=yes
DisableWelcomePage=no
; Cancelling while Install-Pulsar.ps1 runs would roll back the setup's files under a Pulsar that is installed.
AllowCancelDuringInstall=no
CloseApplications=no
RestartIfNeededByRun=no
; This session's only: a Global\ name could be taken first by any account on the PC, which would keep every setup from
; running (the ZIP's Install-Pulsar.ps1 still would). Install-Pulsar.ps1 refuses to install over what is there.
SetupMutex=PulsarSetup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern dynamic
ShowLanguageDialog=auto

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[Messages]
en.ConfirmUninstall=Remove %1 from this PC?%n%nIts services, programs and firewall rules go, and so does the trust it gave the panel's certificate. Kept: the database and PostgreSQL (its own app in Apps), the .NET runtimes, C:\ProgramData\Pulsar (the panel's keys, needed to read its database, and the logs) and the game servers' folders. Delete game servers in the panel first: that also removes their Windows accounts.
es.ConfirmUninstall=¿Quitar %1 de esta PC?%n%nSe van sus servicios, sus programas, sus reglas del firewall y la confianza que le dio al certificado del panel. Se conservan: la base de datos y PostgreSQL (una aplicación aparte en Aplicaciones), los runtimes de .NET, C:\ProgramData\Pulsar (las claves del panel, necesarias para leer su base de datos, y los logs) y las carpetas de los servidores de juegos. Borrá antes los servidores de juegos desde el panel: eso también quita sus cuentas de Windows.
en.UninstalledAll=%1 was removed from this PC.%n%nKept, for you to remove when nothing needs them: the database and PostgreSQL (in Apps), the .NET runtimes (in Apps), C:\ProgramData\Pulsar and the game servers' folders.
es.UninstalledAll=%1 se quitó de esta PC.%n%nSe conservan, para que los quites cuando nada los necesite: la base de datos y PostgreSQL (en Aplicaciones), los runtimes de .NET (en Aplicaciones), C:\ProgramData\Pulsar y las carpetas de los servidores de juegos.

[CustomMessages]
en.NodeCaption=Join a panel
es.NodeCaption=Unirse a un panel
en.NodeDescription=The panel's address and the node code from Nodes > Add node.
es.NodeDescription=La dirección del panel y el código del nodo de Nodos > Agregar nodo.
en.NodeSubCaption=In the panel, open Nodes > Add node and name the node: the dialog shows the panel's address and a node code (pulsar-node-1....). Paste the code here, and the address too when this PC reaches the panel at another address than the code has. The code works once, for 30 minutes: keep it secret.
es.NodeSubCaption=En el panel, abrí Nodos > Agregar nodo y ponele un nombre al nodo: el diálogo muestra la dirección del panel y un código del nodo (pulsar-node-1....). Pegá acá el código, y también la dirección si esta PC llega al panel por otra dirección que la del código. El código sirve una sola vez, durante 30 minutos: mantenelo en secreto.
en.NodeAddress=Panel &address (optional: the code has it):
es.NodeAddress=&Dirección del panel (opcional: el código la tiene):
en.NodeCode=Node &code:
es.NodeCode=&Código del nodo:
en.FolderCaption=Game servers' folder
es.FolderCaption=Carpeta de los servidores de juegos
en.FolderDescription=Where this node installs game servers.
es.FolderDescription=Dónde instala este nodo los servidores de juegos.
en.FolderSubCaption=Games need a lot of space (Counter-Strike 2, about 85 GB). Choose a new or empty folder on a local drive, not one inside a folder other users may change.
es.FolderSubCaption=Los juegos ocupan mucho espacio (Counter-Strike 2, unos 85 GB). Elegí una carpeta nueva o vacía en un disco local, que no esté dentro de una carpeta que otros usuarios puedan cambiar.
en.ErrorFolder=Choose a folder on a local drive, such as %1.
es.ErrorFolder=Elegí una carpeta en un disco local, como %1.
en.ErrorAddress=The panel's address is https://, the panel's name or IP address and its port, and nothing more, like https://panel.example.com:8443 (no path, ? or #, no user name, and no :443, which https:// already means).
es.ErrorAddress=La dirección del panel es https://, el nombre o la dirección IP del panel y su puerto, y nada más, como https://panel.example.com:8443 (sin ruta, ? ni #, sin nombre de usuario, y sin :443, que https:// ya implica).
en.ErrorNodeCode=That is not a node code: copy the whole code Nodes > Add node shows (it starts with pulsar-node-1.).
es.ErrorNodeCode=Eso no es un código de nodo: copiá el código completo que muestra Nodos > Agregar nodo (empieza con pulsar-node-1.).
en.ErrorUnsafe=%1 contains a character the setup does not accept (a quote or a control character).
es.ErrorUnsafe=%1 tiene un carácter que el instalador no acepta (una comilla o un carácter de control).
en.MemoInstall=Install Pulsar %1:
es.MemoInstall=Instalar Pulsar %1:
en.MemoServers=Game servers: %1
es.MemoServers=Servidores de juegos: %1
en.MemoNodeOf=Panel: %1
es.MemoNodeOf=Panel: %1
en.MemoNodeOfCode=Panel: the address in the node code
es.MemoNodeOfCode=Panel: la dirección del código del nodo
en.MemoDownloads=What this PC lacks of %1 is downloaded over HTTPS and checked before it runs. A download that fails, or fails a check, is not run, and nothing is changed.
es.MemoDownloads=Lo que le falta a esta PC de %1 se descarga por HTTPS y se comprueba antes de ejecutarlo. Una descarga que falla, o que no pasa una comprobación, no se ejecuta, y no se cambia nada.
en.MemoDotNet=the newest .NET 10 runtimes (Microsoft)
es.MemoDotNet=los runtimes de .NET 10 más nuevos (Microsoft)
en.MemoDotNetPostgres=the newest .NET 10 runtimes (Microsoft) and PostgreSQL 18 (postgresql.org and EDB)
es.MemoDotNetPostgres=los runtimes de .NET 10 más nuevos (Microsoft) y PostgreSQL 18 (postgresql.org y EDB)
en.MemoUpdate=Update Pulsar on this PC (%1) to version %2. Settings, data, nodes and game servers are kept, and game servers keep running.
es.MemoUpdate=Actualizar Pulsar en esta PC (%1) a la versión %2. Se conservan la configuración, los datos, los nodos y los servidores de juegos, que siguen corriendo.
en.MemoLog=The installer's log goes to %1.
es.MemoLog=El log del instalador queda en %1.
en.PartsBoth=the panel and the agent
es.PartsBoth=el panel y el agente
en.PartsPanel=the panel
es.PartsPanel=el panel
en.PartsAgent=the agent
es.PartsAgent=el agente
en.WelcomeUpdate=Pulsar is installed on this PC (%1). This setup updates it to version %2, keeping its settings, data, nodes and game servers.%n%nClick Next to continue.
es.WelcomeUpdate=Pulsar está instalado en esta PC (%1). Este instalador lo actualiza a la versión %2 y conserva su configuración, sus datos, sus nodos y sus servidores de juegos.%n%nHacé clic en Siguiente para continuar.
en.StatusStarting=Starting Pulsar's installer...
es.StatusStarting=Iniciando el instalador de Pulsar...
en.StatusStep=Step %1 of %2: %3
es.StatusStep=Paso %1 de %2: %3
en.OutcomeNotInstalled=Pulsar was not installed.
es.OutcomeNotInstalled=Pulsar no se instaló.
en.OutcomeNotRemoved=Pulsar was not removed: fix what the message says and remove it from Apps again.
es.OutcomeNotRemoved=Pulsar no se quitó: corregí lo que dice el mensaje y volvé a quitarlo desde Aplicaciones.
en.OutcomeFailed=The installer stopped partway, and part of Pulsar may be installed. Remove Pulsar from Settings > Apps (that removes what was left), fix what the message says and run this setup again.
es.OutcomeFailed=El instalador se detuvo a mitad de camino y parte de Pulsar puede haber quedado instalada. Quitá Pulsar desde Configuración > Aplicaciones (eso quita lo que quedó), corregí lo que dice el mensaje y volvé a ejecutar este instalador.
en.OutcomeUsage=Something it was given is wrong or missing (a password, the node code, a port or a file), or what it would install is there already.
es.OutcomeUsage=Algo de lo que se le dio está mal o falta (una contraseña, el código del nodo, un puerto o un archivo), o lo que instalaría ya está.
en.OutcomeUnsupported=This PC cannot install Pulsar as it is: Windows updates may be missing, or a program, port, service or folder Pulsar needs is taken.
es.OutcomeUnsupported=Esta PC no puede instalar Pulsar tal como está: puede que le falten actualizaciones de Windows, o que un programa, puerto, servicio o carpeta que Pulsar necesita esté ocupado.
en.OutcomeOffline=A download, or the panel at the node code's address, could not be reached. Check this PC's internet connection (and that the panel runs at that address) and run the setup again. Without internet, see "Offline installs" in the install guide.
es.OutcomeOffline=No se pudo llegar a una descarga, o al panel en la dirección del código del nodo. Revisá la conexión a internet de esta PC (y que el panel esté funcionando en esa dirección) y volvé a ejecutar el instalador. Sin internet, mirá "Offline installs" en la guía de instalación.
en.OutcomeUntrusted=A download failed its checks (signature, hash or version), so it was not run.
es.OutcomeUntrusted=Una descarga no pasó sus comprobaciones (firma, hash o versión), así que no se ejecutó.
en.OutcomeCertificate=The certificate the panel presents is not the one the node code names (another machine, or a proxy in front of the panel that serves its own), or it may not be pinned. Check that the address leads to your panel, and create a new code in Nodes > Add node, opened at the address nodes use.
es.OutcomeCertificate=El certificado que presenta el panel no es el que nombra el código del nodo (otra máquina, o un proxy delante del panel que sirve el suyo), o no se puede fijar. Comprobá que la dirección lleve a tu panel y creá un código nuevo en Nodos > Agregar nodo, abierto desde la dirección que usan los nodos.
en.OutcomeUnknown=The installer ended unexpectedly (exit code %1).
es.OutcomeUnknown=El instalador terminó de forma inesperada (código de salida %1).
en.OutcomeNotStarted=Windows PowerShell could not be started: %1
es.OutcomeNotStarted=No se pudo iniciar Windows PowerShell: %1
en.OutcomeDetails=Details: %1
es.OutcomeDetails=Detalle: %1
en.OutcomeLog=The installer's log: %1
es.OutcomeLog=El log del instalador: %1
en.FinishedFailedHeading=Pulsar is not fully installed
es.FinishedFailedHeading=Pulsar no quedó instalado del todo
en.FinishedAgent=This PC is a node of the panel now: it shows as online in Nodes.
es.FinishedAgent=Esta PC ya es un nodo del panel: aparece como conectado en Nodos.
en.FinishedUpdate=Pulsar is updated.
es.FinishedUpdate=Pulsar está actualizado.
en.FinishedRestart=Restart Windows when you can: a .NET installer asks for it to finish. Pulsar runs meanwhile.
es.FinishedRestart=Reiniciá Windows cuando puedas: un instalador de .NET lo pide para terminar. Mientras tanto Pulsar funciona.
en.FinishedLog=The installer's log: %1
es.FinishedLog=El log del instalador: %1
en.FinishedAddress=The panel's sign-in page (select it and copy it with Ctrl+C, then open it in your browser):
es.FinishedAddress=La página de inicio de sesión del panel (seleccionala y copiala con Ctrl+C, y después abrila en tu navegador):
en.NewerInstalled=Pulsar %1 is installed on this PC, which is newer than this setup's version (%2). A setup cannot go back to an older version.
es.NewerInstalled=En esta PC está instalado Pulsar %1, más nuevo que la versión de este instalador (%2). Un instalador no puede volver a una versión anterior.
en.WrongFolder=This setup installs into %1 only.
es.WrongFolder=Este instalador instala solo en %1.
en.UninstallScriptMissing=Install-Pulsar.ps1 is missing from %1, so Pulsar's services cannot be removed from here. Run Install-Pulsar.ps1 -Uninstall from the release ZIP, then remove Pulsar from Apps again.
es.UninstallScriptMissing=Falta Install-Pulsar.ps1 en %1, así que desde acá no se pueden quitar los servicios de Pulsar. Ejecutá Install-Pulsar.ps1 -Uninstall desde el ZIP de la versión y después volvé a quitar Pulsar desde Aplicaciones.
en.OfflineParameter=The /%1= parameter
es.OfflineParameter=El parámetro /%1=

[InstallDelete]
; The payload is the new release's alone (Install-Pulsar.ps1 copies all of panel and agent into Program Files). These
; are deleted once an install ends anyway (deleteafterinstall); Install-Pulsar.ps1 stays, for the uninstaller.
Type: filesandordirs; Name: "{app}\payload\panel"
Type: filesandordirs; Name: "{app}\payload\agent"
Type: files; Name: "{app}\payload\Backup-Pulsar.ps1"
Type: files; Name: "{app}\payload\release.json"

[UninstallDelete]
Type: dirifempty; Name: "{app}\payload"
Type: dirifempty; Name: "{commonpf64}\Pulsar"

; No [Run] entry opens the panel: started elevated (by an administrator's own elevated prompt, a deployment tool),
; runasoriginaluser has no other user to run as, and the browser would run as administrator. The Finished page shows
; the address to copy instead (ShowFinishedText).

[Code]
const
  // Install-Pulsar.ps1's exit codes with -Unattended (docs/guides/install.md, "Unattended installs").
  ExitFailed = 1;
  ExitUsage = 2;
  ExitUnsupported = 3;
  ExitOffline = 4;
  ExitUntrusted = 5;
  ExitCertificate = 6;
  ExitRestart = 3010;
  // This setup's own exit code when Install-Pulsar.ps1 failed after changing something (GetCustomSetupExitCode).
  SetupExitPartial = 100;
  ServicesKey = 'SYSTEM\CurrentControlSet\Services';
  UninstallKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall';
  Paragraph = #13#10#13#10;

var
  // What this PC has: the services Install-Pulsar.ps1 installs (the agent's unless the agent MSI installed it, which
  // the script leaves alone) and a PostgreSQL service.
  PanelInstalled: Boolean;
  AgentInstalled: Boolean;
  AgentFromMsi: Boolean;
  PostgresFound: Boolean;
  // The last run of Install-Pulsar.ps1: whether it started, how it ended, and its PULSAR-* lines.
  RunStarted: Boolean;
  RunLogPath: String;
  RunPanelUrl: String;
  RunError: String;
  // It failed after changing something: the setup's entry in Apps stays, so that removing it removes what was left.
  RunKept: Boolean;
  RestartNeeded: Boolean;

// --- The environment: how the secrets reach Install-Pulsar.ps1 ---------------------------------------------------

function SetEnvironmentVariable(lpName, lpValue: String): BOOL;
  external 'SetEnvironmentVariableW@kernel32.dll stdcall';
// The same function with a null value, which removes the variable.
function RemoveEnvironmentVariable(lpName: String; lpValue: UINT_PTR): BOOL;
  external 'SetEnvironmentVariableW@kernel32.dll stdcall';

// Removes from this process's environment every variable Install-Pulsar.ps1 takes a secret from.
procedure ClearSecretVariables;
begin
  RemoveEnvironmentVariable('PULSAR_ADMIN_PASSWORD', 0);
  RemoveEnvironmentVariable('PULSAR_POSTGRES_PASSWORD', 0);
  RemoveEnvironmentVariable('PULSAR_CONNECTION_STRING', 0);
  RemoveEnvironmentVariable('PULSAR_CERTIFICATE_PASSWORD', 0);
  RemoveEnvironmentVariable('PULSAR_ENROLLMENT_TOKEN', 0);
  RemoveEnvironmentVariable('PULSAR_NODE_CODE', 0);
  RemoveEnvironmentVariable('PULSAR_SECRETS_FILE', 0);
end;

// Puts a secret in this process's environment for Install-Pulsar.ps1 to inherit (nothing when it is empty).
procedure SetSecretVariable(const Name, Value: String);
begin
  if Value = '' then
    Exit;
  if not SetEnvironmentVariable(Name, Value) then
    RaiseException('Could not set ' + Name + ' in this process''s environment.');
end;

// --- Values on a command line ------------------------------------------------------------------------------------

// Whether a value may go on Install-Pulsar.ps1's command line: no quote and no control character (the pages refuse
// others; secrets never go there).
function IsSafeArgument(const Value: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to Length(Value) do
    if (Ord(Value[I]) < 32) or (Ord(Value[I]) = 127) or (Value[I] = '"') then
      Exit;
  Result := True;
end;

// One argument, as Windows programs read it back: in quotes, with the backslashes before the closing quote doubled
// (otherwise "D:\" would escape it).
function QuoteArgument(const Value: String): String;
var
  I: Integer;
  Tail: String;
begin
  Tail := '';
  I := Length(Value);
  while I > 0 do begin
    if Value[I] <> '\' then
      Break;
    Tail := Tail + '\';
    I := I - 1;
  end;
  Result := '"' + Value + Tail + '"';
end;

// " -<Name> "<Value>"" for Install-Pulsar.ps1's command line. A value the pages should have refused stops the install
// (and rolls it back) rather than reach the command line.
function NamedArgument(const Name, Value: String): String;
begin
  if not IsSafeArgument(Value) then
    RaiseException(FmtMessage(CustomMessage('ErrorUnsafe'), ['-' + Name]));
  Result := ' -' + Name + ' ' + QuoteArgument(Value);
end;

// A folder on a local drive ("D:\Games\Pulsar"), as the agent takes one: a full path with a drive letter.
function IsLocalFolder(const Path: String): Boolean;
var
  Drive: Char;
begin
  Result := False;
  // Lengths first, and characters only then: an index past the end is an error.
  if (Length(Path) < 3) or (Length(Path) > 240) then
    Exit;
  if (Path[2] <> ':') or (Path[3] <> '\') then
    Exit;
  Drive := Path[1];
  if not (((Drive >= 'A') and (Drive <= 'Z')) or ((Drive >= 'a') and (Drive <= 'z'))) then
    Exit;
  Result := IsSafeArgument(Path) and not PathHasInvalidCharacters(Path, True) and (Pos('\..', Path + '\') = 0);
end;

// A panel's address as the Add node dialog shows it (https://panel.example.com:8443), in the one form Install-Pulsar.ps1
// takes (Test-PanelUrl, as Pulsar.Contracts.NodeCode writes origins): the scheme and the host in lower case, the port
// only when it is not 443, nothing after it. One final '/' is dropped; anything else (a path, a query, a fragment, a
// user name, the port 443, a character no host name has) is refused. True, with that form in Normal, or False.
function NormalizePanelAddress(const Address: String; var Normal: String): Boolean;
var
  Rest, Host, PortText: String;
  I, Colon, Number: Integer;
  C: Char;
begin
  Result := False;
  Normal := '';
  if (Length(Address) <= 8) or (Length(Address) > 512) or not SameText(Copy(Address, 1, 8), 'https://') then
    Exit;
  Rest := Copy(Address, 9, Length(Address));
  if Rest[Length(Rest)] = '/' then
    Rest := Copy(Rest, 1, Length(Rest) - 1);
  // An IPv6 address in brackets, or a name or an IPv4 address; then ':' and the port, or nothing.
  if Copy(Rest, 1, 1) = '[' then begin
    Colon := Pos(']', Rest);
    if Colon < 3 then
      Exit;
    Host := Copy(Rest, 1, Colon);
    PortText := Copy(Rest, Colon + 1, Length(Rest));
    for I := 2 to Length(Host) - 1 do begin
      C := Host[I];
      if not (((C >= '0') and (C <= '9')) or ((C >= 'a') and (C <= 'f')) or ((C >= 'A') and (C <= 'F')) or (C = ':') or (C = '.')) then
        Exit;
    end;
  end else begin
    Colon := Pos(':', Rest);
    if Colon = 0 then
      Colon := Length(Rest) + 1;
    Host := Copy(Rest, 1, Colon - 1);
    PortText := Copy(Rest, Colon, Length(Rest));
    if (Host = '') or (Host[1] = '.') or (Pos('..', Host) > 0) then
      Exit;
    for I := 1 to Length(Host) do begin
      C := Host[I];
      if not (((C >= '0') and (C <= '9')) or ((C >= 'a') and (C <= 'z')) or ((C >= 'A') and (C <= 'Z')) or (C = '.') or (C = '-') or (C = '_')) then
        Exit;
    end;
  end;
  // ':' and 1 to 65535 without a leading zero, never 443 (https's own, which an origin leaves out).
  if PortText <> '' then begin
    if (Length(PortText) < 2) or (Length(PortText) > 6) or (PortText[1] <> ':') or (PortText[2] = '0') then
      Exit;
    for I := 2 to Length(PortText) do
      if (PortText[I] < '0') or (PortText[I] > '9') then
        Exit;
    Number := StrToIntDef(Copy(PortText, 2, Length(PortText)), 0);
    if (Number < 1) or (Number > 65535) or (Number = 443) then
      Exit;
  end;
  Normal := 'https://' + Lowercase(Host) + PortText;
  Result := True;
end;

// The shape of a node code ("pulsar-node-1." and base64url, at most 2048 characters): Install-Pulsar.ps1 reads it
// strictly, as Pulsar.Contracts.NodeCode does; this only catches a code copied in part.
function IsNodeCode(const Code: String): Boolean;
var
  I: Integer;
  C: Char;
begin
  Result := False;
  if (Length(Code) <= 14) or (Length(Code) > 2048) or (Copy(Code, 1, 14) <> 'pulsar-node-1.') then
    Exit;
  for I := 15 to Length(Code) do begin
    C := Code[I];
    if not (((C >= 'A') and (C <= 'Z')) or ((C >= 'a') and (C <= 'z')) or ((C >= '0') and (C <= '9')) or (C = '-') or (C = '_')) then
      Exit;
  end;
  Result := True;
end;

// Shows a page's error (or, when silent, logs it: Setup then ends) and returns False, for NextButtonClick.
function Refuse(const Message: String): Boolean;
begin
  Log('Refused: ' + Message);
  SuppressibleMsgBox(Message, mbError, MB_OK, IDOK);
  Result := False;
end;

// --- What this PC has --------------------------------------------------------------------------------------------

function ServiceExists(const Name: String): Boolean;
begin
  Result := RegKeyExists(HKLM64, ServicesKey + '\' + Name);
end;

// The agent MSI's entry in Apps (DisplayName "Pulsar Agent", a Windows Installer package), as Install-Pulsar.ps1 finds it.
function AgentPackageInstalled: Boolean;
var
  Names: TArrayOfString;
  I: Integer;
  Name: String;
  Flag: Cardinal;
begin
  Result := False;
  if not RegGetSubkeyNames(HKLM64, UninstallKey, Names) then
    Exit;
  for I := 0 to GetArrayLength(Names) - 1 do
    if RegQueryStringValue(HKLM64, UninstallKey + '\' + Names[I], 'DisplayName', Name) then
      if Name = 'Pulsar Agent' then
        if RegQueryDWordValue(HKLM64, UninstallKey + '\' + Names[I], 'WindowsInstaller', Flag) then
          if Flag = 1 then begin
            Result := True;
            Exit;
          end;
end;

// A PostgreSQL service (postgresql-x64-18, say, run by its pg_ctl.exe), as Install-Pulsar.ps1 looks for one.
function PostgresServiceExists: Boolean;
var
  Names: TArrayOfString;
  I: Integer;
  ImagePath: String;
begin
  Result := False;
  if not RegGetSubkeyNames(HKLM64, ServicesKey, Names) then
    Exit;
  for I := 0 to GetArrayLength(Names) - 1 do
    if Pos('postgresql', Lowercase(Names[I])) = 1 then
      if RegQueryStringValue(HKLM64, ServicesKey + '\' + Names[I], 'ImagePath', ImagePath) then
        if Pos('\bin\pg_ctl.exe', Lowercase(ImagePath)) > 0 then begin
          Result := True;
          Exit;
        end;
end;

procedure DetectInstalled;
begin
  AgentFromMsi := AgentPackageInstalled;
  PanelInstalled := ServiceExists('PulsarPanel');
  AgentInstalled := ServiceExists('PulsarAgent') and not AgentFromMsi;
  PostgresFound := PostgresServiceExists;
end;

// Pulsar is installed: the setup updates it (Install-Pulsar.ps1 -Update) and asks nothing.
function UpdateMode: Boolean;
begin
  Result := PanelInstalled or AgentInstalled;
end;

function InstalledParts: String;
begin
  if PanelInstalled and AgentInstalled then
    Result := CustomMessage('PartsBoth')
  else if PanelInstalled then
    Result := CustomMessage('PartsPanel')
  else
    Result := CustomMessage('PartsAgent');
end;

// The numbers of a version ("1.2.3-beta" gives "1.2.3").
function NumericVersion(const Version: String): String;
var
  I: Integer;
begin
  Result := Version;
  for I := 1 to Length(Version) do
    if (Version[I] = '-') or (Version[I] = '+') then begin
      Result := Copy(Version, 1, I - 1);
      Exit;
    end;
end;

// A program of a newer Pulsar installed here ($Path: Pulsar.Api.exe or PulsarAgent.exe, by its file version, whoever
// installed it: a setup, the ZIP's script, or by hand), as $Installed.
function NewerProgramInstalled(const Path: String; const Current: Int64; var Installed: String): Boolean;
var
  Version: Int64;
begin
  Result := False;
  if not FileExists(Path) or not GetPackedVersion(Path, Version) then
    Exit;
  if ComparePackedVersion(Version, Current) > 0 then begin
    if not GetVersionNumbersString(Path, Installed) then
      Installed := Path;
    Result := True;
  end;
end;

// A newer Pulsar installed here ($Installed): its database may have changes an older panel cannot read, and an older
// agent may not speak its panel's protocol. The version a setup recorded, and the installed programs' own versions
// (Install-Pulsar.ps1 -Update refuses an older release too).
function NewerVersionInstalled(var Installed: String): Boolean;
var
  Previous, Current: Int64;
begin
  Result := False;
  if not StrToVersion('{#NumericVersion}', Current) then
    Exit;
  if RegQueryStringValue(HKLM64, UninstallKey + '\{{#AppGuid}}_is1', 'DisplayVersion', Installed) then
    if StrToVersion(NumericVersion(Installed), Previous) and (ComparePackedVersion(Previous, Current) > 0) then begin
      Result := True;
      Exit;
    end;
  Result := NewerProgramInstalled(ExpandConstant('{commonpf64}\Pulsar\Panel\Pulsar.Api.exe'), Current, Installed) or
    NewerProgramInstalled(ExpandConstant('{commonpf64}\Pulsar\Agent\PulsarAgent.exe'), Current, Installed);
end;

// The installers given for an offline install (docs/guides/install.md, "Offline installs"), passed on to the script.
function OfflineDotNetInstallers: String;
begin
  Result := ExpandConstant('{param:DOTNETINSTALLERS|}');
end;

function OfflinePostgresInstaller: String;
begin
  Result := ExpandConstant('{param:POSTGRESINSTALLER|}');
end;

<event('InitializeSetup')>
function CheckThisMachine: Boolean;
var
  Installed: String;
begin
  Result := False;
  DetectInstalled;
  if NewerVersionInstalled(Installed) then begin
    SuppressibleMsgBox(FmtMessage(CustomMessage('NewerInstalled'), [Installed, '{#Version}']), mbError, MB_OK, IDOK);
    Exit;
  end;
  if not IsSafeArgument(OfflineDotNetInstallers) then begin
    SuppressibleMsgBox(FmtMessage(CustomMessage('ErrorUnsafe'), [FmtMessage(CustomMessage('OfflineParameter'), ['DOTNETINSTALLERS'])]), mbError, MB_OK, IDOK);
    Exit;
  end;
  if not IsSafeArgument(OfflinePostgresInstaller) then begin
    SuppressibleMsgBox(FmtMessage(CustomMessage('ErrorUnsafe'), [FmtMessage(CustomMessage('OfflineParameter'), ['POSTGRESINSTALLER'])]), mbError, MB_OK, IDOK);
    Exit;
  end;
  Result := True;
end;

// The payload goes to C:\Program Files\Pulsar\Setup and nowhere else: Install-Pulsar.ps1 refuses a folder others can
// change, and the uninstaller runs the copy there.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not PathSame(ExpandConstant('{app}'), ExpandConstant('{commonpf64}\Pulsar\Setup')) then
    Result := FmtMessage(CustomMessage('WrongFolder'), [ExpandConstant('{commonpf64}\Pulsar\Setup')]);
end;

// --- Running Install-Pulsar.ps1 ----------------------------------------------------------------------------------

// Shows what the installer is doing: the step (and how far along it is) and its latest detail.
procedure ShowProgress(const Status, Detail: String; const Done, Total: Integer);
var
  Gauge: TNewProgressBar;
begin
  if IsUninstaller then begin
    if Detail <> '' then
      UninstallProgressForm.StatusLabel.Caption := Detail
    else
      UninstallProgressForm.StatusLabel.Caption := Status;
    Gauge := UninstallProgressForm.ProgressBar;
  end
  else begin
    if Status <> '' then
      WizardForm.StatusLabel.Caption := Status;
    WizardForm.FilenameLabel.Caption := Detail;
    Gauge := WizardForm.ProgressGauge;
  end;
  if Total > 0 then
    Gauge.Position := Gauge.Min + MulDiv(Gauge.Max - Gauge.Min, Done, Total);
end;

// A "PULSAR-STEP <n>/<total> <text>" line's step.
procedure ShowStep(const Rest: String);
var
  Space, Slash, Step, Total: Integer;
  Counter: String;
begin
  Space := Pos(' ', Rest);
  if Space = 0 then
    Exit;
  Counter := Copy(Rest, 1, Space - 1);
  Slash := Pos('/', Counter);
  if Slash = 0 then
    Exit;
  Step := StrToIntDef(Copy(Counter, 1, Slash - 1), 0);
  Total := StrToIntDef(Copy(Counter, Slash + 1, Length(Counter)), 0);
  if (Step < 1) or (Step > Total) then
    Exit;
  ShowProgress(FmtMessage(CustomMessage('StatusStep'), [IntToStr(Step), IntToStr(Total), Copy(Rest, Space + 1, Length(Rest))]), '', Step - 1, Total);
end;

// https://localhost:<port>/ and nothing else: the only address the setup opens in a browser.
function IsLocalPanelUrl(const Url: String): Boolean;
var
  Port: String;
  I: Integer;
begin
  Result := False;
  if (Length(Url) < 20) or (Length(Url) > 24) then
    Exit;
  if (Copy(Url, 1, 18) <> 'https://localhost:') or (Url[Length(Url)] <> '/') then
    Exit;
  Port := Copy(Url, 19, Length(Url) - 19);
  for I := 1 to Length(Port) do
    if (Port[I] < '0') or (Port[I] > '9') then
      Exit;
  Result := (StrToIntDef(Port, 0) >= 1) and (StrToIntDef(Port, 0) <= 65535);
end;

// Each line Install-Pulsar.ps1 prints (secrets already replaced by it). Not written to this setup's log: the script
// keeps its own.
procedure ScriptOutput(const S: String; const Error, FirstLine: Boolean);
var
  Line: String;
begin
  // It runs with its own copy of this process's environment by now.
  if FirstLine then
    ClearSecretVariables;
  if Error then begin
    Log('Install-Pulsar.ps1''s output could not be read further: ' + S);
    Exit;
  end;
  Line := Trim(S);
  if Copy(Line, 1, 11) = 'PULSAR-LOG ' then
    RunLogPath := Copy(Line, 12, Length(Line))
  else if Copy(Line, 1, 12) = 'PULSAR-STEP ' then
    ShowStep(Copy(Line, 13, Length(Line)))
  else if Copy(Line, 1, 17) = 'PULSAR-PANEL-URL ' then begin
    if IsLocalPanelUrl(Copy(Line, 18, Length(Line))) then
      RunPanelUrl := Copy(Line, 18, Length(Line));
  end
  else if Copy(Line, 1, 13) = 'PULSAR-ERROR ' then begin
    // "PULSAR-ERROR <code> <message>": the message.
    RunError := Copy(Line, 14, Length(Line));
    if Pos(' ', RunError) > 0 then
      RunError := Copy(RunError, Pos(' ', RunError) + 1, Length(RunError));
  end
  else if Copy(Line, 1, 4) = '==> ' then
    ShowProgress('', Copy(Line, 5, Length(Line)), 0, 0);
end;

// Runs {app}\payload\Install-Pulsar.ps1 -Unattended with $Arguments (never a secret) in 64-bit Windows PowerShell,
// waiting for it while the wizard shows its progress. Its exit code; RunStarted says whether it started at all.
function RunInstallScript(const Arguments: String): Integer;
var
  Folder, Params: String;
  Code: Integer;
begin
  RunStarted := False;
  RunLogPath := '';
  RunPanelUrl := '';
  RunError := '';
  Code := -1;
  Folder := ExpandConstant('{app}\payload');
  Params := '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' + QuoteArgument(Folder + '\Install-Pulsar.ps1') + ' -Unattended' + Arguments;
  Log('Running Install-Pulsar.ps1 -Unattended' + Arguments);
  ShowProgress(CustomMessage('StatusStarting'), '', 0, 1);
  try
    try
      RunStarted := ExecAndLogOutput(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Params, Folder,
        SW_SHOWNORMAL, ewWaitUntilTerminated, Code, @ScriptOutput);
      if not RunStarted then
        RunError := SysErrorMessage(Code);
    except
      RunStarted := False;
      RunError := GetExceptionMessage;
    end;
  finally
    ClearSecretVariables;
  end;
  if RunStarted then
    Log('Install-Pulsar.ps1 ended with exit code ' + IntToStr(Code) + '. Its log: ' + RunLogPath)
  else
    Log('Windows PowerShell could not be started: ' + RunError);
  Result := Code;
end;

// What a run that did not succeed means, for the person at the PC: by exit code, with the script's message and log.
function OutcomeText(const Code: Integer; const Uninstalling: Boolean): String;
begin
  if not RunStarted then
    Result := FmtMessage(CustomMessage('OutcomeNotStarted'), [RunError])
  else begin
    if Uninstalling then
      Result := CustomMessage('OutcomeNotRemoved')
    else begin
      case Code of
        ExitFailed: Result := CustomMessage('OutcomeFailed');
        ExitUsage: Result := CustomMessage('OutcomeUsage');
        ExitUnsupported: Result := CustomMessage('OutcomeUnsupported');
        ExitOffline: Result := CustomMessage('OutcomeOffline');
        ExitUntrusted: Result := CustomMessage('OutcomeUntrusted');
        ExitCertificate: Result := CustomMessage('OutcomeCertificate');
      else
        Result := FmtMessage(CustomMessage('OutcomeUnknown'), [IntToStr(Code)]);
      end;
    end;
    if RunError <> '' then
      Result := Result + Paragraph + FmtMessage(CustomMessage('OutcomeDetails'), [RunError]);
  end;
  if RunLogPath <> '' then
    Result := Result + Paragraph + FmtMessage(CustomMessage('OutcomeLog'), [RunLogPath]);
end;

// The release's programs in the payload, once Install-Pulsar.ps1 is done with them (it copied what it installs into
// Program Files\Pulsar): deleteafterinstall deletes their files, this their folders too. Install-Pulsar.ps1 stays.
procedure RemovePayloadPrograms;
begin
  DelTree(ExpandConstant('{app}\payload\panel'), True, True, True);
  DelTree(ExpandConstant('{app}\payload\agent'), True, True, True);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    RemovePayloadPrograms;
end;

// Runs the install (from the payload's last file, AfterInstall) and decides what the setup does next. Done: on to the
// Finished page. Done with changes left (exit code 1, or one it does not know): the same, saying so, the entry in Apps
// kept. Nothing changed (2 to 6), or PowerShell did not start: the setup rolls back its own files and says why (an
// error that stops a silent setup, or a message and a silent stop).
procedure RunInstallStep(const Arguments: String);
var
  Code: Integer;
  Text: String;
begin
  Code := RunInstallScript(Arguments);
  if RunStarted and ((Code = 0) or (Code = ExitRestart)) then begin
    RestartNeeded := Code = ExitRestart;
    Exit;
  end;
  if RunStarted and ((Code < ExitUsage) or (Code > ExitCertificate)) then begin
    RunKept := True;
    Exit;
  end;
  Text := CustomMessage('OutcomeNotInstalled') + Paragraph + OutcomeText(Code, False);
  Log('Nothing was installed: rolling back.');
  // So that the rollback, which removes the folders it made only when they are empty, leaves nothing.
  RemovePayloadPrograms;
  if WizardSilent then
    RaiseException(Text);
  SuppressibleMsgBox(Text, mbError, MB_OK, IDOK);
  Abort;
end;

function HasPanelUrl: Boolean;
begin
  Result := (RunPanelUrl <> '') and not RunKept;
end;

// The Finished page: what was done ($Done) and the log, or why it is not fully installed; and the panel's sign-in page
// (PULSAR-PANEL-URL, only ever https://localhost:<port>/) in a read-only box to copy, never opened from here.
procedure ShowFinishedText(const Done: String);
var
  Text: String;
  Address: TNewEdit;
begin
  if RunKept then begin
    WizardForm.FinishedHeadingLabel.Caption := CustomMessage('FinishedFailedHeading');
    Text := OutcomeText(ExitFailed, False);
  end
  else begin
    Text := Done;
    if RestartNeeded then
      Text := Text + Paragraph + CustomMessage('FinishedRestart');
    if RunLogPath <> '' then
      Text := Text + Paragraph + FmtMessage(CustomMessage('FinishedLog'), [RunLogPath]);
    if HasPanelUrl then
      Text := Text + Paragraph + CustomMessage('FinishedAddress');
  end;
  WizardForm.FinishedLabel.Caption := Text;
  WizardForm.AdjustLabelHeight(WizardForm.FinishedLabel);
  if HasPanelUrl then begin
    Address := TNewEdit.Create(WizardForm);
    Address.Parent := WizardForm.FinishedPage;
    Address.ReadOnly := True;
    Address.Left := WizardForm.FinishedLabel.Left;
    Address.Width := WizardForm.FinishedLabel.Width;
    Address.Top := WizardForm.FinishedLabel.Top + WizardForm.FinishedLabel.Height + ScaleY(8);
    Address.Text := RunPanelUrl;
  end;
end;

function NeedRestart: Boolean;
begin
  Result := RestartNeeded;
end;

function GetCustomSetupExitCode: Integer;
begin
  Result := 0;
  if RunKept then
    Result := SetupExitPartial;
end;

// --- Uninstall ---------------------------------------------------------------------------------------------------

// Before the uninstaller removes its files: Install-Pulsar.ps1 -Uninstall removes the services, programs, firewall rules
// and the certificate trust it gave, and keeps the data. When it fails, nothing more is removed and the entry in Apps
// stays, to try again.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Integer;
begin
  if CurUninstallStep <> usUninstall then
    Exit;
  DetectInstalled;
  if not FileExists(ExpandConstant('{app}\payload\Install-Pulsar.ps1')) then begin
    if PanelInstalled or AgentInstalled then begin
      SuppressibleMsgBox(FmtMessage(CustomMessage('UninstallScriptMissing'), [ExpandConstant('{app}\payload')]), mbError, MB_OK, IDOK);
      Abort;
    end;
    Exit;
  end;
  Code := RunInstallScript(' -Uninstall');
  if RunStarted and (Code = 0) then
    Exit;
  SuppressibleMsgBox(OutcomeText(Code, True), mbError, MB_OK, IDOK);
  Abort;
end;
