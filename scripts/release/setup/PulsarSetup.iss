; PulsarSetup.exe (Phase 18, docs/guides/install.md "Easy install"): the wizard that installs Pulsar on this PC (the
; panel and this PC as a node, the panel only, or a node only) or updates it, with Install-Pulsar.ps1 doing the work.
; Common.iss says how; Build-Release.ps1 compiles it.

#include "Common.iss"

[Setup]
OutputBaseFilename=PulsarSetup-{#Version}
VersionInfoDescription=Pulsar setup

[Messages]
en.WelcomeLabel2=This will install [name/ver] on your computer: the panel (Pulsar's web interface), this PC as a node that runs game servers, or both.%n%nSetup downloads what this PC lacks, the newest .NET 10 runtimes and, for the panel, PostgreSQL 18, and checks every download before running it. At the end it shows the address of the panel's sign-in page, to copy into your browser.
es.WelcomeLabel2=Esto va a instalar [name/ver] en tu computadora: el panel (la interfaz web de Pulsar), esta PC como un nodo que corre servidores de juegos, o las dos cosas.%n%nEl instalador descarga lo que le falta a esta PC, los runtimes de .NET 10 más nuevos y, para el panel, PostgreSQL 18, y comprueba cada descarga antes de ejecutarla. Al final muestra la dirección de la página de inicio de sesión del panel, para que la copies en tu navegador.

[CustomMessages]
en.ModeCaption=What to install
es.ModeCaption=Qué instalar
en.ModeDescription=Choose what this PC becomes.
es.ModeDescription=Elegí qué va a ser esta PC.
en.ModeSubCaption=Pulsar has a panel (the web interface, with its database) and an agent on every PC that runs game servers (a node).
es.ModeSubCaption=Pulsar tiene un panel (la interfaz web, con su base de datos) y un agente en cada PC que corre servidores de juegos (un nodo).
en.ModeAllInOne=The panel, and this PC as a node (everything on this PC)
es.ModeAllInOne=El panel, y esta PC como nodo (todo en esta PC)
en.ModePanel=The panel only (nodes on other machines join it with PulsarAgentSetup.exe)
es.ModePanel=Solo el panel (los nodos en otras máquinas se unen con PulsarAgentSetup.exe)
en.ModeAgent=A node only (this PC joins a panel that runs on another machine)
es.ModeAgent=Solo un nodo (esta PC se une a un panel que corre en otra máquina)
en.ShowAdvanced=Show advanced settings (ports, host name, certificate, firewall, database)
es.ShowAdvanced=Mostrar opciones avanzadas (puertos, nombre de host, certificado, firewall, base de datos)
en.AdminCaption=First administrator
es.AdminCaption=Primer administrador
en.AdminDescription=The panel's first user, who can do everything.
es.AdminDescription=El primer usuario del panel, que puede hacer todo.
en.AdminSubCaption=Choose the name and password you sign in to the panel with. The password needs at least 12 characters: a few unrelated words make a strong one that is easy to remember. This setup does not keep it.
es.AdminSubCaption=Elegí el nombre y la contraseña con los que vas a iniciar sesión en el panel. La contraseña necesita al menos 12 caracteres: unas cuantas palabras sin relación forman una contraseña fuerte y fácil de recordar. Este instalador no la guarda.
en.AdminName=&User name:
es.AdminName=&Nombre de usuario:
en.AdminPassword=&Password:
es.AdminPassword=&Contraseña:
en.AdminConfirm=&Repeat the password:
es.AdminConfirm=&Repetir contraseña:
en.PostgresCaption=PostgreSQL on this PC
es.PostgresCaption=PostgreSQL en esta PC
en.PostgresDescription=A PostgreSQL is installed here already.
es.PostgresDescription=Ya hay un PostgreSQL instalado acá.
en.PostgresSubCaption=If it is PostgreSQL 15 or later, Pulsar creates its own database and user in it, which takes the password of its 'postgres' user (chosen when it was installed). It is used once and not kept. With an older PostgreSQL, leave it empty: the setup installs PostgreSQL 18, which needs port 5432 free.
es.PostgresSubCaption=Si es PostgreSQL 15 o posterior, Pulsar crea en él su propia base de datos y su usuario, y para eso necesita la contraseña de su usuario 'postgres' (la que se eligió al instalarlo). Se usa una vez y no se guarda. Con un PostgreSQL más viejo, dejala vacía: el instalador instala PostgreSQL 18, que necesita el puerto 5432 libre.
en.PostgresPassword=Password of the '&postgres' user:
es.PostgresPassword=Contraseña del usuario '&postgres':
en.NetworkCaption=Advanced: network
es.NetworkCaption=Avanzado: red
en.NetworkDescription=The panel's ports and the name nodes and browsers use.
es.NetworkDescription=Los puertos del panel y el nombre que usan los nodos y los navegadores.
en.NetworkSubCaption=Leave a field as it is unless you need another value. The agents' port (8444, say) is where nodes present a client certificate; the extra name or IP address is one nodes and browsers reach the panel by (a DNS name, a public address, a port forward's). This PC's name and its local network addresses are in the certificate already.
es.NetworkSubCaption=Dejá cada campo como está salvo que necesites otro valor. El puerto de los agentes (8444, por ejemplo) es donde los nodos presentan un certificado de cliente; el nombre o la dirección IP extra es uno por el que nodos y navegadores llegan al panel (un nombre DNS, una dirección pública, el de una redirección de puertos). El nombre de esta PC y sus direcciones de la red local ya están en el certificado.
en.PanelPort=The panel's HTTPS port:
es.PanelPort=Puerto HTTPS del panel:
en.SftpPort=SFTP port:
es.SftpPort=Puerto SFTP:
en.AgentsPort=Agents' port (optional):
es.AgentsPort=Puerto de los agentes (opcional):
en.HostName=Another name or IP address for the certificate (optional):
es.HostName=Otro nombre o dirección IP para el certificado (opcional):
en.FirewallPublic=Also open the panel's ports on networks Windows treats as public
es.FirewallPublic=Abrir también los puertos del panel en las redes que Windows considera públicas
en.CertificateCaption=Advanced: certificate
es.CertificateCaption=Avanzado: certificado
en.CertificateDescription=A certificate of your own instead of a self-signed one.
es.CertificateDescription=Un certificado propio en lugar de uno autofirmado.
en.CertificateSubCaption=Leave it empty and the setup makes a self-signed certificate for this PC's names. A PFX of your own must contain its intermediate certificates, and the nodes must trust its root.
es.CertificateSubCaption=Dejalo vacío y el instalador crea un certificado autofirmado para los nombres de esta PC. Un PFX propio tiene que incluir sus certificados intermedios, y los nodos tienen que confiar en su raíz.
en.CertificateFile=&Certificate (PFX):
es.CertificateFile=&Certificado (PFX):
en.CertificateFilter=PFX certificates (*.pfx;*.p12)|*.pfx;*.p12|All files|*.*
es.CertificateFilter=Certificados PFX (*.pfx;*.p12)|*.pfx;*.p12|Todos los archivos|*.*
en.CertificatePassword=The certificate's pass&word (if it has one):
es.CertificatePassword=Contra&seña del certificado (si tiene):
en.DatabaseCaption=Advanced: database
es.DatabaseCaption=Avanzado: base de datos
en.DatabaseDescription=A PostgreSQL elsewhere instead of one on this PC.
es.DatabaseDescription=Un PostgreSQL en otro lado en lugar de uno en esta PC.
en.DatabaseSubCaption=Create an empty database and a user that owns it, and paste its connection string (Host=...;Port=5432;Database=...;Username=...;Password=...). Leave it empty to use PostgreSQL on this PC (the setup installs PostgreSQL 18 when there is none).
es.DatabaseSubCaption=Creá una base de datos vacía y un usuario que sea su dueño, y pegá su cadena de conexión (Host=...;Port=5432;Database=...;Username=...;Password=...). Dejala vacía para usar PostgreSQL en esta PC (el instalador instala PostgreSQL 18 si no hay ninguno).
en.ConnectionString=Connection &string:
es.ConnectionString=Cadena de co&nexión:
en.PublicIpCaption=Advanced: access from the internet
es.PublicIpCaption=Avanzado: acceso desde internet
en.PublicIpDescription=A free Let's Encrypt certificate for your public IP address.
es.PublicIpDescription=Un certificado gratuito de Let's Encrypt para tu IP pública.
en.PublicIpSubCaption=With a static public IP, anyone opens https://<that IP>:<the panel's port> from the internet with no warning and nothing to install. Leave it empty if you do not need it. On your router, forward TCP ports 80 (Let's Encrypt checks it) and the panel's port to this PC. Filling it in accepts Let's Encrypt's Subscriber Agreement (letsencrypt.org/repository).
es.PublicIpSubCaption=Con una IP pública fija, cualquiera abre https://<esa IP>:<el puerto del panel> desde internet sin avisos y sin instalar nada. Dejalo vacío si no lo necesitás. En tu router, reenviá a esta PC los puertos TCP 80 (Let's Encrypt lo comprueba) y el del panel. Completarlo acepta el acuerdo de suscriptor de Let's Encrypt (letsencrypt.org/repository).
en.PublicIp=Public IP for a free Let's Encrypt certificate (optional):
es.PublicIp=IP pública para un certificado gratuito de Let's Encrypt (opcional):
en.ErrorPublicIp=Give the public IPv4 or IPv6 address your router has on the internet (such as 203.0.113.10), not a local one (192.168..., 10..., 172.16-31...), and without a port.
es.ErrorPublicIp=Escribí la dirección IPv4 o IPv6 pública que tiene tu router en internet (como 203.0.113.10), no una local (192.168..., 10..., 172.16-31...), y sin puerto.
en.MemoPublicIp=Let's Encrypt certificate for %1 (port 80 opened for its check)
es.MemoPublicIp=Certificado de Let's Encrypt para %1 (se abre el puerto 80 para su comprobación)
en.ErrorMode=/MODE= takes AllInOne, Panel or Agent.
es.ErrorMode=/MODE= acepta AllInOne, Panel o Agent.
en.ErrorAdminName=The user name takes 1 to 64 letters, digits, or the characters - . _ @ +.
es.ErrorAdminName=El nombre de usuario lleva de 1 a 64 letras, dígitos o los caracteres - . _ @ +.
en.ErrorPasswordShort=The password needs at least 12 characters.
es.ErrorPasswordShort=La contraseña necesita al menos 12 caracteres.
en.ErrorPasswordLong=The password may have at most 256 characters.
es.ErrorPasswordLong=La contraseña puede tener como máximo 256 caracteres.
en.ErrorPasswordsDiffer=The passwords do not match.
es.ErrorPasswordsDiffer=Las contraseñas no coinciden.
en.ErrorPort=A port is a number from 1 to 65535 (the agents' port may be left empty).
es.ErrorPort=Un puerto es un número de 1 a 65535 (el de los agentes puede quedar vacío).
en.ErrorSamePorts=The panel's port, the SFTP port and the agents' port must all differ.
es.ErrorSamePorts=El puerto del panel, el SFTP y el de los agentes tienen que ser distintos.
en.ErrorHostName=A host name has letters, digits, '.', '-' and '_' (a leading '*.' for a wildcard) and does not end in a number, like panel.example.com; an IP address is four numbers from 0 to 255 (203.0.113.7) or an IPv6 address (2001:db8::7). One only: for more, use Install-Pulsar.ps1.
es.ErrorHostName=Un nombre de host tiene letras, dígitos, '.', '-' y '_' (un '*.' al principio para un comodín) y no termina en un número, como panel.example.com; una dirección IP son cuatro números de 0 a 255 (203.0.113.7) o una dirección IPv6 (2001:db8::7). Uno solo: para más, usá Install-Pulsar.ps1.
en.ErrorCertificateMissing=The certificate file was not found.
es.ErrorCertificateMissing=No se encontró el archivo del certificado.
en.ErrorConnectionString=The connection string is too long or has a line break.
es.ErrorConnectionString=La cadena de conexión es demasiado larga o tiene un salto de línea.
en.MemoAllInOne=the panel, and this PC as a node
es.MemoAllInOne=el panel, y esta PC como nodo
en.MemoPanel=the panel only
es.MemoPanel=solo el panel
en.MemoAgent=this PC as a node of a panel on another machine
es.MemoAgent=esta PC como nodo de un panel en otra máquina
en.MemoAdministrator=First administrator: %1
es.MemoAdministrator=Primer administrador: %1
en.MemoAddress=Panel: https://%1:%2 (on this PC also https://localhost:%2), SFTP on port %3
es.MemoAddress=Panel: https://%1:%2 (en esta PC también https://localhost:%2), SFTP en el puerto %3
en.MemoAgentsPort=Agents' port (client certificates): %1
es.MemoAgentsPort=Puerto de los agentes (certificados de cliente): %1
en.MemoHostName=Another name or address in the certificate: %1
es.MemoHostName=Otro nombre o dirección en el certificado: %1
en.MemoCertificate=Certificate: %1
es.MemoCertificate=Certificado: %1
en.MemoSelfSigned=Certificate: self-signed, for this PC's names and local network addresses
es.MemoSelfSigned=Certificado: autofirmado, para los nombres de esta PC y sus direcciones de la red local
en.MemoFirewall=Windows Firewall: the panel's ports open on Domain and Private networks
es.MemoFirewall=Firewall de Windows: los puertos del panel abiertos en redes de dominio y privadas
en.MemoFirewallPublic=Windows Firewall: the panel's ports open on every network, public ones too
es.MemoFirewallPublic=Firewall de Windows: los puertos del panel abiertos en todas las redes, también las públicas
en.MemoDatabaseNew=Database: PostgreSQL 18, installed on this PC (listening on this PC only)
es.MemoDatabaseNew=Base de datos: PostgreSQL 18, instalado en esta PC (escucha solo en esta PC)
en.MemoDatabaseLocal=Database: in the PostgreSQL on this PC (PostgreSQL 18 is installed if it is older than 15)
es.MemoDatabaseLocal=Base de datos: en el PostgreSQL de esta PC (se instala PostgreSQL 18 si es anterior a la 15)
en.MemoDatabaseElsewhere=Database: the one the connection string names
es.MemoDatabaseElsewhere=Base de datos: la que nombra la cadena de conexión
en.FinishedAllInOne=Pulsar is running on this PC, which is its first node. Sign in at %1 as '%2'.%n%nFrom other computers: https://%3:%4, or this PC's IP address in place of its name (the log lists them). They warn about the self-signed certificate until they trust it: the panel's Account page has it to download, with the steps.
es.FinishedAllInOne=Pulsar está funcionando en esta PC, que es su primer nodo. Iniciá sesión en %1 como '%2'.%n%nDesde otras computadoras: https://%3:%4, o la dirección IP de esta PC en lugar de su nombre (el log las muestra). Avisan del certificado autofirmado hasta que confíen en él: la página Mi cuenta del panel lo tiene para descargar, con los pasos.
en.FinishedPanel=The panel is running: sign in at %1 as '%2' (from other computers: https://%3:%4, or this PC's IP address in place of its name). Unless you gave a certificate of your own, browsers warn about its self-signed one until they trust it: the panel's Account page has it to download, with the steps.%n%nAdd nodes in Nodes > Add node, which offers PulsarAgentSetup.exe.
es.FinishedPanel=El panel está funcionando: iniciá sesión en %1 como '%2' (desde otras computadoras: https://%3:%4, o la dirección IP de esta PC en lugar de su nombre). Salvo que hayas dado un certificado propio, los navegadores avisan de su certificado autofirmado hasta que confíen en él: la página Mi cuenta del panel lo tiene para descargar, con los pasos.%n%nAgregá nodos en Nodos > Agregar nodo, que ofrece PulsarAgentSetup.exe.
en.FinishedPostgres=PostgreSQL, when this setup installed it, is an app of its own in Settings > Apps: removing Pulsar keeps it and its data.
es.FinishedPostgres=PostgreSQL, si lo instaló este instalador, es una aplicación aparte en Configuración > Aplicaciones: quitar Pulsar lo conserva, con sus datos.

[Files]
; The release as Build-Release.ps1 stages it (with PulsarAgentSetup.exe in panel\downloads, which the panel serves to new
; nodes). Gone once the install ends: Install-Pulsar.ps1 copies what it installs into Program Files\Pulsar.
Source: "{#Release}\panel\*"; DestDir: "{app}\payload\panel"; Flags: ignoreversion recursesubdirs createallsubdirs deleteafterinstall
Source: "{#Release}\agent\*"; DestDir: "{app}\payload\agent"; Flags: ignoreversion recursesubdirs createallsubdirs deleteafterinstall
Source: "{#Release}\Backup-Pulsar.ps1"; DestDir: "{app}\payload"; Flags: ignoreversion deleteafterinstall
Source: "{#Release}\release.json"; DestDir: "{app}\payload"; Flags: ignoreversion deleteafterinstall
; Last: once it is in place, it runs (RunPulsarSetup). It stays, for the uninstaller.
Source: "{#Release}\Install-Pulsar.ps1"; DestDir: "{app}\payload"; Flags: ignoreversion; AfterInstall: RunPulsarSetup

[Code]
var
  ModePage: TInputOptionWizardPage;
  AdminPage: TInputQueryWizardPage;
  PostgresPage: TInputQueryWizardPage;
  NodePage: TInputQueryWizardPage;
  FolderPage: TInputDirWizardPage;
  NetworkPage: TInputQueryWizardPage;
  FirewallPublicCheck: TNewCheckBox;
  CertificatePage: TInputFileWizardPage;
  CertificatePasswordLabel: TNewStaticText;
  CertificatePasswordEdit: TPasswordEdit;
  DatabasePage: TInputQueryWizardPage;
  PublicIpPage: TInputQueryWizardPage;

// --- The choices -------------------------------------------------------------------------------------------------

// -Mode of Install-Pulsar.ps1.
function SelectedMode: String;
begin
  if ModePage.Values[1] then
    Result := 'Panel'
  else if ModePage.Values[2] then
    Result := 'Agent'
  else
    Result := 'AllInOne';
end;

function InstallsPanel: Boolean;
begin
  Result := SelectedMode <> 'Agent';
end;

function InstallsAgent: Boolean;
begin
  Result := SelectedMode <> 'Panel';
end;

// The Advanced pages' values count when they were shown (or, silent, given as parameters); otherwise the defaults do.
function UsesAdvanced: Boolean;
begin
  Result := WizardSilent or ModePage.Values[3];
end;

function PortValue(const Text: String): Integer;
begin
  Result := StrToIntDef(Trim(Text), -1);
end;

function PanelPort: Integer;
begin
  Result := 8443;
  if UsesAdvanced then
    Result := PortValue(NetworkPage.Values[0]);
end;

function SftpPort: Integer;
begin
  Result := 2022;
  if UsesAdvanced then
    Result := PortValue(NetworkPage.Values[1]);
end;

// 0: none.
function AgentsPort: Integer;
begin
  Result := 0;
  if UsesAdvanced and (Trim(NetworkPage.Values[2]) <> '') then
    Result := PortValue(NetworkPage.Values[2]);
end;

function HostNameValue: String;
begin
  Result := '';
  if UsesAdvanced then
    Result := Trim(NetworkPage.Values[3]);
end;

function CertificateFile: String;
begin
  Result := '';
  if UsesAdvanced then
    Result := Trim(CertificatePage.Values[0]);
end;

// -PublicIp: the panel's public address for a Let's Encrypt certificate, or nothing.
function PublicIpValue: String;
begin
  Result := '';
  if UsesAdvanced then
    Result := Trim(PublicIpPage.Values[0]);
end;

function AdminUserName: String;
begin
  Result := Trim(AdminPage.Values[0]);
end;

function ServersRoot: String;
begin
  Result := RemoveBackslashUnlessRoot(Trim(FolderPage.Values[0]));
end;

function PanelAddress: String;
begin
  Result := Trim(NodePage.Values[0]);
end;

// The secret fields: read here only, and passed on through the environment only (PassSecrets), never on a command line.
function SecretAdminPassword: String;
begin
  Result := AdminPage.Values[1];
end;

function SecretAdminConfirmation: String;
begin
  Result := AdminPage.Values[2];
end;

function SecretPostgresPassword: String;
begin
  Result := PostgresPage.Values[0];
end;

function SecretCertificatePassword: String;
begin
  Result := CertificatePasswordEdit.Text;
end;

function SecretConnectionString: String;
begin
  Result := '';
  if UsesAdvanced then
    Result := Trim(DatabasePage.Values[0]);
end;

function SecretNodeCode: String;
begin
  Result := Trim(NodePage.Values[1]);
end;

// Whether a connection string was given (silent: whether the environment has one).
function HasConnectionString: Boolean;
begin
  if WizardSilent then
    Result := GetEnv('PULSAR_CONNECTION_STRING') <> ''
  else
    Result := SecretConnectionString <> '';
end;

// Empties the secret fields once the installer has them.
procedure ClearSecretFields;
begin
  AdminPage.Values[1] := '';
  AdminPage.Values[2] := '';
  PostgresPage.Values[0] := '';
  CertificatePasswordEdit.Text := '';
  DatabasePage.Values[0] := '';
  NodePage.Values[1] := '';
end;

// --- Checks ------------------------------------------------------------------------------------------------------

// A panel user name as the panel takes it: 1 to 64 of ASP.NET Core Identity's allowed characters.
function IsUserName(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  if (Length(Name) < 1) or (Length(Name) > 64) then
    Exit;
  for I := 1 to Length(Name) do
    if Pos(Name[I], 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._@+') = 0 then
      Exit;
  Result := True;
end;

// An IPv4 address as -HostName takes one: four numbers from 0 to 255 between dots, without leading zeros.
function IsIPv4Address(const Name: String): Boolean;
var
  I, Parts, Digits, Value: Integer;
begin
  Result := False;
  Parts := 1;
  Digits := 0;
  Value := 0;
  for I := 1 to Length(Name) do begin
    if Name[I] = '.' then begin
      if Digits = 0 then
        Exit;
      Parts := Parts + 1;
      Digits := 0;
      Value := 0;
    end
    else if (Name[I] >= '0') and (Name[I] <= '9') then begin
      if (Digits = 1) and (Value = 0) then
        Exit;
      Digits := Digits + 1;
      Value := Value * 10 + Ord(Name[I]) - Ord('0');
      if (Digits > 3) or (Value > 255) then
        Exit;
    end
    else
      Exit;
  end;
  Result := (Parts = 4) and (Digits > 0);
end;

// The shape of an IPv6 address (hexadecimal digits, ':' and '.', at least two ':'): Install-Pulsar.ps1 reads it strictly.
function IsIPv6Address(const Name: String): Boolean;
var
  I, Colons: Integer;
  C: Char;
begin
  Result := False;
  if (Length(Name) < 2) or (Length(Name) > 45) then
    Exit;
  Colons := 0;
  for I := 1 to Length(Name) do begin
    C := Name[I];
    if C = ':' then
      Colons := Colons + 1
    else if not (((C >= '0') and (C <= '9')) or ((C >= 'a') and (C <= 'f')) or ((C >= 'A') and (C <= 'F')) or (C = '.')) then
      Exit;
  end;
  Result := Colons >= 2;
end;

// One name or IP address for the certificate, as -HostName takes it: an IPv4 or IPv6 address, or labels of letters,
// digits, '-' and '_' between dots, a leading "*." for a wildcard and a final dot allowed, the last label not a number
// (browsers read such a name as an IPv4 address: 10.1, 192.168.1.300 and 0x7f are neither).
function IsHostName(Name: String): Boolean;
var
  I, LabelLength, LabelStart: Integer;
  C: Char;
  Last: String;
  Hex: Boolean;
begin
  Result := False;
  if Pos(':', Name) > 0 then begin
    Result := IsIPv6Address(Name);
    Exit;
  end;
  if IsIPv4Address(Name) then begin
    Result := True;
    Exit;
  end;
  if Copy(Name, 1, 2) = '*.' then
    Name := Copy(Name, 3, Length(Name));
  if Name <> '' then
    if Name[Length(Name)] = '.' then
      Name := Copy(Name, 1, Length(Name) - 1);
  if (Name = '') or (Length(Name) > 253) then
    Exit;
  LabelLength := 0;
  LabelStart := 1;
  for I := 1 to Length(Name) do begin
    C := Name[I];
    if C = '.' then begin
      if LabelLength = 0 then
        Exit;
      LabelLength := 0;
      LabelStart := I + 1;
    end
    else if ((C >= 'a') and (C <= 'z')) or ((C >= 'A') and (C <= 'Z')) or ((C >= '0') and (C <= '9')) or (C = '-') or (C = '_') then
      LabelLength := LabelLength + 1
    else
      Exit;
  end;
  if LabelLength = 0 then
    Exit;
  // The last label: a number (decimal, or hexadecimal after 0x) makes the name one browsers read as an address.
  Last := Lowercase(Copy(Name, LabelStart, Length(Name)));
  Hex := Copy(Last, 1, 2) = '0x';
  if Hex then
    Last := Copy(Last, 3, Length(Last));
  for I := 1 to Length(Last) do
    if not (((Last[I] >= '0') and (Last[I] <= '9')) or (Hex and (Last[I] >= 'a') and (Last[I] <= 'f'))) then begin
      Result := True;
      Exit;
    end;
end;

// The value of a hexadecimal group of an IPv6 address (1 to 4 digits), or -1.
function HexGroup(const Text: String): Integer;
var
  I, Digit: Integer;
begin
  Result := -1;
  if (Length(Text) < 1) or (Length(Text) > 4) then
    Exit;
  Result := 0;
  for I := 1 to Length(Text) do begin
    Digit := Pos(Lowercase(Text[I]), '0123456789abcdef') - 1;
    if Digit < 0 then begin
      Result := -1;
      Exit;
    end;
    Result := Result * 16 + Digit;
  end;
end;

// A public IP address as -PublicIp takes it (Install-Pulsar.ps1 and the panel check again): IPv4 as four numbers without
// leading zeros outside the private, local, shared, documentation, multicast and reserved ranges; IPv6 in 2000::/3 but
// not in 2001::/23, 2001:db8::/32, 2002::/16 or 3fff::/20.
function IsPublicIp(Text: String): Boolean;
var
  Parts: array[0..3] of Integer;
  Count, I, Value, First, Second, Colons: Integer;
  Part: String;
begin
  Result := False;
  if (Text = '') or (Length(Text) > 45) then
    Exit;
  if Pos(':', Text) > 0 then begin
    Colons := 0;
    for I := 1 to Length(Text) do
      if Text[I] = ':' then
        Colons := Colons + 1
      else if HexGroup(Copy(Text, I, 1)) < 0 then
        Exit;
    if (Colons < 2) or (Colons > 7) or (Text[1] = ':') or (Pos(':::', Text) > 0) then
      Exit;
    if (Pos('::', Text) > 0) and (Pos('::', Copy(Text, Pos('::', Text) + 2, Length(Text))) > 0) then
      Exit;
    First := HexGroup(Copy(Text, 1, Pos(':', Text) - 1));
    Part := Copy(Text, Pos(':', Text) + 1, Length(Text));
    if Pos(':', Part) > 0 then
      Part := Copy(Part, 1, Pos(':', Part) - 1);
    if Part = '' then
      Second := 0
    else
      Second := HexGroup(Part);
    Result := (First >= $2000) and (First <= $3FFF) and (Second >= 0) and not ((First = $2001) and (Second < $200)) and
      not ((First = $2001) and (Second = $DB8)) and (First <> $2002) and not ((First = $3FFF) and (Second < $1000));
    Exit;
  end;
  Count := 0;
  Text := Text + '.';
  while Text <> '' do begin
    Part := Copy(Text, 1, Pos('.', Text) - 1);
    Text := Copy(Text, Pos('.', Text) + 1, Length(Text));
    if (Count = 4) or (Part = '') or (Length(Part) > 3) or ((Length(Part) > 1) and (Part[1] = '0')) then
      Exit;
    Value := 0;
    for I := 1 to Length(Part) do begin
      if (Part[I] < '0') or (Part[I] > '9') then
        Exit;
      Value := Value * 10 + Ord(Part[I]) - Ord('0');
    end;
    if Value > 255 then
      Exit;
    Parts[Count] := Value;
    Count := Count + 1;
  end;
  if Count <> 4 then
    Exit;
  Result := not ((Parts[0] = 0) or (Parts[0] = 10) or (Parts[0] = 127) or (Parts[0] >= 224) or
    ((Parts[0] = 100) and (Parts[1] >= 64) and (Parts[1] <= 127)) or ((Parts[0] = 169) and (Parts[1] = 254)) or
    ((Parts[0] = 172) and (Parts[1] >= 16) and (Parts[1] <= 31)) or ((Parts[0] = 192) and (Parts[1] = 168)) or
    ((Parts[0] = 192) and (Parts[1] = 0) and ((Parts[2] = 0) or (Parts[2] = 2))) or ((Parts[0] = 192) and (Parts[1] = 88) and (Parts[2] = 99)) or
    ((Parts[0] = 198) and ((Parts[1] = 18) or (Parts[1] = 19))) or ((Parts[0] = 198) and (Parts[1] = 51) and (Parts[2] = 100)) or
    ((Parts[0] = 203) and (Parts[1] = 0) and (Parts[2] = 113)));
end;

function CheckPublicIpPage: Boolean;
begin
  Result := (PublicIpValue = '') or IsPublicIp(PublicIpValue);
  if not Result then
    Result := Refuse(CustomMessage('ErrorPublicIp'));
end;

function IsPort(const Port: Integer): Boolean;
begin
  Result := (Port >= 1) and (Port <= 65535);
end;

function CheckAdminPage: Boolean;
begin
  if not IsUserName(AdminUserName) then begin
    Result := Refuse(CustomMessage('ErrorAdminName'));
    Exit;
  end;
  // Silent: the password comes from PULSAR_ADMIN_PASSWORD, which Install-Pulsar.ps1 checks.
  if not WizardSilent then begin
    if Length(SecretAdminPassword) < 12 then begin
      Result := Refuse(CustomMessage('ErrorPasswordShort'));
      Exit;
    end;
    if Length(SecretAdminPassword) > 256 then begin
      Result := Refuse(CustomMessage('ErrorPasswordLong'));
      Exit;
    end;
    if SecretAdminPassword <> SecretAdminConfirmation then begin
      Result := Refuse(CustomMessage('ErrorPasswordsDiffer'));
      Exit;
    end;
  end;
  Result := True;
end;

function CheckNodePage: Boolean;
var
  Address: String;
begin
  // In the one form Install-Pulsar.ps1 takes, which goes on as -PanelUrl.
  if PanelAddress <> '' then begin
    if not NormalizePanelAddress(PanelAddress, Address) then begin
      Result := Refuse(CustomMessage('ErrorAddress'));
      Exit;
    end;
    NodePage.Values[0] := Address;
  end;
  // Silent: the code comes from PULSAR_NODE_CODE, which Install-Pulsar.ps1 checks.
  if not WizardSilent and not IsNodeCode(SecretNodeCode) then begin
    Result := Refuse(CustomMessage('ErrorNodeCode'));
    Exit;
  end;
  Result := True;
end;

function CheckFolderPage: Boolean;
begin
  Result := IsLocalFolder(ServersRoot);
  if not Result then
    Result := Refuse(FmtMessage(CustomMessage('ErrorFolder'), [ExpandConstant('{sd}\PulsarServers')]));
end;

function CheckNetworkPage: Boolean;
begin
  if not IsPort(PanelPort) or not IsPort(SftpPort) or ((Trim(NetworkPage.Values[2]) <> '') and not IsPort(AgentsPort)) then begin
    Result := Refuse(CustomMessage('ErrorPort'));
    Exit;
  end;
  if (PanelPort = SftpPort) or ((AgentsPort > 0) and ((AgentsPort = PanelPort) or (AgentsPort = SftpPort))) then begin
    Result := Refuse(CustomMessage('ErrorSamePorts'));
    Exit;
  end;
  if (HostNameValue <> '') and not IsHostName(HostNameValue) then begin
    Result := Refuse(CustomMessage('ErrorHostName'));
    Exit;
  end;
  Result := True;
end;

function CheckCertificatePage: Boolean;
begin
  if CertificateFile <> '' then begin
    if not IsSafeArgument(CertificateFile) then begin
      Result := Refuse(FmtMessage(CustomMessage('ErrorUnsafe'), [CertificateFile]));
      Exit;
    end;
    if not FileExists(CertificateFile) then begin
      Result := Refuse(CustomMessage('ErrorCertificateMissing'));
      Exit;
    end;
  end;
  Result := True;
end;

function CheckDatabasePage: Boolean;
var
  I: Integer;
begin
  Result := True;
  if Length(SecretConnectionString) > 4096 then
    Result := False;
  for I := 1 to Length(SecretConnectionString) do
    if Ord(SecretConnectionString[I]) < 32 then
      Result := False;
  if not Result then
    Result := Refuse(CustomMessage('ErrorConnectionString'));
end;

// --- The wizard --------------------------------------------------------------------------------------------------

// A silent setup shows no wizard page, so the checks of the pages' buttons never run: the ports of its command line are checked
// here, before anything is installed. Otherwise a bad one reaches Install-Pulsar.ps1 as a parameter error, whose exit code (1)
// the setup takes for an install that had changed the PC.
function SilentPortsAreValid: Boolean;
var
  Agents: String;
  PanelNumber, SftpNumber, AgentsNumber: Integer;
begin
  Result := True;
  if not WizardSilent or SameText(ExpandConstant('{param:MODE|AllInOne}'), 'Agent') then
    Exit;
  Agents := Trim(ExpandConstant('{param:AGENTSPORT|}'));
  PanelNumber := StrToIntDef(Trim(ExpandConstant('{param:PORT|8443}')), -1);
  SftpNumber := StrToIntDef(Trim(ExpandConstant('{param:SFTPPORT|2022}')), -1);
  AgentsNumber := 0;
  if Agents <> '' then
    AgentsNumber := StrToIntDef(Agents, -1);
  if not IsPort(PanelNumber) or not IsPort(SftpNumber) or ((Agents <> '') and not IsPort(AgentsNumber)) then
    Result := Refuse(CustomMessage('ErrorPort'))
  else if (PanelNumber = SftpNumber) or ((AgentsNumber > 0) and ((AgentsNumber = PanelNumber) or (AgentsNumber = SftpNumber))) then
    Result := Refuse(CustomMessage('ErrorSamePorts'));
end;

function InitializeSetup: Boolean;
var
  Mode: String;
begin
  Mode := ExpandConstant('{param:MODE|AllInOne}');
  Result := SameText(Mode, 'AllInOne') or SameText(Mode, 'Panel') or SameText(Mode, 'Agent');
  if not Result then
    SuppressibleMsgBox(CustomMessage('ErrorMode'), mbError, MB_OK, IDOK)
  else
    Result := SilentPortsAreValid;
end;

procedure InitializeWizard;
var
  Mode: String;
  Below: Integer;
begin
  if UpdateMode then
    WizardForm.WelcomeLabel2.Caption := FmtMessage(CustomMessage('WelcomeUpdate'), [InstalledParts, '{#Version}']);

  ModePage := CreateInputOptionPage(wpLicense, CustomMessage('ModeCaption'), CustomMessage('ModeDescription'),
    CustomMessage('ModeSubCaption'), True, False);
  ModePage.Add(CustomMessage('ModeAllInOne'));
  ModePage.Add(CustomMessage('ModePanel'));
  ModePage.Add(CustomMessage('ModeAgent'));
  ModePage.AddEx(CustomMessage('ShowAdvanced'), 0, False);
  Mode := ExpandConstant('{param:MODE|AllInOne}');
  ModePage.Values[0] := SameText(Mode, 'AllInOne');
  ModePage.Values[1] := SameText(Mode, 'Panel');
  ModePage.Values[2] := SameText(Mode, 'Agent');
  // Closed by default; open when the command line gave one of its values.
  ModePage.Values[3] := (ExpandConstant('{param:PORT|}') <> '') or (ExpandConstant('{param:SFTPPORT|}') <> '') or
    (ExpandConstant('{param:AGENTSPORT|}') <> '') or (ExpandConstant('{param:HOSTNAME|}') <> '') or
    (ExpandConstant('{param:CERTIFICATE|}') <> '') or (ExpandConstant('{param:FIREWALLPUBLIC|}') <> '');

  // Nothing here is ever stored (no SetPreviousData): the secret fields start empty on every run.
  AdminPage := CreateInputQueryPage(ModePage.ID, CustomMessage('AdminCaption'), CustomMessage('AdminDescription'),
    CustomMessage('AdminSubCaption'));
  AdminPage.Add(CustomMessage('AdminName'), False);
  AdminPage.Add(CustomMessage('AdminPassword'), True);
  AdminPage.Add(CustomMessage('AdminConfirm'), True);
  AdminPage.Values[0] := ExpandConstant('{param:ADMINUSER|admin}');

  PostgresPage := CreateInputQueryPage(AdminPage.ID, CustomMessage('PostgresCaption'), CustomMessage('PostgresDescription'),
    CustomMessage('PostgresSubCaption'));
  PostgresPage.Add(CustomMessage('PostgresPassword'), True);

  NodePage := CreateInputQueryPage(PostgresPage.ID, CustomMessage('NodeCaption'), CustomMessage('NodeDescription'),
    CustomMessage('NodeSubCaption'));
  NodePage.Add(CustomMessage('NodeAddress'), False);
  NodePage.Add(CustomMessage('NodeCode'), True);
  NodePage.Values[0] := ExpandConstant('{param:PANELURL|}');

  FolderPage := CreateInputDirPage(NodePage.ID, CustomMessage('FolderCaption'), CustomMessage('FolderDescription'),
    CustomMessage('FolderSubCaption'), False, '');
  FolderPage.Add('');
  FolderPage.Values[0] := ExpandConstant('{param:SERVERSROOT|{sd}\PulsarServers}');

  NetworkPage := CreateInputQueryPage(FolderPage.ID, CustomMessage('NetworkCaption'), CustomMessage('NetworkDescription'),
    CustomMessage('NetworkSubCaption'));
  NetworkPage.Add(CustomMessage('PanelPort'), False);
  NetworkPage.Add(CustomMessage('SftpPort'), False);
  NetworkPage.Add(CustomMessage('AgentsPort'), False);
  NetworkPage.Add(CustomMessage('HostName'), False);
  NetworkPage.Values[0] := ExpandConstant('{param:PORT|8443}');
  NetworkPage.Values[1] := ExpandConstant('{param:SFTPPORT|2022}');
  NetworkPage.Values[2] := ExpandConstant('{param:AGENTSPORT|}');
  NetworkPage.Values[3] := ExpandConstant('{param:HOSTNAME|}');
  FirewallPublicCheck := TNewCheckBox.Create(NetworkPage);
  FirewallPublicCheck.Parent := NetworkPage.Surface;
  FirewallPublicCheck.Caption := CustomMessage('FirewallPublic');
  FirewallPublicCheck.Left := 0;
  FirewallPublicCheck.Top := NetworkPage.Edits[3].Top + NetworkPage.Edits[3].Height + ScaleY(12);
  FirewallPublicCheck.Width := NetworkPage.SurfaceWidth;
  FirewallPublicCheck.Height := ScaleY(17);
  FirewallPublicCheck.Checked := SameText(ExpandConstant('{param:FIREWALLPUBLIC|no}'), 'yes');

  CertificatePage := CreateInputFilePage(NetworkPage.ID, CustomMessage('CertificateCaption'),
    CustomMessage('CertificateDescription'), CustomMessage('CertificateSubCaption'));
  CertificatePage.Add(CustomMessage('CertificateFile'), CustomMessage('CertificateFilter'), '.pfx');
  CertificatePage.Values[0] := ExpandConstant('{param:CERTIFICATE|}');
  Below := CertificatePage.Edits[0].Top + CertificatePage.Edits[0].Height + ScaleY(16);
  CertificatePasswordLabel := TNewStaticText.Create(CertificatePage);
  CertificatePasswordLabel.Parent := CertificatePage.Surface;
  CertificatePasswordLabel.Caption := CustomMessage('CertificatePassword');
  CertificatePasswordLabel.Left := 0;
  CertificatePasswordLabel.Top := Below;
  CertificatePasswordEdit := TPasswordEdit.Create(CertificatePage);
  CertificatePasswordEdit.Parent := CertificatePage.Surface;
  CertificatePasswordEdit.Password := True;
  CertificatePasswordEdit.Left := 0;
  CertificatePasswordEdit.Top := Below + CertificatePasswordLabel.Height + ScaleY(4);
  CertificatePasswordEdit.Width := CertificatePage.Edits[0].Width;
  CertificatePasswordLabel.FocusControl := CertificatePasswordEdit;

  DatabasePage := CreateInputQueryPage(CertificatePage.ID, CustomMessage('DatabaseCaption'),
    CustomMessage('DatabaseDescription'), CustomMessage('DatabaseSubCaption'));
  DatabasePage.Add(CustomMessage('ConnectionString'), True);

  // Right after the network page.
  PublicIpPage := CreateInputQueryPage(NetworkPage.ID, CustomMessage('PublicIpCaption'), CustomMessage('PublicIpDescription'),
    CustomMessage('PublicIpSubCaption'));
  PublicIpPage.Add(CustomMessage('PublicIp'), False);
  PublicIpPage.Values[0] := ExpandConstant('{param:PUBLICIP|}');
  if PublicIpPage.Values[0] <> '' then
    ModePage.Values[3] := True;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if (PageID = ModePage.ID) or (PageID = AdminPage.ID) or (PageID = PostgresPage.ID) or (PageID = NodePage.ID) or
     (PageID = FolderPage.ID) or (PageID = NetworkPage.ID) or (PageID = CertificatePage.ID) or (PageID = DatabasePage.ID) then begin
    if UpdateMode then
      Result := True
    else if PageID = AdminPage.ID then
      Result := not InstallsPanel
    else if PageID = PostgresPage.ID then
      Result := not InstallsPanel or not PostgresFound
    else if PageID = NodePage.ID then
      Result := SelectedMode <> 'Agent'
    else if PageID = FolderPage.ID then
      Result := not InstallsAgent
    else if PageID <> ModePage.ID then
      Result := not InstallsPanel or not UsesAdvanced;
  end;
  if PageID = PublicIpPage.ID then
    Result := UpdateMode or not InstallsPanel or not UsesAdvanced;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = AdminPage.ID then
    Result := CheckAdminPage
  else if CurPageID = NodePage.ID then
    Result := CheckNodePage
  else if CurPageID = FolderPage.ID then
    Result := CheckFolderPage
  else if CurPageID = NetworkPage.ID then
    Result := CheckNetworkPage
  else if CurPageID = CertificatePage.ID then
    Result := CheckCertificatePage
  else if CurPageID = DatabasePage.ID then
    Result := CheckDatabasePage;
  if CurPageID = PublicIpPage.ID then
    Result := CheckPublicIpPage;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo,
  MemoGroupInfo, MemoTasksInfo: String): String;
var
  What, Downloads: String;
begin
  Downloads := CustomMessage('MemoDotNet');
  if UpdateMode then
    Result := FmtMessage(CustomMessage('MemoUpdate'), [InstalledParts, '{#Version}']) + NewLine
  else begin
    if SelectedMode = 'Panel' then
      What := CustomMessage('MemoPanel')
    else if SelectedMode = 'Agent' then
      What := CustomMessage('MemoAgent')
    else
      What := CustomMessage('MemoAllInOne');
    Result := FmtMessage(CustomMessage('MemoInstall'), ['{#Version}']) + NewLine + Space + What + NewLine;
    if InstallsPanel then begin
      Result := Result + Space + FmtMessage(CustomMessage('MemoAdministrator'), [AdminUserName]) + NewLine;
      Result := Result + Space + FmtMessage(CustomMessage('MemoAddress'), [GetComputerNameString, IntToStr(PanelPort), IntToStr(SftpPort)]) + NewLine;
      if AgentsPort > 0 then
        Result := Result + Space + FmtMessage(CustomMessage('MemoAgentsPort'), [IntToStr(AgentsPort)]) + NewLine;
      if HostNameValue <> '' then
        Result := Result + Space + FmtMessage(CustomMessage('MemoHostName'), [HostNameValue]) + NewLine;
      if CertificateFile <> '' then
        Result := Result + Space + FmtMessage(CustomMessage('MemoCertificate'), [CertificateFile]) + NewLine
      else
        Result := Result + Space + CustomMessage('MemoSelfSigned') + NewLine;
      if UsesAdvanced and FirewallPublicCheck.Checked then
        Result := Result + Space + CustomMessage('MemoFirewallPublic') + NewLine
      else
        Result := Result + Space + CustomMessage('MemoFirewall') + NewLine;
      if PublicIpValue <> '' then
        Result := Result + Space + FmtMessage(CustomMessage('MemoPublicIp'), [PublicIpValue]) + NewLine;
      if HasConnectionString then
        Result := Result + Space + CustomMessage('MemoDatabaseElsewhere') + NewLine
      else begin
        Downloads := CustomMessage('MemoDotNetPostgres');
        if PostgresFound then
          Result := Result + Space + CustomMessage('MemoDatabaseLocal') + NewLine
        else
          Result := Result + Space + CustomMessage('MemoDatabaseNew') + NewLine;
      end;
    end;
    if InstallsAgent then
      Result := Result + Space + FmtMessage(CustomMessage('MemoServers'), [ServersRoot]) + NewLine;
    if SelectedMode = 'Agent' then begin
      if PanelAddress <> '' then
        Result := Result + Space + FmtMessage(CustomMessage('MemoNodeOf'), [PanelAddress]) + NewLine
      else
        Result := Result + Space + CustomMessage('MemoNodeOfCode') + NewLine;
    end;
  end;
  Result := Result + NewLine + FmtMessage(CustomMessage('MemoDownloads'), [Downloads]) + NewLine + NewLine +
    FmtMessage(CustomMessage('MemoLog'), [ExpandConstant('{commonappdata}\Pulsar\Setup\logs')]);
end;

// --- Installing --------------------------------------------------------------------------------------------------

// Install-Pulsar.ps1's parameters: the choices, none of them secret (those go through the environment, PassSecrets).
function InstallArguments: String;
begin
  if UpdateMode then
    Result := ' -Update'
  else begin
    Result := ' -Mode ' + SelectedMode;
    if InstallsPanel then begin
      Result := Result + NamedArgument('AdminUserName', AdminUserName) + ' -Port ' + IntToStr(PanelPort) + ' -SftpPort ' + IntToStr(SftpPort);
      if AgentsPort > 0 then
        Result := Result + ' -AgentsPort ' + IntToStr(AgentsPort);
      if HostNameValue <> '' then
        Result := Result + NamedArgument('HostName', HostNameValue);
      if CertificateFile <> '' then
        Result := Result + NamedArgument('CertificatePath', CertificateFile);
      if UsesAdvanced and FirewallPublicCheck.Checked then
        Result := Result + ' -FirewallPublic';
      if PublicIpValue <> '' then
        Result := Result + NamedArgument('PublicIp', PublicIpValue);
      if OfflinePostgresInstaller <> '' then
        Result := Result + NamedArgument('PostgresInstaller', OfflinePostgresInstaller);
    end;
    if InstallsAgent then
      Result := Result + NamedArgument('ServersRoot', ServersRoot);
    if (SelectedMode = 'Agent') and (PanelAddress <> '') then
      Result := Result + NamedArgument('PanelUrl', PanelAddress);
  end;
  if OfflineDotNetInstallers <> '' then
    Result := Result + NamedArgument('DotNetInstallers', OfflineDotNetInstallers);
end;

// The secrets, into this process's environment for Install-Pulsar.ps1 to inherit (RunInstallScript removes them as
// soon as it has started). A silent setup passes on the environment it was started with (docs/guides/install.md).
procedure PassSecrets;
begin
  if WizardSilent then
    Exit;
  ClearSecretVariables;
  if UpdateMode then
    Exit;
  if InstallsPanel then begin
    SetSecretVariable('PULSAR_ADMIN_PASSWORD', SecretAdminPassword);
    if PostgresFound then
      SetSecretVariable('PULSAR_POSTGRES_PASSWORD', SecretPostgresPassword);
    if CertificateFile <> '' then
      SetSecretVariable('PULSAR_CERTIFICATE_PASSWORD', SecretCertificatePassword);
    SetSecretVariable('PULSAR_CONNECTION_STRING', SecretConnectionString);
  end
  else
    SetSecretVariable('PULSAR_NODE_CODE', SecretNodeCode);
end;

// AfterInstall of Install-Pulsar.ps1, the payload's last file.
procedure RunPulsarSetup;
begin
  PassSecrets;
  try
    RunInstallStep(InstallArguments);
  finally
    ClearSecretFields;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Done, Port, SignIn: String;
begin
  if CurPageID <> wpFinished then
    Exit;
  Port := IntToStr(PanelPort);
  SignIn := RunPanelUrl;
  if SignIn = '' then
    SignIn := 'https://localhost:' + Port + '/';
  if UpdateMode then
    Done := CustomMessage('FinishedUpdate')
  else if SelectedMode = 'Agent' then
    Done := CustomMessage('FinishedAgent')
  else if SelectedMode = 'Panel' then
    Done := FmtMessage(CustomMessage('FinishedPanel'), [SignIn, AdminUserName, GetComputerNameString, Port])
  else
    Done := FmtMessage(CustomMessage('FinishedAllInOne'), [SignIn, AdminUserName, GetComputerNameString, Port]);
  if not UpdateMode and InstallsPanel and not HasConnectionString then
    Done := Done + Paragraph + CustomMessage('FinishedPostgres');
  ShowFinishedText(Done);
end;
