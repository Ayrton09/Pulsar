; PulsarAgentSetup.exe (Phase 18, docs/guides/install.md "The agent's setup"): the wizard that makes this PC a node of
; a panel on another machine, with the panel's address and the node code from Nodes > Add node, or updates the agent,
; with Install-Pulsar.ps1 -Mode Agent doing the work. The panel offers it at /downloads/PulsarAgentSetup.exe (it is in
; PulsarSetup.exe's payload, in the panel's downloads folder). Common.iss says how; Build-Release.ps1 compiles it.
; No Advanced page: the agent opens no port of its own (the panel's firewall options do nothing for it).

#include "Common.iss"

[Setup]
OutputBaseFilename=PulsarAgentSetup-{#Version}
VersionInfoDescription=Pulsar agent setup

[Messages]
en.WelcomeLabel2=This will make this PC a node of your Pulsar panel, which runs game servers: it installs [name/ver]'s agent.%n%nHave the panel's address and a node code ready: in the panel, Nodes > Add node. Setup downloads the newest .NET 10 runtime if this PC lacks it, and checks it before running it.
es.WelcomeLabel2=Esto va a convertir esta PC en un nodo de tu panel de Pulsar, que corre servidores de juegos: instala el agente de [name/ver].%n%nTené a mano la dirección del panel y un código del nodo: en el panel, Nodos > Agregar nodo. El instalador descarga el runtime de .NET 10 más nuevo si a esta PC le falta, y lo comprueba antes de ejecutarlo.

[CustomMessages]
en.AgentPanelHere=This PC runs Pulsar's panel. Update it with PulsarSetup.exe, which updates its agent too.
es.AgentPanelHere=Esta PC corre el panel de Pulsar. Actualizala con PulsarSetup.exe, que también actualiza su agente.
en.AgentMsiHere=The agent on this PC was installed with the agent MSI, which maintains it: update it with a newer MSI (msiexec /i pulsar-agent-<version>-windows-x64.msi /qn).
es.AgentMsiHere=El agente de esta PC se instaló con el MSI del agente, que lo mantiene: actualizalo con un MSI más nuevo (msiexec /i pulsar-agent-<versión>-windows-x64.msi /qn).
en.MemoNode=this PC as a node
es.MemoNode=esta PC como nodo

[Files]
; The release's agent and installer, as Build-Release.ps1 stages them. Gone once the install ends but for
; Install-Pulsar.ps1, which stays for the uninstaller.
Source: "{#Release}\agent\*"; DestDir: "{app}\payload\agent"; Flags: ignoreversion recursesubdirs createallsubdirs deleteafterinstall
Source: "{#Release}\release.json"; DestDir: "{app}\payload"; Flags: ignoreversion deleteafterinstall
; Last: once it is in place, it runs (RunPulsarSetup).
Source: "{#Release}\Install-Pulsar.ps1"; DestDir: "{app}\payload"; Flags: ignoreversion; AfterInstall: RunPulsarSetup

[Code]
var
  NodePage: TInputQueryWizardPage;
  FolderPage: TInputDirWizardPage;

function ServersRoot: String;
begin
  Result := RemoveBackslashUnlessRoot(Trim(FolderPage.Values[0]));
end;

function PanelAddress: String;
begin
  Result := Trim(NodePage.Values[0]);
end;

// The secret field: read here only, and passed on through the environment only (PassSecrets), never on a command line.
function SecretNodeCode: String;
begin
  Result := Trim(NodePage.Values[1]);
end;

procedure ClearSecretFields;
begin
  NodePage.Values[1] := '';
end;

function CheckNodePage: Boolean;
begin
  if (PanelAddress <> '') and not IsPanelAddress(PanelAddress) then begin
    Result := Refuse(CustomMessage('ErrorAddress'));
    Exit;
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

// Not where the panel runs (PulsarSetup.exe updates both there), nor over the agent MSI's agent.
function InitializeSetup: Boolean;
begin
  Result := False;
  if PanelInstalled then begin
    SuppressibleMsgBox(CustomMessage('AgentPanelHere'), mbError, MB_OK, IDOK);
    Exit;
  end;
  if AgentFromMsi then begin
    SuppressibleMsgBox(CustomMessage('AgentMsiHere'), mbError, MB_OK, IDOK);
    Exit;
  end;
  Result := True;
end;

procedure InitializeWizard;
begin
  if UpdateMode then
    WizardForm.WelcomeLabel2.Caption := FmtMessage(CustomMessage('WelcomeUpdate'), [InstalledParts, '{#Version}']);

  // Nothing here is ever stored (no SetPreviousData): the code's field starts empty on every run.
  NodePage := CreateInputQueryPage(wpLicense, CustomMessage('NodeCaption'), CustomMessage('NodeDescription'),
    CustomMessage('NodeSubCaption'));
  NodePage.Add(CustomMessage('NodeAddress'), False);
  NodePage.Add(CustomMessage('NodeCode'), True);
  NodePage.Values[0] := ExpandConstant('{param:PANELURL|}');

  FolderPage := CreateInputDirPage(NodePage.ID, CustomMessage('FolderCaption'), CustomMessage('FolderDescription'),
    CustomMessage('FolderSubCaption'), False, '');
  FolderPage.Add('');
  FolderPage.Values[0] := ExpandConstant('{param:SERVERSROOT|{sd}\PulsarServers}');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := UpdateMode and ((PageID = NodePage.ID) or (PageID = FolderPage.ID));
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = NodePage.ID then
    Result := CheckNodePage
  else if CurPageID = FolderPage.ID then
    Result := CheckFolderPage;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo,
  MemoGroupInfo, MemoTasksInfo: String): String;
begin
  if UpdateMode then
    Result := FmtMessage(CustomMessage('MemoUpdate'), [InstalledParts, '{#Version}']) + NewLine
  else begin
    Result := FmtMessage(CustomMessage('MemoInstall'), ['{#Version}']) + NewLine + Space + CustomMessage('MemoNode') + NewLine;
    if PanelAddress <> '' then
      Result := Result + Space + FmtMessage(CustomMessage('MemoNodeOf'), [PanelAddress]) + NewLine
    else
      Result := Result + Space + CustomMessage('MemoNodeOfCode') + NewLine;
    Result := Result + Space + FmtMessage(CustomMessage('MemoServers'), [ServersRoot]) + NewLine;
  end;
  Result := Result + NewLine + FmtMessage(CustomMessage('MemoDownloads'), [CustomMessage('MemoDotNet')]) + NewLine + NewLine +
    FmtMessage(CustomMessage('MemoLog'), [ExpandConstant('{commonappdata}\Pulsar\Setup\logs')]);
end;

// Install-Pulsar.ps1's parameters: the choices, none of them secret (the code goes through the environment, PassSecrets).
function InstallArguments: String;
begin
  if UpdateMode then
    Result := ' -Update'
  else begin
    Result := ' -Mode Agent' + NamedArgument('ServersRoot', ServersRoot);
    if PanelAddress <> '' then
      Result := Result + NamedArgument('PanelUrl', PanelAddress);
  end;
  if OfflineDotNetInstallers <> '' then
    Result := Result + NamedArgument('DotNetInstallers', OfflineDotNetInstallers);
end;

// The node code, into this process's environment for Install-Pulsar.ps1 to inherit (RunInstallScript removes it as
// soon as it has started). A silent setup passes on the environment it was started with (PULSAR_NODE_CODE).
procedure PassSecrets;
begin
  if WizardSilent then
    Exit;
  ClearSecretVariables;
  if not UpdateMode then
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
begin
  if CurPageID <> wpFinished then
    Exit;
  if UpdateMode then
    ShowFinishedText(CustomMessage('FinishedUpdate'))
  else
    ShowFinishedText(CustomMessage('FinishedAgent'));
end;
