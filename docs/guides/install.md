# Installing Pulsar

Pulsar has two parts:

- **The panel**: the web interface and API. It keeps its data in PostgreSQL. One per installation.
- **The agent**: runs on every Windows machine that hosts game servers (a *node*) and connects to the panel.

Both can run on the same PC (the usual setup for a home server), or the panel can run on one machine and nodes on
others. Pulsar's installer does either; this guide covers both.

## Requirements

- Windows 11 or Windows Server 2022/2025, 64-bit, with an administrator account. (Windows 10 22H2 works on a
  best-effort basis.)
- For the panel: PostgreSQL 16 or later (step 1), about 500 MB of disk and 1 GB of RAM.
- For each node: the disk space of its games (Counter-Strike 2 needs about 85 GB) and their memory. Nodes connect to
  the panel; they open no port of their own besides their game servers' ports.
- Internet access on the nodes (games are downloaded from Steam with SteamCMD).

## 1. PostgreSQL (panel machine)

1. Download the PostgreSQL installer for Windows from <https://www.postgresql.org/download/windows/> (the
   "Download the installer" link, by EDB). Any version from 16 on.
2. Run it and keep the defaults: install directory, data directory, **port 5432**. Stack Builder is not needed.
3. When it asks for the password of the database superuser (`postgres`), choose one and keep it: Pulsar's installer
   asks for it once, to create Pulsar's own database and user. Pulsar never stores it.

Already have a PostgreSQL elsewhere? Create an empty database and a user that owns it, and give the installer its
connection string with `-ConnectionString "Host=…;Port=5432;Database=…;Username=…;Password=…"` instead.

## 2. Download Pulsar

1. Download `pulsar-<version>-windows-x64.zip` and its `.sha256` file from the project's
   [Releases](https://github.com/Ayrton09/Pulsar/releases) page.
2. Check the download (PowerShell, in the download folder):

   ```powershell
   (Get-FileHash .\pulsar-<version>-windows-x64.zip -Algorithm SHA256).Hash
   Get-Content .\pulsar-<version>-windows-x64.zip.sha256
   ```

   Both must show the same value.
3. Unblock and extract it to a folder of your choice, for example `C:\PulsarSetup`:

   ```powershell
   Unblock-File .\pulsar-<version>-windows-x64.zip
   Expand-Archive .\pulsar-<version>-windows-x64.zip C:\PulsarSetup
   ```

The installer copies the programs to `C:\Program Files\Pulsar`; the extracted folder is only needed while installing
and for later updates.

## 3. Install

Open **Windows PowerShell as administrator** (Start → type *PowerShell* → *Run as administrator*) and go to the
extracted folder:

```powershell
cd C:\PulsarSetup
```

### Panel and agent on this PC

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-Pulsar.ps1
```

It asks for the PostgreSQL `postgres` password (step 1) and for a password for Pulsar's first administrator, `admin`
(at least 12 characters; `-AdminUserName` chooses another name). Then it:

1. creates the `pulsar` database and user in the local PostgreSQL (with a random password);
2. installs the panel in `C:\Program Files\Pulsar\Panel` as the **PulsarPanel** service, running as its own virtual
   account (`NT SERVICE\PulsarPanel`), with its data in `C:\ProgramData\Pulsar\Panel` (keys, logs, certificate);
3. makes a self-signed HTTPS certificate for this PC's names and `localhost`, valid for 5 years, and trusts it on this
   PC (the agent and this PC's browsers check it);
4. opens the panel's port (**8443**, `-Port` chooses another) in Windows Firewall;
5. starts the panel and creates the administrator;
6. adds this PC as a node, installs the agent in `C:\Program Files\Pulsar\Agent` and enrolls it; the agent runs as the
   **PulsarAgent** service (LocalSystem) and keeps its data in `C:\ProgramData\Pulsar\Agent`. Game servers go to
   `C:\PulsarServers` (`-ServersRoot` chooses another folder).

When it ends, open <https://localhost:8443> and sign in as `admin`.

### Panel and nodes on different machines

On the panel's machine:

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-Pulsar.ps1 -Mode Panel
```

To make the certificate valid for a name other machines use (a DNS name, or the address of a port forward), add
`-HostName panel.example.com`. To use a real certificate instead of a self-signed one, pass it with
`-CertificatePath C:\certs\panel.pfx -CertificatePassword (Read-Host -AsSecureString)`.

Then, for each node: in the panel, **Nodes → Add node**, give it a name and copy the command it shows. On the node,
extract the same ZIP, open PowerShell as administrator in its folder and run that command. It looks like:

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-Pulsar.ps1 -Mode Agent -PanelUrl https://panel:8443 -Token <token> -PanelKey sha256:<fingerprint>
```

The command works once and for 30 minutes. With the panel's self-signed certificate, copy
`C:\ProgramData\Pulsar\Panel\tls\panel.cer` from the panel's machine to the node first and add
`-TrustCertificate .\panel.cer`: the node then trusts that certificate. The node appears as **Online** in the panel.

The panel's machine must accept connections on its port (the installer opens it in Windows Firewall; routers and cloud
firewalls are up to you). Nodes only connect out to the panel.

## 4. First steps in the panel

1. **Account → Two-factor authentication**: set it up with an authenticator app and keep the recovery codes. In
   **Roles**, *Require two-factor* on the Administrator role makes it mandatory for administrators.
2. **Nodes → your node → Allocations**: add the IP addresses and ports game servers may use (for example
   `0.0.0.0`, ports `27015-27030`).
3. **Servers → New server**: choose the node and the game (Counter-Strike 2, Counter-Strike: Source), name it, and
   set its variables. The node downloads the game with SteamCMD; the progress shows on the server's page.
4. Open the game's ports to players: in Windows Firewall on the node (or let the agent manage rules for its servers:
   add `"Pulsar": { "ManageFirewall": true }` to `C:\Program Files\Pulsar\Agent\appsettings.Production.json` and
   restart the PulsarAgent service), and in your router if players come from the internet.

Windows Defender scans every file SteamCMD writes, which slows the first download of large games. Pulsar does not add
exclusions on its own: whether to exclude `C:\PulsarServers` is your security decision.

## Where things are

| What | Where |
|---|---|
| Panel program and settings (`appsettings.Production.json`: database, certificate) | `C:\Program Files\Pulsar\Panel` |
| Panel data: Data Protection keys, logs, certificate | `C:\ProgramData\Pulsar\Panel` |
| Agent program | `C:\Program Files\Pulsar\Agent` |
| Agent data: configuration, logs, node state, backups | `C:\ProgramData\Pulsar\Agent` |
| Game servers | `C:\PulsarServers` |

Back up the panel as described in [backup.md](backup.md): without its keys, the database's secrets cannot be read.

## Troubleshooting

- **The installer stops with an error**: it says what failed. Run it again after fixing it; if the panel or the agent
  was already installed, remove it first with `-Uninstall` (it keeps the database and data).
- **The panel does not start**: `C:\ProgramData\Pulsar\Panel\logs\panel-<date>.log` says why (database unreachable,
  port in use…). `Get-Service PulsarPanel` shows its state.
- **A node stays offline**: on the node, `& 'C:\Program Files\Pulsar\Agent\PulsarAgent.exe' status --check` checks the
  connection to the panel; its log is in `C:\ProgramData\Pulsar\Agent\logs`.
- **The browser warns about the certificate**: expected with the self-signed certificate on other computers. Trust
  `panel.cer` on them, or install a real certificate (`-CertificatePath` when installing; to change it later, edit
  `Kestrel:Endpoints:Https:Certificate` in the panel's `appsettings.Production.json` and restart the service).

## Uninstalling

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-Pulsar.ps1 -Uninstall
```

It removes the services, the programs, the firewall rule and the trust in the panel's certificate. It keeps the
database, `C:\ProgramData\Pulsar` and the game servers' folders. Delete game servers from the panel first: that also
removes their Windows accounts.
