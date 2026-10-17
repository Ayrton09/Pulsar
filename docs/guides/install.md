# Installing Pulsar

Pulsar has two parts:

- **The panel**: the web interface and API. It keeps its data in PostgreSQL. One per installation.
- **The agent**: runs on every Windows machine that hosts game servers (a *node*) and connects to the panel.

Both can run on the same PC (the usual setup for a home server), or the panel can run on one machine and nodes on
others. Pulsar's installer does either; this guide covers both. The [easy install](#easy-install) is a setup wizard;
the [installer script](#advanced-and-automation-the-installer-script) underneath it is there for automation and for
what the wizard does not ask.

## Requirements

- Windows 11 or Windows Server 2022/2025, 64-bit, with an administrator account. (Windows 10 22H2 works on a
  best-effort basis.)
- Windows updates installed. Pulsar's programs (.NET 10) do not start on a Windows that lacks them: Windows Server 2022
  as installed from its original image stops them with "Your Windows doesn't fully support CET". Run Windows Update
  (Settings > Windows Update, or `sconfig` on Server Core) and restart before installing; the installer checks this
  before it installs Pulsar and installs nothing of Pulsar if it fails (the .NET runtimes, if it just installed them,
  stay).
- Internet access while installing, or the installers it would download, given offline
  ([Offline installs](#offline-installs)). The installer downloads what Pulsar needs and the machine lacks, and checks
  every download before it runs it ([What the installer downloads](#what-the-installer-downloads)):
  - the .NET 10 runtime, and for the panel the ASP.NET Core 10 runtime, which Pulsar's programs run on;
  - for the panel, PostgreSQL 18, unless the machine has PostgreSQL 15 or later already or the panel uses a database
    elsewhere (step 1).
- For the panel: about 500 MB of disk (and PostgreSQL's) and 1 GB of RAM.
- For each node: the disk space of its games (Counter-Strike 2 needs about 85 GB) and their memory. Nodes connect to
  the panel; they open no port of their own besides their game servers' ports.
- Internet access on the nodes (games are downloaded from Steam with SteamCMD).

## Easy install

### The panel's PC: PulsarSetup.exe

1. Download `PulsarSetup-<version>.exe` and its `.sha256` file from the project's
   [Releases](https://github.com/Ayrton09/Pulsar/releases) page, and check it as [step 2](#2-download-pulsar) shows
   for the ZIP: `(Get-FileHash .\PulsarSetup-<version>.exe -Algorithm SHA256).Hash` must show the value in the
   `.sha256` file. The release workflow builds the setups and attests their provenance, as it does the ZIP's: with
   the GitHub CLI, `gh attestation verify .\PulsarSetup-<version>.exe --repo Ayrton09/Pulsar` (and the same for
   `PulsarAgentSetup-<version>.exe`) says it was built by this repository's workflow from the tag's commit.
2. Run it. It asks for administrator rights. The setups are not code-signed yet, so Windows SmartScreen may say
   *Windows protected your PC*: for a file whose SHA-256 you checked, click **More info**, check that the app is
   `PulsarSetup-<version>.exe`, and click **Run anyway**.
3. Follow the wizard, in English or Spanish (Windows' language chooses):
   1. **License**: Pulsar's, the GNU AGPL v3.
   2. **What to install**: the panel and this PC as a node (the default: everything on one PC), the panel only, or a
      node only (this PC joins a panel on another machine, as with `PulsarAgentSetup.exe` below). **Show advanced
      settings** adds the Advanced pages.
   3. **First administrator** (with the panel): the name you sign in with (`admin`) and a password, twice: at least
      12 characters. The setup keeps neither.
   4. **PostgreSQL on this PC**, only when a PostgreSQL service is installed: the password of its `postgres` user, to
      create Pulsar's database in it, used once (PostgreSQL 15 or later; with an older one see
      [step 1](#1-postgresql-panel-machine)).
   5. **Join a panel** (a node only): the panel's address and the node code from the panel's **Nodes → Add node**.
   6. **Game servers' folder** (with a node): `C:\PulsarServers`, or a new or empty folder of your choice.
   7. **Advanced** (with the panel, when shown): the panel's HTTPS port (8443), the SFTP port (2022), an agents' port
      for [client certificates](#agents-port-client-certificates-optional) (none by default), one more name for the
      panel's certificate (a DNS name nodes and browsers use; several names need the script's `-HostName`), opening
      the ports on public networks too, a PFX certificate of your own and its password, and the connection string of
      a PostgreSQL elsewhere.
   8. **Ready**: what it will do (never a password).
4. It installs, showing each step: it downloads what this PC lacks (the newest .NET 10 runtimes and, for a panel
   without a PostgreSQL to use, the newest PostgreSQL 18) and checks every download before anything runs (the table
   in [What the installer downloads](#what-the-installer-downloads)), then installs .NET, PostgreSQL (listening on this
   PC only), the panel, its administrator and, with a node, the agent.
5. **Finish** shows the panel's sign-in page, `https://localhost:8443/`, in a box to copy (the setup runs as
   administrator, so it opens no browser itself): open it in your browser and sign in as `admin`. Then see
   [First steps in the panel](#4-first-steps-in-the-panel).

Underneath, the setup does what the [installer script](#advanced-and-automation-the-installer-script) does, with the
same checks: it puts the release's programs and `Install-Pulsar.ps1` in `C:\Program Files\Pulsar\Setup\payload` (made
in Program Files, so only administrators can change it, as [step 2](#2-download-pulsar) explains) and runs
`Install-Pulsar.ps1 -Unattended` in 64-bit Windows PowerShell. The passwords, the connection string and the node code
reach it through the setup's environment ([Unattended installs](#unattended-installs)), never a command line, a file,
the registry, the setup's saved settings or its log. Once it ends, only `Install-Pulsar.ps1` stays there, for
uninstalling.

When it fails, the setup says why, and names the installer's log
(`C:\ProgramData\Pulsar\Setup\logs\install-<date>-<process>.log`):

- when the installer changed nothing (a wrong password, no internet, a download that failed its checks, a port or
  folder taken...), the setup undoes its own files too and leaves no entry in **Apps**: fix it and run it again;
- when it stopped partway, the setup's entry in **Apps** stays: remove Pulsar there (that removes what was left), fix
  what the message says and run the setup again.

PostgreSQL, when the setup installed it, is an app of its own in **Settings → Apps** (EDB's installer), which removing
Pulsar keeps, with its data.

### Other machines: PulsarAgentSetup.exe

In the panel, **Nodes → Add node**: give the node a name. The dialog shows the panel's address, a node code and a link
to `PulsarAgentSetup.exe`, which the panel serves ([The agent's setup](#the-agents-setup); it is also on the Releases
page, as `PulsarAgentSetup-<version>.exe`). On the node, run it (SmartScreen as above), and give it the code (the
address too when the node reaches the panel at another address than the code names) and the game servers' folder.
It reads the certificate the panel presents, checks it against the code and has the agent pin it (the agent accepts
that certificate and no other; the node's certificate stores are not changed): nobody copies `panel.cer`. The dialog
also shows the SHA-256 of the `PulsarAgentSetup.exe` it links to: on the node, `Get-FileHash .\PulsarAgentSetup.exe`
must show the same value.
The node appears as **Online**. It refuses a PC that runs the panel (run `PulsarSetup.exe` there, which updates both)
and one whose agent the agent MSI installed.

### Silent installs with the setups

For a script, Intune or a group policy, from an **elevated** process (one that asks for elevation starts the setup
with a fresh environment, without your variables), with the secrets in its environment, as for the script's
[unattended installs](#unattended-installs) (`PULSAR_ADMIN_PASSWORD`, `PULSAR_NODE_CODE`...):

```powershell
# Asked for here (or taken from your secret store), never typed on a command line.
$env:PULSAR_ADMIN_PASSWORD = [Net.NetworkCredential]::new('', (Read-Host -AsSecureString 'Administrator password')).Password
Start-Process -Wait .\PulsarSetup-<version>.exe '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="C:\Logs\pulsar-setup.log" /MODE=AllInOne'
Remove-Item Env:PULSAR_ADMIN_PASSWORD
```

The choices are parameters, never a secret: `/MODE=AllInOne|Panel|Agent`, `/ADMINUSER=`, `/SERVERSROOT=`, `/PORT=`,
`/SFTPPORT=`, `/AGENTSPORT=`, `/HOSTNAME=`, `/CERTIFICATE=<pfx>`, `/FIREWALLPUBLIC=yes`, `/PANELURL=`, `/LANG=en|es`,
and, offline, `/DOTNETINSTALLERS=<folder>` and `/POSTGRESINSTALLER=<file>` ([Offline installs](#offline-installs)).
`PulsarAgentSetup-<version>.exe` takes `/SERVERSROOT=`, `/PANELURL=` and `/DOTNETINSTALLERS=`, with the code in
`PULSAR_NODE_CODE`. Exit codes: `0` done (add `/RESTARTEXITCODE=3010` to get 3010 when a .NET installer asks for a
restart); `4` the installer stopped and changed nothing, and the setup undid its own files; `100` the installer
failed after changing something (remove Pulsar from **Apps**); the installer's log says why. Inno Setup's other codes
mean what [its documentation](https://jrsoftware.org/ishelp/index.php?topic=setupexitcodes) says.

### Updating and removing

A newer `PulsarSetup.exe` (or `PulsarAgentSetup.exe` on a node) run on a PC where Pulsar is installed updates it,
asking nothing but the license ([update.md](update.md)); a setup older than what is installed refuses to run. To
remove Pulsar, **Settings → Apps → Pulsar → Uninstall**: it runs `Install-Pulsar.ps1 -Uninstall`
([Uninstalling](#uninstalling)) and keeps the data.

## Advanced and automation: the installer script

The setups run the release's `Install-Pulsar.ps1`, which the release ZIP, published too, also holds: steps 1 to 3
install with it directly, for automation, offline machines, a panel restored from a backup (`-RestoreFrom`), several
host names, and everything else the wizard does not ask.

## 1. PostgreSQL (panel machine)

Usually nothing to do. When the panel's machine has no PostgreSQL 15 or later, the installer installs the newest
PostgreSQL 18 with EDB's installer (the one postgresql.org links for Windows), unattended:

- the server and its command-line tools only (no pgAdmin, no Stack Builder), in `C:\Program Files\PostgreSQL\18`, as
  the service `postgresql-x64-18` on **port 5432** (`-PostgresPort` chooses another; a port in use stops the install
  before anything changes);
- listening on this machine only (`listen_addresses = 'localhost'`, set with `ALTER SYSTEM`), and the installer checks
  that `pg_hba.conf` lets in nothing but this machine;
- its superuser, `postgres`, gets a random password, handed to EDB's installer in a file only SYSTEM and Administrators
  can read (deleted when the installer ends), changed again to another random one as soon as PostgreSQL runs, used to
  create Pulsar's database and user, and then kept nowhere. Nobody needs it for Pulsar; to use PostgreSQL yourself,
  give it a password of yours as [below](#the-postgres-superusers-password).

With a PostgreSQL 15 or later installed already, the installer uses it (the newest, with several): it asks for its
`postgres` password once, to create Pulsar's own database and user, and never stores it. Its programs must be in a
folder only administrators can change (its installer's default, in Program Files): the installer runs its `psql`
as administrator.

Pulsar's own user, `pulsar`, gets a random password of its own, kept in the panel's settings (readable only by
SYSTEM, Administrators and the panel's service account).

Already have a PostgreSQL elsewhere? Create an empty database and a user that owns it, and give the installer its
connection string with `-ConnectionString "Host=…;Port=5432;Database=…;Username=…;Password=…"` instead, or in the
`PULSAR_CONNECTION_STRING` environment variable of that PowerShell, which keeps the password off the command line.

### The postgres superuser's password

The password the installer gave `postgres` is not kept anywhere. To set one of your own, in an elevated PowerShell on
the panel's machine (for the minute it takes, any program on this machine can connect to PostgreSQL as `postgres`, so
do it while no game server runs here, or accept that):

1. `Stop-Service postgresql-x64-18`, and make a copy of `C:\Program Files\PostgreSQL\18\data\pg_hba.conf`.
2. In `pg_hba.conf`, add these two lines above the others:

   ```text
   host    postgres    postgres    127.0.0.1/32    trust
   host    postgres    postgres    ::1/128         trust
   ```

3. `Start-Service postgresql-x64-18`, then set the password (psql asks for it; it never shows on a command line):

   ```powershell
   & 'C:\Program Files\PostgreSQL\18\bin\psql.exe' -h localhost -U postgres -d postgres -c '\password postgres'
   ```

4. Put the copy of `pg_hba.conf` back and `Restart-Service postgresql-x64-18`.

## 2. Download Pulsar

1. Download `pulsar-<version>-windows-x64.zip` and its `.sha256` file from the project's
   [Releases](https://github.com/Ayrton09/Pulsar/releases) page.
2. Check the download (PowerShell, in the download folder):

   ```powershell
   (Get-FileHash .\pulsar-<version>-windows-x64.zip -Algorithm SHA256).Hash
   Get-Content .\pulsar-<version>-windows-x64.zip.sha256
   ```

   Both must show the same value. A release built by the project's release workflow also carries a provenance
   attestation, which you can check with the GitHub CLI: `gh attestation verify .\pulsar-<version>-windows-x64.zip --repo Ayrton09/Pulsar`.
3. Extract it into a **new folder in `C:\Program Files`**, which only administrators can change. Open **Windows
   PowerShell as administrator** (Start → type *PowerShell* → *Run as administrator*), go to the download folder and
   run:

   ```powershell
   New-Item -ItemType Directory "$env:ProgramFiles\PulsarSetup"
   Unblock-File .\pulsar-<version>-windows-x64.zip
   Expand-Archive .\pulsar-<version>-windows-x64.zip "$env:ProgramFiles\PulsarSetup"
   ```

   This makes `C:\Program Files\PulsarSetup`. Why there: the installer runs as administrator, runs the agent from this
   folder and copies the programs into `C:\Program Files\Pulsar`, where they run as services (the agent as SYSTEM).
   Whoever could change what is in the folder, a program or the installer itself, would take over the machine at your
   next install or update. A folder made in `C:\Program Files` gets that folder's permissions as it is made: other
   accounts can read it, never change it, not even for a moment. Do not make it directly under `C:\` and restrict it
   afterwards (with `icacls`, say): until its permissions change, every signed-in account may open it, game servers'
   accounts included, and an account that opened it keeps what it was allowed then (a game server's plugin could move
   the folder aside and put one of its own in its place). The installer checks the folder and refuses to run (changing
   nothing) when anyone but SYSTEM, Administrators and TrustedInstaller could change it, what is in it or a folder
   above it. That check catches a release extracted in the wrong place, but it is part of the installer it protects:
   it cannot stop someone who already changed the installer, so the folder is what keeps you safe. `New-Item` fails if
   the folder already exists: delete it (or choose a new name) rather than reuse it.

The installer copies the programs to `C:\Program Files\Pulsar` (and `Backup-Pulsar.ps1` into the panel's folder there);
the extracted folder is only needed while installing, and can be deleted afterwards. For an update, extract the new
release into a new folder made the same way ([update.md](update.md)).

## 3. Install

In the **Windows PowerShell as administrator** of step 2, go to the extracted folder:

```powershell
cd "$env:ProgramFiles\PulsarSetup"
```

### Panel and agent on this PC

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-Pulsar.ps1
```

It asks for a password for Pulsar's first administrator, `admin` (at least 12 characters; `-AdminUserName` chooses
another name), and for the `postgres` password when a PostgreSQL is installed already (step 1). It checks everything
it can before it changes anything (the folder, the ports, the passwords, what is installed), prints each step as
`[n/total] …`, and writes a log ([Unattended installs](#unattended-installs)). Then it:

1. downloads and checks what the machine lacks, then installs it: the .NET 10 runtimes and, without a PostgreSQL to
   use, PostgreSQL 18 (step 1; [What the installer downloads](#what-the-installer-downloads));
2. creates the `pulsar` database and user in the local PostgreSQL (with a random password);
3. installs the panel in `C:\Program Files\Pulsar\Panel` as the **PulsarPanel** service, running as its own virtual
   account (`NT SERVICE\PulsarPanel`), with its data in `C:\ProgramData\Pulsar\Panel` (keys, logs, certificate);
4. makes a self-signed HTTPS certificate for this PC's names and `localhost`, valid for 5 years, and trusts it on this
   PC (the agent and this PC's browsers check it). It is marked as unable to issue other certificates and as good for
   a web server's identity only (server authentication), so trusting it vouches only for the names it lists, never
   for other sites, code or clients; the installer prints its thumbprint and names;
5. opens the panel's port (**8443**, `-Port` chooses another) in Windows Firewall, on Domain and Private networks
   (`-FirewallPublic` also opens it on networks Windows treats as public; `-NoFirewall` leaves the firewall alone), and
   the SFTP port (**2022**, `-SftpPort` chooses another; it must differ from `-Port` and `-AgentsPort`) with its own
   rule, `Pulsar-Panel-SFTP`, the same way;
6. starts the panel and creates the administrator; if `-SftpPort` is not 2022 it sets that port in the panel's SFTP
   settings (the port is a panel setting kept in the database, not in the configuration file);
7. adds this PC as a node, installs the agent in `C:\Program Files\Pulsar\Agent` and enrolls it; the agent runs as the
   **PulsarAgent** service (LocalSystem) and keeps its data in `C:\ProgramData\Pulsar\Agent`. Game servers go to
   `C:\PulsarServers` (`-ServersRoot` chooses another folder).

When it ends, open <https://localhost:8443> and sign in as `admin`. The installer also prints the SFTP host key's
fingerprint (`SHA256:…`) to check at the first SFTP connection; users find it, with their SFTP address, in the panel's
**Account** page, and administrators change the SFTP switch, port and public host in **Nodes → SFTP**
(see [SFTP](sftp.md)). With `-RestoreFrom` the port comes back with the database: `-SftpPort` then only names the port
to open in the firewall.

### Panel and nodes on different machines

On the panel's machine:

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-Pulsar.ps1 -Mode Panel
```

To make the certificate valid for a name other machines use (a DNS name, or the address of a port forward), add
`-HostName panel.example.com` (letters, digits, `.`, `-` and `_`, or a leading `*.` for a wildcard; several names are
separated by commas). To use a real certificate instead of a self-signed one, pass it with
`-CertificatePath C:\certs\panel.pfx -CertificatePassword (Read-Host -AsSecureString)`. Agents check the panel's
certificate without downloading anything (so whoever answers at the panel's address cannot make them fetch addresses
of its choosing): the PFX must contain the intermediate certificates (the full chain), and each node must already trust
the authority's root in **Local Computer → Trusted Root Certification Authorities**. On a fresh Windows a public
authority's root may be missing until something validates a site with it: open the panel's address once in Edge on the
node, or import the root. The agent's log then says `PartialChain` (an intermediate is missing) or `UntrustedRoot`
(the root is).

Then, for each node: in the panel, **Nodes → Add node**, give it a name. The dialog shows the panel's address, a
**node code** (`pulsar-node-1.…`) and a link to `PulsarAgentSetup.exe` (see [The agent's setup](#the-agents-setup)).
With the ZIP instead: on the node, extract the same ZIP into a folder only administrators can change (step 2), open
PowerShell as administrator in it and run, with the code:

```powershell
$env:PULSAR_NODE_CODE = '<the node code>'
powershell -ExecutionPolicy Bypass -File .\Install-Pulsar.ps1 -Mode Agent
```

The node code carries the enrollment token, the fingerprint of the panel key the agent pins, the address the panel was
reached by and the SHA-256 of the certificate it presented then. It is read strictly (anything that is not a code the
panel makes is refused). The installer connects to that address, reads the certificate the panel presents without
trusting it, and compares its SHA-256 with the code's: another certificate (another machine answering at that address,
or a certificate changed since, or a proxy in front of the panel that serves its own certificate without marking the
requests it forwards) is refused and nothing is changed. Unless this machine trusts it already, the certificate the
code names must be a web server's own (Basic Constraints CA=false, server authentication alone) and name the address
used; the agent is then enrolled with it pinned (`PulsarAgent enroll … --panel-certificate <sha256>`): it accepts that
certificate from the panel and no other, and nothing is added to this machine's certificate stores, so the
certificate vouches for nothing else here, whatever names it lists. Nobody copies `panel.cer`. A code without a
certificate (the panel was reached through a proxy that adds `Forwarded`, `Via` or `X-Forwarded-*` headers) needs one
this machine trusts already. When this machine reaches the panel at another address than the code names (another DNS name, a port
forward), add `-PanelUrl https://that-name:port` (an origin, as the code has it): the certificate must still be the
code's, and list that name. The code is as secret as the token in it, and works once and for 30 minutes; `-NodeCode`
takes it too, but a command line shows in the process list.

The dialog's **Advanced** part shows the command the panel offered before node codes, which still works:

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-Pulsar.ps1 -Mode Agent -PanelUrl https://panel:8443 -Token <token> -PanelKey sha256:<fingerprint>
```

The command works once and for 30 minutes. The installer hands the token to the agent through the environment, not
its command line; to keep it off the installer's own command line too, leave `-Token` out and set the
`PULSAR_ENROLLMENT_TOKEN` environment variable in that PowerShell window. When you run `PulsarAgent enroll` yourself, prefer `--token -` (it asks for the token on standard
input) or the `PULSAR_ENROLLMENT_TOKEN` environment variable to `--token <token>`: other programs on the machine can see
command lines in the process list. With the panel's self-signed certificate, copy
`C:\ProgramData\Pulsar\Panel\tls\panel.cer` from the panel's machine to the node first and add
`-TrustCertificate .\panel.cer`: the node then trusts that certificate. The node appears as **Online** in the panel.
`-TrustCertificate` refuses a certificate that can issue other certificates (a certificate authority's: trusted by the
node, whoever holds its key could vouch for any site), and one that does not say whether it can (no Basic Constraints:
certificates made by installers before this check, or by hand; Windows may take them for a certificate authority).
Make a new certificate like the installer's (Basic Constraints, critical, CA=false), or add
`-TrustCertificateWithoutBasicConstraints` if you know it is the panel's own. It also refuses a certificate whose
Subject Alternative Name does not list the host of `-PanelUrl` (a DNS name, a wildcard for its first label, or an IP
address): the agent would not accept it there, and trusting it would vouch for names that are not the panel's. Install
the panel with `-HostName` for the name nodes use. A trusted certificate vouches for every name it lists and every use
it allows, so it also refuses, unless told:

- one that lists names other than the host of `-PanelUrl`, `localhost` and machine names (a name without a dot, such as
  the panel machine's own, which the installer's certificate always lists): another site's name, a wildcard, another
  IP address. Nodes that use one name of a panel known by several (its machine's full name in a domain, a
  `-HostName`) meet the others here: add `-TrustCertificateWithOtherNames` if every name it lists is yours;
- one that may be used for more than a web server's identity: an Extended Key Usage beyond server authentication, or
  none at all (which allows every use, code signing included). Installers before this check also listed client
  authentication; add `-TrustCertificateWithOtherUses` if you know it is the panel's own.

A name with characters a DNS name cannot have (a line break, an escape, a space) is refused outright. The node trusts
the certificate exactly as it was read and checked, and the installer prints its subject, thumbprint and names (with
any control character shown as `?`): whoever holds its key can pass for those names to this machine. `-Uninstall`
removes the trust it gave: only certificates it marked as its own when it trusted them.

The panel's machine must accept connections on its port (the installer opens it in Windows Firewall on Domain and
Private networks, see `-FirewallPublic` above; routers and cloud firewalls are up to you). Nodes only connect out to
the panel.

#### The agent's setup

The panel offers the agent's setup for download, without signing in, at `https://<panel>/downloads/PulsarAgentSetup.exe`
(the link in the Add node dialog, shown when the file is there). It serves that one file name, over HTTPS only, from
the folder `downloads` next to its program (`C:\Program Files\Pulsar\Panel\downloads`), or from the folder
`appsettings.Production.json` names:

```json
"Downloads": { "Path": "D:\\PulsarDownloads" }
```

(a relative path is taken from the program's folder). The release puts it there: the ZIP's and `PulsarSetup.exe`'s
panel carry `PulsarAgentSetup.exe` in its `downloads` folder, and the installer copies that folder with the panel
(for a folder of your own, copy it from the release). Whoever can change that folder chooses what new nodes run, so it
must be one only administrators can change, as the program's folder is. `-Update` replaces the panel's program and
keeps its `downloads` folder, but a release that carries a newer `PulsarAgentSetup.exe` replaces that file. Until the
setups are code-signed, download it on a node only from a panel whose certificate the node's browser trusts, or copy
it from the release, and compare its SHA-256 with the one the Add node dialog shows (`Get-FileHash
.\PulsarAgentSetup.exe`). Each address may have 4 downloads running and start 60 an hour (`"Downloads":
{ "ConcurrentPerAddress": 4, "PerAddressPerHour": 60 }`); more answer 429 until the hour ends.

#### Changing the panel's certificate

A node added with a node code pins the panel's certificate: `PulsarAgent status` shows it (`Panel TLS: certificate
SHA-256 … (pinned)`). Replacing the panel's certificate (a new self-signed one, a renewal, one from an authority in its
place) leaves those nodes offline until each is pinned again: the agent's log says the TLS handshake failed
(`RemoteCertificateValidationCallback`). For each, either

- enroll it again: **Add node** for a new code (`PulsarAgentSetup.exe`, or `Install-Pulsar.ps1 -Uninstall` then
  `-Mode Agent` with the code), or, in an elevated prompt on the node, `PulsarAgent enroll --panel <url> --token -
  --panel-key <fingerprint> --panel-certificate <the new certificate's SHA-256>` with a token from
  **New enrollment command** (the SHA-256 of `panel.cer`: `(Get-FileHash C:\ProgramData\Pulsar\Panel\tls\panel.cer).Hash.ToLower()`
  on the panel, for the installer's own certificate, which is its DER); or
- as an administrator of the node, set `"panelCertificate"` in `C:\ProgramData\Pulsar\Agent\config\agent.json` to the
  new SHA-256 (lowercase), or remove it once the node trusts the new certificate's authority, and restart the service
  (`PulsarAgent service stop`, then `start`).

#### Agents' port: client certificates (optional)

Add `-AgentsPort 8444` to the panel's install. Agents then present their node key as a TLS client certificate on that
second port, so a stolen session token is useless without the node's key. You do nothing on the nodes: agents move there
on their own and keep the main address as a fallback. Browsers keep using the main port and never see a certificate
request. **Nodes → Agent connections** shows which nodes still connect without a certificate; once every node has
moved, **Require client certificates** turns away agents that do not use one (agents too old to move cannot connect
until you update them).

- Open the agents' port wherever nodes reach the panel from (the installer opens it in Windows Firewall like the main
  one). It must reach the panel directly: a reverse proxy that decrypts TLS in front of it cannot pass the certificate
  on (a TCP pass-through can). A pass-through, or a router that rewrites the source of connections made from inside the
  network to the panel's public address (hairpin NAT), makes every agent come from one address, which then shares the
  budgets below: anyone with 20 idle connections through it would keep every agent out. Use a pass-through that keeps
  the client's address, or set `MaxPendingConnectionsPerAddress` and `MaxPendingConnectionsPerNetwork` to
  `MaxPendingConnections` and limit connections per client at the proxy. When nodes reach the port under another
  address (a port forwarded under another number), set it in `appsettings.Production.json`:
  `"Agents": { "PublicUrl": "https://panel.example.com:9444" }`.
- `"Agents": { "MaxConnections": 1000 }` (the default, about four per node) caps the connections the agents' port holds
  at once, so a flood of handshakes cannot take the panel's resources: raise it for more than about 250 nodes.
- A connection *signs in* when a request on it presents a valid agent session with the key of the connection's
  certificate (an agent's hub, its file transfers, a key rotation; a sign-in or an enrollment does not). Within
  `MaxConnections`, connections not signed in yet (a TLS handshake, an agent signing in or enrolling, anyone else's
  connection) have smaller budgets:
  - `"MaxPendingConnections"`: for the whole port, by default a quarter of `MaxConnections` (250). Half of it is kept
    for addresses an agent signed in from within the last day (each node's latest four addresses), so strangers
    alone never take all of it.
  - `"MaxPendingConnectionsPerAddress"`: from one address, an IPv4 address or an IPv6 /64 (the panel's own machine
    counts as one), 20 by default.
  - `"MaxPendingConnectionsPerNetwork"`: from one IPv4 /24 or IPv6 /48, three times the per-address budget by default
    (60), so a host with several addresses or a routed IPv6 prefix gets no more. It counts addresses no agent signed in
    from only: an agent's address keeps its own 20 whatever its neighbours do.

  A connection that never completes its TLS handshake is closed after 5 seconds. One not signed in 30 seconds after it
  arrived is closed once the request in progress, if any, is answered, and 5 seconds later in any case. A signed-in
  connection lasts as long as the session it presented (15 minutes at most) unless a request of a session is still
  running (a transfer is never cut). A node holds at most `"MaxConnectionsPerNode"` of them (16 by default): its file
  transfers take all but four (half, below 8), which stay for its hub, the hub it replaces and its rotation, and
  further requests with its session are refused. The panel starts at most half of that many transfers on a node at once
  (8 by default), and answers others "too many file transfers on this node" right away. A user gets up to three of
  them, the second and third only while two places stay free for users with none running, so it takes four users to
  fill a node. A transfer ends when its user signs out, is disabled or deleted, or gets other roles (and when any role
  changes), when the API key it was started with is revoked, and when it has lasted as long as its size allows at
  64 KiB/s (five minutes at least): a 1 GiB upload has four and a half hours. Revoking a node's key, disabling or
  deleting the node closes its connections at once.

  An agent's sign-in holds a connection for a second or two. Nodes behind one NAT address share its budgets: 20 lets
  them sign in about ten at a time and the others retry within seconds. Raise the per-address and per-network budgets
  (up to `MaxPendingConnections`) for a site with hundreds of nodes behind one address that restart together. The
  panel refuses to start with a budget below 1 or out of order (per address ≤ per network ≤ pending ≤
  `MaxConnections`, and 2 ≤ per node ≤ `MaxConnections`).
- What a flood of connections can still do: one address holds at most 20 places and one network 60, each for
  5 seconds if it never completes its handshake and up to 35 seconds from its arrival otherwise, so it cannot take the
  port alone. Many networks together (a botnet) can fill the strangers' half of the budget for as long as they keep it
  up: agents signing in from addresses no agent signed in from today are then refused, and so is a new node's
  enrollment. Agents that signed in from their address within the last day keep the other half, unless the flood also
  comes from their addresses; whoever holds a node's key can make up to four addresses of its own count as an agent's.
  Connected agents keep working until their session ends (at most 15 minutes); they then sign in like the others.
  While client certificates are not required, an agent that cannot sign in on the agents' port signs in at the main
  address instead, where its session is not bound to its key; once they are required, it keeps retrying the agents'
  port. Each node's connections are capped, but several nodes add up: machines enrolled with a reusable token that
  needs no approval can together hold the whole port. The panel's log says how many connections it closed and why,
  and which nodes it refused, at most once a minute.
- Whatever shares an agent's address can use up that address's budget: a game server on the node, a neighbour behind
  the same NAT. Before TLS the panel cannot tell them from the agent, so 20 idle connections from a game server keep its
  own node from signing in (until they stop). Game servers run under their own Windows accounts (`pulsar_<id>`, one
  per server), so the firewall can keep them from the panel's ports. With `Pulsar:ManageFirewall` on (see
  [First steps](#4-first-steps-in-the-panel)), the agent does it itself: an outbound rule `Pulsar <id> panel block` for
  each server's account, made when the server is created and when the agent starts, kept up to date when the panel's
  addresses or the machine's change (and every 5 minutes), and removed with the server. On a node whose firewall you
  manage, block them yourself, and run it again after you create or delete a server, or when the panel's addresses
  change (it replaces the rule):

  ```powershell
  $panelName = 'panel.example.com'  # the host in the agent's panel address
  $panelPorts = 8443, 8444          # the panel's port and its agents' port
  $rule = 'Pulsar: game servers may not reach the panel'
  $users = @(Get-LocalUser -Name 'pulsar_*' | ForEach-Object { "(A;;CC;;;$($_.SID))" })
  # Every address of the name (IPv4 and IPv6) and this machine's own; loopback as ranges (the firewall refuses ::1).
  $addresses = @([Net.Dns]::GetHostAddresses($panelName).IPAddressToString) + @((Get-NetIPAddress).IPAddress) |
      ForEach-Object { $_ -replace '%.*$' } | Where-Object { $_ -ne '::1' -and $_ -notlike '127.*' } | Select-Object -Unique
  Remove-NetFirewallRule -DisplayName $rule -ErrorAction SilentlyContinue
  if ($users.Count -gt 0) {
      New-NetFirewallRule -DisplayName $rule -Direction Outbound -Action Block -Protocol TCP `
          -RemoteAddress (@('127.0.0.0/8', '::/127') + $addresses) -RemotePort $panelPorts -LocalUser ('D:' + ($users -join ''))
  }
  ```

  Only the panel's ports are blocked, at every address the name resolves to, loopback and the node's own addresses:
  when the panel's public name leads back to the node through the router, a game server reaches it there too.
  Windows Firewall does not filter loopback, so on a PC that runs the panel too this rule cannot keep a game from
  it: run untrusted game servers on another machine. The agent runs as SYSTEM and is not affected. With no game server yet there is nothing to
  block, and the rule is removed.

  Either way the rule narrows this down, it does not rule it out. It blocks the addresses it lists: one of the
  panel's that the name did not return here (a second address, what GeoDNS or anycast give other networks, a NAT64
  form) stays open. It needs Windows Firewall on for the network profile in use and local rules applied: a
  third-party firewall, a disabled profile, or group policy that does not merge local rules make it do nothing (the
  agent's log warns about these). And it blocks the panel's ports on every address it lists: with the panel on 443
  behind a shared proxy or CDN address, game servers cannot reach anything on 443 at that address.
- On a panel installed before, add the endpoint to `C:\Program Files\Pulsar\Panel\appsettings.Production.json`
  with the same certificate as `Https`, open the port, and restart the panel:

  ```json
  "Kestrel": { "Endpoints": {
    "Https": { "Url": "https://*:8443", "Certificate": { "Path": "...", "Password": "..." } },
    "Agents": { "Url": "https://*:8444", "Certificate": { "Path": "...", "Password": "..." } }
  } }
  ```

### Many nodes at once

To enroll many machines with one command, for example with Intune or a group policy, create a reusable token in the
panel: **Nodes → Reusable enrollment tokens → Create token**. Give it a name, how many machines may use it (up to
1000) and for how many days (up to 90). Keep **New nodes wait for my approval** checked unless you deploy only to
machines you control end to end. With it checked, a machine that enrolls appears in **Nodes** as waiting, and its agent
connects once you approve it: one node from its page, or many at once from the list. The token is shown once, inside
two commands:

- **The agent MSI** (`pulsar-agent-<version>-windows-x64.msi`, next to the ZIP in the release; check it against its
  `.sha256` file as you do the ZIP), installed silently as administrator:

  ```powershell
  msiexec /i pulsar-agent-<version>-windows-x64.msi /qn PANELURL=https://panel:8443 TOKEN=<token> PANELKEY=sha256:<fingerprint>
  ```

  The installer cannot take the token any other way than on a command line (the package's `enroll` step receives it
  as `--token "..."`), so it is visible, as `TOKEN=` is on `msiexec`'s, to administrators of the machine while it
  runs; the installer's log hides it. Use a reusable token that waits for approval and expires soon, and revoke it
  when the rollout ends.

  Add `SERVERSROOT=D:\PulsarServers` to put game servers somewhere other than `C:\PulsarServers`: a new or empty folder
  (or the one an earlier enrollment used), not one inside a folder other users may change (`C:\Users\Public`, a
  download folder), which enrollment refuses. The package copies the
  agent to `C:\Program Files\Pulsar\Agent`, enrolls the machine (what `enroll` printed is in
  `C:\ProgramData\Pulsar\Agent\logs\enroll.log`) and starts the service; if enrolling fails, `msiexec` ends with
  error 1603 and nothing is left installed. In Intune, add it as a line-of-business or Win32 app with that command
  line; with a group policy, run it from a computer startup script, since software installation policies cannot pass
  the token.

  The package needs 64-bit Windows 10 version 1607, Windows Server 2016 or later, but unlike `Install-Pulsar.ps1` it
  does not check for the Windows updates Pulsar needs (see Requirements): on a Windows that lacks them, the agent does
  not start, the installation fails and is undone, and `enroll.log` has no entry for that attempt (add
  `/l*v <file>` to the command for the installer's own log). Install the updates first.

  The MSI and the installer script do not share an agent. The package refuses to install where an agent installed
  from the ZIP runs (the PulsarAgent service exists): remove that one first with `Install-Pulsar.ps1 -Uninstall`.
  The script leaves an agent installed by the MSI alone: `-Mode Agent` and `-Mode AllInOne` refuse to run next to it,
  and `-Update` and `-Uninstall` handle only the panel.
- **The installer script**, as for a single node: `Install-Pulsar.ps1 -Mode Agent -PanelUrl … -Token … -PanelKey …`.

Each machine becomes a new node named after the computer (with `-2`, `-3`… when a node already has that name). The
panel's certificate must be trusted by every machine, as for one node: distribute `panel.cer` with the same tool (a
group policy or an Intune certificate profile) before the agent, or use a certificate from a trusted authority. The
token travels in the command line, which the machine's administrators can see while it runs (the MSI keeps it out of
its log): revoke the token once the rollout is done.

The panel takes at most 10 enrollments a minute from one address. Machines that reach it through the same NAT address
share that limit: the ones turned away wait as the panel says and try again for up to 10 minutes (a refused token is
not tried again), so a large rollout from one site is slow or fails on some machines. Before one, raise the limit in
the panel's `C:\Program Files\Pulsar\Panel\appsettings.Production.json` and restart the panel service; set it back
afterwards:

```json
"RateLimiting": { "Auth": { "EnrollPermitLimit": 200 } }
```

### What the installer downloads

Only what the machine lacks, only over HTTPS (TLS 1.2 or later, following redirects to HTTPS addresses only, with
sizes capped), into a new folder in `C:\ProgramData\Pulsar\Setup` that only SYSTEM and Administrators can change,
deleted when the installer ends. Everything is downloaded and checked before anything is installed: a download that
fails, or fails a check, is not run, and nothing is changed.

| What | Where from | Checked |
|---|---|---|
| The .NET runtime and, for the panel, the ASP.NET Core runtime: the newest 10.0 patch, at least the one the release was built with (its `release.json`) | Microsoft's release metadata (`https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json`) and the Windows x64 installers it lists, from Microsoft's download hosts only | The SHA-512 the metadata lists; the Authenticode signature: valid (Windows chains it to a root it trusts), in the file itself, by a certificate for code signing whose subject is `O=Microsoft Corporation, L=Redmond, S=Washington, C=US` and whose chain ends at Microsoft Root Certificate Authority 2011 (or 2010); the version information (`Microsoft .NET Runtime - 10.0.<n> (x64)`, version 10.0.<n>) and the version |
| PostgreSQL: the newest release of major version 18 (never a newer major version on its own: that needs a data upgrade), never older than the release's floor (`postgresMinimum` in its `release.json`), and EDB's newest build of it | postgresql.org's list of versions (`https://www.postgresql.org/versions.json`); EDB's installer, `https://get.enterprisedb.com/postgresql/postgresql-18.<minor>-<build>-windows-x64.exe` | The Authenticode signature: valid, in the file itself, by a certificate for code signing whose subject is `O=EnterpriseDB Corporation, L=Wilmington, S=Delaware, C=US`, issued by `DigiCert Trusted G4 Code Signing RSA4096 SHA384 2021 CA1`; the version information (a `PostgreSQL` product of version 18.<minor>) |

Every download also has a time limit: 10 minutes, and a second more for each 128 KiB of its size.

The signers' identities are those of EDB's signed PostgreSQL 18 programs and Microsoft's .NET 10 installers when the
release was made. Before a release, its Sandbox run (`scripts/sandbox/install`) must confirm them on the real
downloads: EDB's installer's signer and issuer (`Get-AuthenticodeSignature` on it, and its `SignerCertificate.Issuer`)
and its version information (`(Get-Item <installer>).VersionInfo`: a `ProductName` with `PostgreSQL` in it and a
`ProductVersion` or `FileVersion` starting with `18.<minor>`). When a publisher changes them (a new certificate
authority, a new address in its certificate), the installer refuses its files (exit code 5) until a release of
Pulsar names the new ones.

The .NET installers run with `/install /quiet /norestart`; their logs go to `C:\ProgramData\Pulsar\Setup\logs`. One
that asks for a restart does not get it: the installer finishes, says so, and (unattended) ends with code 3010.
Pulsar's programs use the newest 10.0 runtime installed in `C:\Program Files\dotnet`, where Microsoft's installers put
it; the installer refuses one Windows would load from elsewhere (a machine-wide `DOTNET_ROOT`) or that others could
change, since the agent runs as SYSTEM. It also drops `DOTNET_*`, `COREHOST_*`, `COMPlus_*` and `CORECLR_*`
variables from its own environment, so that the programs it runs as administrator take nothing from this account's.

Microsoft Update keeps the .NET runtimes patched once Windows Update's **Receive updates for other Microsoft
products** is on (Settings → Windows Update → Advanced options); on servers, through WSUS or your update tool. Nothing
updates PostgreSQL on its own: install a newer 18.x with EDB's installer of it (it keeps the data), and plan an upgrade
to a newer major version yourself.

"The newest" means releases nobody tested with this release of Pulsar: a patch of .NET 10 or a minor release of
PostgreSQL 18 is meant to change nothing Pulsar relies on, but that is their publishers' promise, not a check.

### Offline installs

On a machine without internet, give the installer what it would download (fetched and checked elsewhere; it checks
them again):

- `-DotNetInstallers <folder>`: Microsoft's `dotnet-runtime-10.0.<n>-win-x64.exe` and, for a panel,
  `aspnetcore-runtime-10.0.<n>-win-x64.exe` (the Windows x64 installers of the .NET Runtime and the ASP.NET Core
  Runtime, from <https://dotnet.microsoft.com/download/dotnet/10.0>). The newest of each in the folder is used: it must
  be at least the version this release needs and be signed by Microsoft, and, when Microsoft's release metadata can be
  read after all, match the SHA-512 it lists for that version.
- `-PostgresInstaller <file>`: EDB's installer, `postgresql-18.<minor>-<build>-windows-x64.exe` (from
  <https://www.enterprisedb.com/downloads/postgres-postgresql-downloads>; PostgreSQL 15 or later, its name gives the
  version, which its version information must confirm; of 18, at least the release's floor), signed by EnterpriseDB.

Both are copied into the installer's private folder and checked and run from there. Without internet and without
them, the installer says what it could not download and changes nothing; a panel can also use an existing PostgreSQL
(step 1) or `-ConnectionString`. The setups take them as `/DOTNETINSTALLERS=<folder>` and `/POSTGRESINSTALLER=<file>`
([Silent installs with the setups](#silent-installs-with-the-setups); the wizard shows its pages as usual with them).

### Unattended installs

For the setup wizards and other automation, `-Unattended` runs the installer without asking anything. Run it as
administrator, in 64-bit Windows PowerShell (a 32-bit program reaches it as
`%windir%\Sysnative\WindowsPowerShell\v1.0\powershell.exe`), from a folder only administrators can change (step 2).

Secrets come from the environment of the elevated process that runs it, or from a secrets file; never from the command
line, which other programs on the machine can read. The installer removes each of these variables from its own
environment as it reads them, so nothing it starts inherits them, and a parameter given on the command line wins over
the secrets file, which wins over the environment:

| Secret | Environment variable | In the secrets file |
|---|---|---|
| The first administrator's password | `PULSAR_ADMIN_PASSWORD` | `adminPassword` |
| An existing PostgreSQL's `postgres` password | `PULSAR_POSTGRES_PASSWORD` | `postgresPassword` |
| A database elsewhere (`-ConnectionString`) | `PULSAR_CONNECTION_STRING` | `connectionString` |
| The password of `-CertificatePath`'s PFX | `PULSAR_CERTIFICATE_PASSWORD` | `certificatePassword` |
| A node code (`-Mode Agent`) | `PULSAR_NODE_CODE` | `nodeCode` |
| An enrollment token (`-Mode Agent` with `-PanelUrl` and `-PanelKey`) | `PULSAR_ENROLLMENT_TOKEN` | `enrollmentToken` |

The secrets file (`-SecretsFile <path>`, or `PULSAR_SECRETS_FILE`) is UTF-8 JSON, one object with any of those names
and text values, at most 64 KiB: `{"adminPassword": "…"}`. Only SYSTEM and Administrators may own or open it, and it
must be in a folder only they may change (one made for them alone): otherwise it is refused unread, since whoever could
replace it would choose the administrator's password. The installer deletes it once read.

What would be asked is an error instead (exit code 2), naming its variable. Standard output carries, besides the
details:

- `PULSAR-LOG <path>`, first: this run's log;
- `PULSAR-STEP <n>/<total> <text>`: each step of the run, once and in order (a step it does not need says so), for a
  progress bar;
- `PULSAR-PANEL-URL https://localhost:<port>/`, at the end of a panel's install: its sign-in page;
- `PULSAR-ERROR <code> <message>`, on one line, when it fails.

It ends with one of these exit codes (without `-Unattended`, a failure is a PowerShell error, exit code 1 under
`powershell -File`):

| Code | Meaning |
|---|---|
| `0` | Done. |
| `1` | It failed; the message, and the log, say what failed and what was left. |
| `2` | Wrong or missing parameters, secrets or node code, or what it would install is installed already (`-Update` updates it). Nothing was changed. |
| `3` | This machine, the installer's folder or the account cannot install Pulsar as they are: not elevated, a 32-bit PowerShell, a folder others can change, Windows updates missing, an agent from the MSI, a PostgreSQL port, service or folder taken. Nothing was changed (but for the .NET runtimes, when the Windows updates check fails after they were installed). |
| `4` | A download, or the panel at a node code's address, could not be reached. Nothing was changed. |
| `5` | A download or a given installer failed its checks (signature, SHA-512, version), or Microsoft's or postgresql.org's metadata was not as expected. Nothing was changed. |
| `6` | The certificate the panel presents is not the node code's (another machine, or a proxy serving its own), or may not be trusted or pinned (it can issue certificates, does not name the panel's address...). Nothing was changed. |
| `3010` | Done; a .NET installer asks for Windows to be restarted to finish (Pulsar runs meanwhile). |

PowerShell itself ends with 1 when it cannot bind a parameter (a misspelt one).

Every run, unattended or not, writes a log to `C:\ProgramData\Pulsar\Setup\logs\install-<date>-<process>.log`: what
it printed, the parameters it was given (secrets named, never shown), the installers it ran and their arguments, and
the error, with every secret it knows of replaced by `[secret]`: the passwords, tokens and node code it was given, the
passwords it makes (PostgreSQL's, the HTTPS certificate's and the key ring's PFX passwords, the setup token), those it
reads from a `-RestoreFrom` backup, and the enrollment token and node code the all-in-one install gets from the panel.
The folder is for SYSTEM and Administrators only.

#### Secrets and PowerShell's logging

The installer builds its SecureStrings character by character, never with `ConvertTo-SecureString -AsPlainText`,
whose parameters PowerShell's **module logging** writes to the event log. Logging that a machine's policy turns on
for every script can still see secrets: **Script Block Logging** records script blocks as they are compiled (the
installer's own code holds none, but its run does), and **transcription** (`Turn on PowerShell Transcription`) records
everything a session prints and the values some commands show. On a machine where those policies are on, their logs
(`Microsoft-Windows-PowerShell/Operational`, the transcripts' folder) are as secret as the passwords the install was
given: run it where they are off, or protect and clear those logs afterwards. A failed PostgreSQL install keeps EDB's own logs in
`C:\ProgramData\Pulsar\Setup\postgresql-install-<date>` (administrators only; they may hold the password that attempt
was given): delete the folder once read.

## 4. First steps in the panel

1. **Account → Two-factor authentication**: set it up with an authenticator app and keep the recovery codes. In
   **Roles**, *Require two-factor* on the Administrator role makes it mandatory for administrators.
2. **Nodes → your node → Allocations**: add the IP addresses and ports game servers may use (for example
   `0.0.0.0`, ports `27015-27030`).
3. **Servers → New server**: choose the node and the game (Counter-Strike 2, Counter-Strike: Source), name it, and
   set its variables. The node downloads the game with SteamCMD; the progress shows on the server's page.
4. Open the game's ports to players: in Windows Firewall on the node (or let the agent manage rules for its servers:
   add `"Pulsar": { "ManageFirewall": true }` to `C:\Program Files\Pulsar\Agent\appsettings.Production.json` and
   restart the PulsarAgent service), and in your router if players come from the internet. The agent's rules apply to
   every network profile, Public included (the network adapters of cloud servers are often Public), and only to that
   game's program and the ports it declares. The agent then also blocks each game server's account from the panel's
   ports, so a game cannot easily hold the connections its node signs in with (see the agents' port above for what
   this rule does not cover).

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
| The installer's logs (and, while it runs, its downloads) | `C:\ProgramData\Pulsar\Setup` |
| The setup's uninstaller and the copy of `Install-Pulsar.ps1` it runs (setups only) | `C:\Program Files\Pulsar\Setup` |
| The .NET runtimes (Microsoft's, shared with other programs) | `C:\Program Files\dotnet` |
| PostgreSQL, when the installer installed it: programs and data | `C:\Program Files\PostgreSQL\18` (the data in `data`) |

Back up the panel as described in [backup.md](backup.md): without its keys, the database's secrets cannot be read.

## Troubleshooting

- **SmartScreen says *Windows protected your PC***: the setups are not code-signed yet. Check the file's SHA-256
  against the `.sha256` file from the Releases page, then **More info → Run anyway**.
- **The setup says Pulsar was not installed**: the installer changed nothing, and the setup undid its own files. The
  message, and the installer's log it names, say why (the cases below apply); fix it and run the setup again. With
  `/LOG`, the setup writes a log of its own too (its steps and the installer's exit code, never its output, which is
  in the installer's log).
- **The installer stops with an error**: it says what failed, and its log in `C:\ProgramData\Pulsar\Setup\logs` has
  the details. Run it again after fixing it; if the panel or the agent was already installed, remove it first with
  `-Uninstall`, or from **Apps** when a setup installed it (it keeps the database and data).
- **"could not be downloaded" / "could not be reached"**: the machine has no internet, or a proxy or firewall stops
  HTTPS to Microsoft's, postgresql.org's or EDB's servers. Nothing was installed; see
  [Offline installs](#offline-installs).
- **"is not validly signed" / "is signed by …, not by …" / "does not match the SHA-512"**: a download (or an installer
  given offline) is not what its publisher made, or Windows cannot check its signature (on a machine without internet,
  Windows may lack the root certificate the signature chains to; open any HTTPS site once, or import the root). It was
  not run and nothing was changed: download it again, from the publisher's site.
- **"PostgreSQL 18 was installed, but setting it up failed"**: remove PostgreSQL 18 from Apps (and
  `C:\Program Files\PostgreSQL\18`, whose data holds nothing yet) and run the installer again.
- **"It refuses to run from a folder that other accounts could change"**: the release was extracted where accounts
  other than administrators can change it (a download folder, a folder made directly under `C:\`, a USB stick). It
  lists who could change what. Extract it again into a new folder made as in step 2 and run it from there; nothing was
  installed.
- **"This Windows lacks updates that Pulsar needs" / "Your Windows doesn't fully support CET"**: install all available
  Windows updates, restart and run the installer again (see Requirements). Nothing was installed.
- **The panel does not start**: `C:\ProgramData\Pulsar\Panel\logs\panel-<date>.log` says why (database unreachable,
  port in use…). `Get-Service PulsarPanel` shows its state.
- **A node stays offline**: on the node, `& 'C:\Program Files\Pulsar\Agent\PulsarAgent.exe' status --check` checks the
  connection to the panel; its log is in `C:\ProgramData\Pulsar\Agent\logs`. `PartialChain` there means the panel's
  certificate file lacks its intermediate certificates; `UntrustedRoot`, that the node does not trust its authority's
  root yet (see "Panel and nodes on different machines").
- **The browser warns about the certificate**: expected with the self-signed certificate on other computers. Trust
  `panel.cer` on them, or install a real certificate (`-CertificatePath` when installing; to change it later, edit
  `Kestrel:Endpoints:Https:Certificate` in the panel's `appsettings.Production.json` and restart the service).

## Uninstalling

Installed with a setup: **Settings → Apps → Pulsar → Uninstall** (silently: the `UninstallString` of its entry, with
`/VERYSILENT /SUPPRESSMSGBOXES`). It runs the copy of `Install-Pulsar.ps1 -Uninstall` the setup kept in
`C:\Program Files\Pulsar\Setup\payload`, as below; when that fails, it says why and removes nothing more, keeping its
entry to try again. There is no option to delete the data: it is kept, as below. Otherwise, from the release folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-Pulsar.ps1 -Uninstall
```

It removes the services, the programs (`Backup-Pulsar.ps1` in the panel's folder too), the firewall rules
(`Pulsar-Panel-HTTPS`, `Pulsar-Panel-Agents` for the agents' port and `Pulsar-Panel-SFTP` for SFTP) and the trust the script gave the panel's
certificate (in all-in-one mode, or with `-TrustCertificate`; a certificate this machine trusted already stays
trusted). It removes only certificates it marked as its own when it trusted them: one that an install made before
that mark only noted in the panel's data folder is named in a warning instead, for you to remove in `certlm.msc`
(*Trusted Root Certification Authorities*) if it is the panel's (the panel's service account can change that note, so
the script does not act on it). It keeps the
database, `C:\ProgramData\Pulsar` and the game servers' folders, and PostgreSQL and the .NET runtimes, which other
programs may use: remove them from **Apps** when nothing else needs them (PostgreSQL's data folder stays until you
delete it). Delete game servers from the panel first: that also
removes their Windows accounts. An agent installed with the MSI is not the script's to remove: it says so and removes
only the panel, if this machine has one.

If the agent cannot unenroll (stop its game servers, delete its key and configuration, remove its service), the script
stops there and keeps the agent, as the MSI does (see below): what `unenroll` printed says why; fix that and run
`-Uninstall` again.

An agent installed with the MSI is removed from **Apps** in Windows settings, or with
`msiexec /x pulsar-agent-<version>-windows-x64.msi /qn`: its game servers stop, and the service, the node key, the
configuration, the game servers' Windows accounts and firewall rules, their saved passwords and the cached Steam logins
go, as with `PulsarAgent unenroll` (what it printed is in `C:\ProgramData\Pulsar\Agent\logs\unenroll.log`). The game
servers' folders, the backups and the logs stay; `unenroll` says where, delete them once you no longer need them. Then the program goes; an `appsettings.Production.json` you added to
its folder stays, delete it by hand. The node stays in the panel, offline, until you delete it there.

If the agent cannot unenroll, the uninstall fails (`msiexec` ends with error 1603) and the program stays, so that it
can be retried instead of leaving a service without its program, the node key and the configuration behind.
`unenroll.log` says why; fix that and uninstall again. Running
`& 'C:\Program Files\Pulsar\Agent\PulsarAgent.exe' unenroll` in an elevated PowerShell shows the same error, and once
it succeeds the uninstall goes through.

A configuration (`C:\ProgramData\Pulsar\Agent\config\agent.json`) that cannot be read does not stop it: the service,
the game servers and the configuration are removed anyway, but the node key cannot be found without the configuration
and stays in the machine's key store (`unenroll.log` says so; once you delete the node in the panel it opens nothing).
Nor does a missing configuration: the service is still removed when it runs with that state directory.

A service still stopping its game servers after a minute is removed once it stops: `unenroll` ends the games and gives
it 30 s more. If it is still stopping then, it could yet save the servers' passwords or log on as their accounts, so the
game servers' Windows accounts (`pulsar_<id>`) and `C:\ProgramData\Pulsar\Agent\state` stay and `unenroll.log` names
them: once `PulsarAgent` no longer shows in `services.msc`, delete the accounts in Computer Management (Local Users and
Groups) and the folder by hand.

As a last resort, when `unenroll` keeps failing for a reason you cannot fix, remove what it would have removed by hand,
in an elevated PowerShell, then uninstall again:

```powershell
& 'C:\Program Files\Pulsar\Agent\PulsarAgent.exe' service stop --stop-servers  # stops its game servers gracefully
sc.exe delete PulsarAgent
Remove-Item C:\ProgramData\Pulsar\Agent\config\agent.json
```

With the service and the configuration gone, `unenroll` finds nothing to remove and succeeds. The node key stays in the
key store, and the node stays in the panel until you delete it there.
