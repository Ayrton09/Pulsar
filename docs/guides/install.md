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

### With one command

In PowerShell **run as administrator** (right-click Windows PowerShell, *Run as administrator*), on the PC to install:

```powershell
irm https://github.com/Ayrton09/Pulsar/releases/latest/download/install.ps1 | iex
```

It downloads the newest release's `PulsarSetup-<version>.exe` and runs it: the same wizard as below, which installs
the panel, this PC as a node, or both. Before running anything, it checks the release as installed Pulsars check an
update: the release's manifest (`pulsar-update.json`) must be signed by the project's update key, which the script
carries ([releasing.md](releasing.md)), and the setup must have the size and SHA-256 that signed manifest gives; it is
downloaded into a new folder in Program Files, which only administrators can change, run from there, and the folder is
removed afterwards. A release that anyone without the maintainer's key published or changed on GitHub is refused.

`install.ps1` itself is what GitHub serves for the newest release, over HTTPS, so that line trusts GitHub for it as a
download from the Releases page does. To read it first, download it from the Releases page (it is named by the signed
manifest too, with its SHA-256, and has its own `.sha256` file), check it, and run it from PowerShell as
administrator: `powershell -ExecutionPolicy Bypass -File .\install.ps1`, adding `-Agent` for a node only
(`PulsarAgentSetup`, below). For a [silent install](#silent-installs-with-the-setups), pass the setup's options from
within PowerShell, where a list is a list: `& .\install.ps1 -SetupArguments '/VERYSILENT', '/SUPPRESSMSGBOXES', ...`
(after `Unblock-File .\install.ps1`, if the execution policy asks for it). When the setup ends with an exit code other
than `0`, the script stops with an error that names it; `3010` (done, restart Windows, with `/RESTARTEXITCODE=3010`) is
a success that says so. Piped to `iex`, the script runs in a scope of its own and leaves nothing in that PowerShell.

### The panel's PC: PulsarSetup.exe

1. Download `PulsarSetup-<version>.exe` and its `.sha256` file from the project's
   [Releases](https://github.com/Ayrton09/Pulsar/releases) page, and check it as [step 2](#2-download-pulsar) shows
   for the ZIP: `(Get-FileHash .\PulsarSetup-<version>.exe -Algorithm SHA256).Hash` must show the value in the
   `.sha256` file. A release built by the repository's release workflow on GitHub attests its provenance, the ZIP's and
   the setups': with the GitHub CLI, `gh attestation verify .\PulsarSetup-<version>.exe --repo Ayrton09/Pulsar` (and
   the same for `PulsarAgentSetup-<version>.exe`) says it was built by that workflow from the tag's commit. **Releases
   the maintainer builds on their own PC and publishes with `Publish-GitHubRelease.ps1` (the ones installed panels and
   agents update to) carry no attestation, and that command fails for them**: the SHA-256 file is their check, and
   the signature of the update manifest, which panels and agents verify themselves, is the one for updates.
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
      for [client certificates](#agents-port-client-certificates-optional) (none by default), one more name or IP
      address for the panel's certificate (a DNS name or a public address nodes and browsers use: this PC's names and
      local network addresses are in it already; several need the script's `-HostName`), opening the ports on public
      networks too, a PFX certificate of your own and its password, and the connection string of a PostgreSQL
      elsewhere.
   8. **Ready**: what it will do (never a password).
4. It installs, showing each step: it downloads what this PC lacks (the newest .NET 10 runtimes and, for a panel
   without a PostgreSQL to use, the newest PostgreSQL 18) and checks every download before anything runs (the table
   in [What the installer downloads](#what-the-installer-downloads)), then installs .NET, PostgreSQL (listening on this
   PC only), the panel, its administrator and, with a node, the agent.
5. **Finish** shows the panel's sign-in page, `https://localhost:8443/`, in a box to copy (the setup runs as
   administrator, so it opens no browser itself), and below it the **certificate fingerprint (SHA-256)** of the
   panel's HTTPS certificate: note it, to check the certificate from your other PCs
   ([Connecting from another PC](#connecting-from-another-pc)). Open the address in your browser and sign in as
   `admin`. Then see [First steps in the panel](#4-first-steps-in-the-panel).

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
address too when the node reaches the panel at another address than the code names: `https://`, the name or IP
address and the port, nothing after it, which the setup writes in lower case) and the game servers' folder.
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
`/SFTPPORT=`, `/AGENTSPORT=`, `/HOSTNAME=`, `/CERTIFICATE=<pfx>`, `/FIREWALLPUBLIC=yes`, `/PANELURL=`, `/LANG=en|es`
(a silent setup checks the ports, each from 1 to 65535, before it installs anything; one that is not stops it
with Inno Setup's own exit code `1` and a line in its `/LOG`, instead of a script error that would look like an install that
changed the PC),
and, offline, `/DOTNETINSTALLERS=<folder>` and `/POSTGRESINSTALLER=<file>` ([Offline installs](#offline-installs)).
`PulsarAgentSetup-<version>.exe` takes `/SERVERSROOT=`, `/PANELURL=` and `/DOTNETINSTALLERS=`, with the code in
`PULSAR_NODE_CODE`. Exit codes: `0` done (add `/RESTARTEXITCODE=3010` to get 3010 when a .NET installer asks for a
restart); `5` the installer stopped and changed nothing, and the setup undid its own files (it cancels its own
install, which is Inno Setup's code for a cancelled install; always pass `/SUPPRESSMSGBOXES`: without it, a very silent
setup shows an error about a file named `install-guard` there, to be answered with **Abort**); `100` the installer
failed after changing something (remove Pulsar from **Apps**); the installer's log says why. Inno Setup's other codes
mean what [its documentation](https://jrsoftware.org/ishelp/index.php?topic=setupexitcodes) says.

### Updating and removing

A newer `PulsarSetup.exe` (or `PulsarAgentSetup.exe` on a node) run on a PC where Pulsar is installed updates it,
asking nothing but the license ([update.md](update.md)); a setup older than what is installed refuses to run. To
remove Pulsar, **Settings → Apps → Pulsar → Uninstall**: it runs `Install-Pulsar.ps1 -Uninstall`
([Uninstalling](#uninstalling)) and keeps the data. Remove Pulsar before the .NET runtimes: the agent's program needs
them to unenroll.

## Connecting from another PC

The panel and its SFTP server answer the other computers of your network: the installer opens their ports (8443 and
2022) in Windows Firewall on Domain and Private networks (on a network Windows treats as public, set it to Private in
Windows' network settings, or install with `-FirewallPublic`).

- **The address**: the panel PC's IP address on your network, `https://192.168.1.20:8443` (the installer prints it at
  the end, `From other computers: …`; `ipconfig` on that PC shows it as its IPv4 address), or its name,
  `https://<PC name>:8443`, which home networks do not always resolve. The panel's self-signed certificate lists both:
  `localhost`, the PC's name (and its DNS name in a domain), `127.0.0.1`, `::1` and the PC's private IPv4 addresses
  (`10.x`, `172.16.x` to `172.31.x`, `192.168.x`) on networks with a router (a default gateway), each address as an IP
  address, which is what browsers match an address against. Left out on their own: the addresses of virtual switches
  that reach only that PC's virtual machines and WSL (Hyper-V's Default Switch, WSL's, VirtualBox's and VMware's
  host-only networks: no gateway, and a new address at every start), `169.254.x` addresses, public addresses and IPv6
  addresses. Add those you use with `-HostName` (`-HostName 203.0.113.7, panel.example.com`), or one with the setup's
  Advanced page.
- **Reserve the address** in your router (a *DHCP reservation* or *static lease* for the PC's network adapter), so it
  never changes: the certificate lists the addresses the PC had when Pulsar was installed, and the installer warns
  (in its output and its log) about each one a DHCP server gave
  ([Your server's IP changed](#your-servers-ip-changed)).
- **SFTP**: the same address and port 2022 (`sftp://<user>@192.168.1.20:2022/`); at the first connection, check the
  host key's fingerprint against the one the installer printed on the panel's PC (`The SFTP host key, to check at the
  first connection: …`, in its output and its log), or the Account page's once this PC trusts the panel's certificate
  ([Trusting the panel's certificate](#trusting-the-panels-certificate)): before that, a machine in the middle could
  change what the page says ([SFTP](sftp.md)). SFTP does not use the HTTPS certificate.
- **More nodes**: open **Nodes → Add node** at the address the new node will use (the PC's IP address, say): the node
  code carries that address, which the certificate lists, and `PulsarAgentSetup.exe` pins the certificate there.

### Trusting the panel's certificate

Until a browser trusts the panel's self-signed certificate, it warns (*Your connection isn't private*) at every visit.
Trust it once on each PC (and in Firefox, which keeps its own list):

1. On that PC, open the panel at the address you will use (click through the warning this once), sign in and open
   **My account → Panel certificate**. **Download** saves `pulsar-panel.cer`, the certificate the panel presented to
   this browser.
2. **Check that it is your panel's** before trusting it: its SHA-256 must be the fingerprint the installer showed on
   the panel's PC (the setup's last page, the end of the installer's output and its log, or
   `(Get-FileHash C:\ProgramData\Pulsar\Panel\tls\panel.cer).Hash` there). On this PC,
   `(Get-FileHash $HOME\Downloads\pulsar-panel.cer).Hash` shows the file's (the same digits, without the colons), and the
   browser shows the site's (the warning, or the address bar's icon → the certificate → SHA-256 fingerprint). The
   Account page shows it too, but until the browser trusts the certificate a machine in the middle could change what
   the page says: compare the file's or the browser's, never the page's alone. Different digits: something else
   answered at that address; do not trust it.
3. Windows (Edge, Chrome): double-click `pulsar-panel.cer` → **Install Certificate…** → **Current User** → **Place all
   certificates in the following store** → **Browse…** → **Trusted Root Certification Authorities** → **Next** →
   **Finish**, and **Yes** to Windows' warning. Firefox: **Settings → Privacy & Security → Certificates → View
   Certificates… → Authorities → Import…**, choose the file and check **Trust this CA to identify websites**.
4. Restart the browser and open the panel at the same address: no warning. One that still warns uses an address the
   certificate does not list (above).

The installer's certificate cannot issue other certificates and is for a web server's identity only, so trusting it
vouches for the names and addresses it lists and nothing else. To stop trusting it: `certmgr.msc` → **Trusted Root
Certification Authorities → Certificates** → the `localhost` certificate issued by `localhost` → **Delete** (Firefox:
**Authorities**, the same certificate).

### Your server's IP changed

When the PC gets another address (no DHCP reservation, a new router) or another name, the panel's self-signed
certificate no longer lists it: browsers warn again at the new address, and a node that pins the certificate or checks
its names refuses it there. The panel keeps working at `https://localhost:8443` on its PC. `-Update` keeps the
certificate; `-RenewCertificate` makes a new one. On the panel's PC (the all-in-one install, or `-Mode Panel`), in an
elevated Windows PowerShell, in the folder `Install-Pulsar.ps1` is in: the release folder you installed from
([step 2](#2-download-pulsar)), or `C:\Program Files\Pulsar\Setup\payload` if you installed with `PulsarSetup.exe`,
which keeps the script there:

```powershell
cd "C:\Program Files\Pulsar\Setup\payload"   # or the release folder
powershell -ExecutionPolicy Bypass -File .\Install-Pulsar.ps1 -RenewCertificate
```

Add `-HostName` with the names and addresses you gave the install (`-HostName panel.example.com, 203.0.113.7`): the new
certificate lists this PC's names and addresses as they are now, plus those, and the script does not remember what you
gave before. It says which names the old certificate listed that the new one does not (an old address or name of this
PC is expected there). If the old certificate lists a name of its own (a DNS name, a public address) and you give no
`-HostName` at all, it stops before changing anything (exit code `2`) and tells you to repeat it with `-HostName`, or to
give another `-HostName` (even `-HostName localhost`) to leave it out on purpose; with a `-HostName` that leaves one out,
it only warns. It takes `-HostName`, `-Unattended` (the `PULSAR-STEP`, `PULSAR-LOG` and `PULSAR-CERTIFICATE` lines of
[Unattended installs](#unattended-installs)), `-InstallRoot` and `-DataRoot`, and refuses every other parameter (it
cannot be combined with `-Mode`, `-PublicIp`, `-Update` or `-Uninstall`: PowerShell refuses that command line): it never
installs, updates or removes a program, reads no secrets file, and leaves the database, the settings, the firewall and
the accounts as they are. It also renews a certificate before the 5 years it is valid for are over. What it does, in this order:

1. **Checks, before anything changes.** The panel must be an install of this script (its service runs the program in
   `-InstallRoot`, its settings name `panel.pfx` in the panel's data folder, and open with the password they hold), and
   its certificate a self-signed one the installer made (`CN=localhost`, issued by itself, friendly name `Pulsar panel`),
   not one an authority issued or you brought (`-CertificatePath`: renew that where it came from). Otherwise it stops,
   changing nothing: exit code `2` (no panel here, or another `-InstallRoot`), `3` (the certificate's folder holds files
   a stopped renewal left, or a link; or the folder is not what the install makes, see below) or `6` (a certificate it does
   not renew).
2. **Makes the new certificate the way the install does** (the same names, the same checks: not a certificate
   authority, for a web server's identity only, 5 years), with a new key, in memory. It holds the `tls` folder open
   from here to the end: nobody can rename or delete it, or a folder above it, meanwhile, and a folder that is a link,
   or sits behind one, is refused. It warns about each address a DHCP server gave this PC (a
   [reservation](#connecting-from-another-pc) keeps it).
3. **Swaps the files with the old ones kept.** It stops the panel (it is unavailable for a few seconds; game servers
   on the nodes keep running) and checks that no process still runs as its account, `NT SERVICE\PulsarPanel`. That
   account can write the `tls` folder, and holding a folder does not stop whoever can write it from changing it in
   place (an empty folder can be made a mount point, which would send what is done there elsewhere), so, until the
   files are swapped, the account is denied every right on the folder (an extra entry, which its files inherit; the
   folder's other permissions are not touched, and it needs a folder that only SYSTEM, Administrators and that account
   may use, otherwise it stops before changing anything, exit code `3`). It then writes the new files beside the old
   ones under random names, each checked to be in the held folder, with the old files' permissions and the same PFX
   password, so the panel's settings need no change; swaps `panel.pfx` and `panel.cer`, keeping the old files beside
   them; takes the entry away (the folder's permissions are exactly what they were, which is checked, and so are the
   files') and starts the panel, which must answer over HTTPS on its port (and the agents' port, if it has one) with
   the new certificate. A process still running as the panel's account stops it there (exit code `3`: end it, or
   restart Windows, and run it again); the certificate is untouched and the panel is started again.4. **Moves the trust of this PC, only if the install gave it.** The all-in-one install trusts the panel's certificate on
   its PC (for the agent and the browsers there), marked as its own. If the old certificate is trusted that way, the new
   one is trusted in the same way, with the same checks, and then the old one is no longer trusted. If the old one was
   not (a panel-only install), or someone else trusted it (not marked), no certificate store is changed.
5. **Deletes the old files** (they hold the old key) and prints the new certificate's SHA-256 fingerprint
   (`PULSAR-CERTIFICATE`), the addresses to reach the panel by, and what the nodes need (below).

If anything fails after the panel was stopped, or you interrupt the script (Ctrl+C, a closed window) then, the old
certificate is put back: its files, the folder's permissions, the new trust removed, the panel started again and checked
to answer with the old certificate, and the message says what failed (exit code `1`, or `3` for a process left running
as the panel's account; the log has the rest). If even that fails, the message says what is left: where the old files
are (`panel.pfx.old-…` and `panel.cer.old-…` in the `tls` folder: copy them over `panel.pfx` and `panel.cer` and start
the `PulsarPanel` service), and, if the folder's permissions could not be put back, the command that takes the panel's
account's denied entry away (`icacls "C:\ProgramData\Pulsar\Panel\tls" /remove:d "NT SERVICE\PulsarPanel"`). A renewal
that was stopped halfway makes the next one refuse until you delete those files (or copy them back, if the panel does not
start).

Then:

- **Browsers** that trusted the old certificate trust the new one as in
  [Trusting the panel's certificate](#trusting-the-panels-certificate) (compare the fingerprint the script printed);
  delete the old one from their list.
- **Nodes that pinned the old certificate are offline until each is pinned again.** Every node added with a node code
  pins it (`PulsarAgent status` on the node shows `Panel TLS: certificate SHA-256 … (pinned)`), and so does one
  enrolled with `--panel-certificate`: follow [Changing the panel's certificate](#changing-the-panels-certificate)
  with the new certificate's SHA-256 in lowercase, which the script prints in those steps
  (`(Get-FileHash C:\ProgramData\Pulsar\Panel\tls\panel.cer).Hash.ToLower()` shows it again). A node that reached the
  panel by an address it no longer has needs the new address too: make its code in **Add node** opened at the new
  address. Nodes added with `-TrustCertificate` trust a copy of `panel.cer`: copy the new one to each and trust it
  there in place of the old.
- **The node on the panel's own PC** (the all-in-one install) trusts the panel through this PC's Trusted Root store,
  which step 4 updated if the install had trusted the old certificate, so it reconnects on its own. If the old one was
  trusted by someone else (not marked as the installer's), nothing was changed there and the node, like this PC's
  browsers, trusts the panel again once you remove the old certificate and import the new `panel.cer` (`certlm.msc` →
  Trusted Root Certification Authorities); the script says so. If the node was enrolled again with a node code it is
  pinned too: the script says so, and its `agent.json` needs the new SHA-256 as above.
- **Backups** made before the renewal hold the old certificate and its key: restoring one with `-RestoreFrom` brings
  them back, so make a new backup ([backup.md](backup.md)); the old ones keep the old key, so store them as safely.
- **A Let's Encrypt public IP certificate** ([Access from the internet with your public IP](#access-from-the-internet-with-your-public-ip))
  **is not affected**: the panel keeps it in its database and serves it only to connections from the internet that reach
  the public address without naming a host; the installer's certificate is what it serves to the loopback, your local
  network and the agents' port. Nodes that reach the panel by its public IP are not pinned (their code names no
  certificate); one pinned to the panel's own certificate at that address is the same as any other pinned node.

## Advanced and automation: the installer script

The setups run the release's `Install-Pulsar.ps1`, which the release ZIP, published too, also holds: steps 1 to 3
install with it directly, for automation, offline machines, a panel restored from a backup (`-RestoreFrom`), several
host names, and everything else the wizard does not ask.

`-RestoreFrom` copies the backup to a private folder, checks the copy against its `backup.json` and refuses a backup
folder others could change, a backup made by a newer Pulsar than the release, or one with links in it (see
[backup.md](backup.md#on-a-new-machine); `-TrustRestoreSource` is for a drive that keeps no permissions).

## 1. PostgreSQL (panel machine)

Usually nothing to do. When the panel's machine has no PostgreSQL 15 or later, the installer installs the newest
PostgreSQL 18 with EDB's installer (the one postgresql.org links for Windows), unattended:

- the server and its command-line tools only (no pgAdmin, no Stack Builder), in `C:\Program Files\PostgreSQL\18`, as
  the service `postgresql-x64-18` on **port 5432** (`-PostgresPort` chooses another; a port in use stops the install
  before anything changes);
- listening on this machine only (`listen_addresses = 'localhost'`, set with `ALTER SYSTEM`), and the installer checks
  that `pg_hba.conf` lets in nothing but this machine, and every rule asks for a password (a `trust` rule would let any
  account on the machine, a game server's too, in as the database's superuser: the install stops instead);
- its superuser, `postgres`, gets a random password, handed to EDB's installer in a file only SYSTEM and Administrators
  can read (deleted when the installer ends), changed again to another random one as soon as PostgreSQL runs, used to
  create Pulsar's database and user, and then kept nowhere. Nobody needs it for Pulsar; to use PostgreSQL yourself,
  give it a password of yours as [below](#the-postgres-superusers-password).

With a PostgreSQL 15 or later installed already, the installer uses it (the newest, with several): it asks for its
`postgres` password once, to create Pulsar's own database and user, and never stores it. Its programs must be in a
folder only administrators can change (its installer's default, in Program Files): the installer runs its `psql`
as administrator.

Pulsar's own user, `pulsar`, gets a random password of its own, kept in the panel's settings (readable only by
SYSTEM, Administrators and the panel's service account), and a comment that marks it as the installer's. A
`pulsar` user or database that is already there stops the install before anything changes (exit code 2), unless it
is what an install that failed left: the installer's own user, with no database, which it drops and makes again. Any
`pulsar` database (a panel's data, which `-Uninstall` keeps, an empty one, or someone else's) it never drops, nor
opens: [Starting over](#starting-over) says what to do. An install that fails after making the database drops it
itself.

Already have a PostgreSQL elsewhere? Create an empty database and a user that owns it, and give the installer its
connection string with `-ConnectionString "Host=…;Port=5432;Database=…;Username=…;Password=…"` instead, or in the
`PULSAR_CONNECTION_STRING` environment variable of that PowerShell, which keeps the password off the command line.
When this machine has `psql.exe`, the installer asks that database which migrations it has applied, and stops before
changing anything if a newer Pulsar than this release migrated it (the panel refuses to start on such a database). The
`SSL Mode` and `Root Certificate` of the connection string are also used by the programs the installer, the update's
rollback and `Backup-Pulsar.ps1` run against it (`psql`, `pg_dump`, `pg_restore`), not only by the panel.

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

   Both must show the same value. A release built by the project's release workflow on GitHub also carries a
   provenance attestation, which you can check with the GitHub CLI:
   `gh attestation verify .\pulsar-<version>-windows-x64.zip --repo Ayrton09/Pulsar`. A release built on the
   maintainer's PC and published from there has none (the command then says it found no attestation): its SHA-256
   is the check, and the one-click updates also check the manifest's signature.
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
it can before it changes anything (the folder, the passwords, what is installed, PostgreSQL's `pulsar` user and
database), and so the ports and the PFX: the panel's ports (`-Port`, `-AgentsPort`, `-SftpPort`, or a backup's) must
be free on every address, where nothing listens and Windows keeps none of them for itself, and the PFX of
`-CertificatePath` (or a backup's) must open with its password and hold its private key. Ports and PFX are checked
before anything changes. It prints each step as `[n/total] …` and writes a log
([Unattended installs](#unattended-installs)). If a step fails once the `pulsar` database exists, it undoes what it
did to the panel: its service, program and firewall rules, and the database and user it made (the panel's data
folder stays). Then it:

1. downloads and checks what the machine lacks, then installs it: the .NET 10 runtimes and, without a PostgreSQL to
   use, PostgreSQL 18 (step 1; [What the installer downloads](#what-the-installer-downloads));
2. creates the `pulsar` database and user in the local PostgreSQL (with a random password);
3. installs the panel in `C:\Program Files\Pulsar\Panel` as the **PulsarPanel** service, running as its own virtual
   account (`NT SERVICE\PulsarPanel`), with its data in `C:\ProgramData\Pulsar\Panel` (keys, logs, certificate);
4. makes a self-signed HTTPS certificate for this PC's names, `localhost` and this PC's addresses (`127.0.0.1`, `::1`
   and its private IPv4 addresses on networks with a router, as IP addresses:
   [Connecting from another PC](#connecting-from-another-pc)), valid for 5 years, and trusts it on this PC (the agent
   and this PC's browsers check it). It is made in memory (no certificate store holds it or its key) and written to
   the panel's private folder. It is marked as unable to issue other certificates and as good for a web server's
   identity only (server authentication), so trusting it vouches only for the names and addresses it lists, never for
   other sites, code or clients; the installer prints its names, addresses and thumbprint, and at the end its SHA-256
   fingerprint, to compare with what browsers on other PCs are served;
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

To make the certificate valid for a name or address other machines use (a DNS name, a public IP address, the address
of a port forward), add `-HostName panel.example.com` (a name: letters, digits, `.`, `-` and `_`, or a leading `*.` for
a wildcard, not ending in a number; an address: IPv4 as four numbers, `203.0.113.7`, or IPv6, `2001:db8::7`, listed as
an IP address; several are separated by commas). The PC's names and private IPv4 addresses are always in it
([Connecting from another PC](#connecting-from-another-pc)). To use a real certificate instead of a self-signed one, pass it with
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
  the panel machine's own, which the installer's certificate always lists, but not a number such as `3232235777`, which
  some programs read as an IPv4 address), or addresses other than that host and the loopback ones (`127.x`, `::1`):
  another site's name, a wildcard, any other address, private ones included (`10.x`, `172.16.x` to `172.31.x`,
  `192.168.x`: trusted, the certificate would vouch for your router or NAS too). The installer's certificate lists the
  panel PC's private addresses, so a node whose `-PanelUrl` uses the one it has on that network takes it as it is when
  the PC has no other; a node that uses the PC's name, or one name of a panel known by several (its machine's full name
  in a domain, a `-HostName`), meets the others here: use the address in `-PanelUrl`, or add
  `-TrustCertificateWithOtherNames` if every name it lists is yours;
- one that may be used for more than a web server's identity: an Extended Key Usage beyond server authentication, or
  none at all (which allows every use, code signing included). Installers before this check also listed client
  authentication; add `-TrustCertificateWithOtherUses` if you know it is the panel's own.

A name with characters a DNS name cannot have (a line break, an escape, a space) is refused outright. A certificate
valid for longer than 5 years (and a day: the installer's own is valid for 5) is trusted with a warning, since a leaked
key would vouch for its names until it expires: make a new one like the installer's when you can. The node trusts
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
SHA-256 … (pinned)`). Replacing the panel's certificate (a new self-signed one, for a new address say:
[Your server's IP changed](#your-servers-ip-changed), where `Install-Pulsar.ps1 -RenewCertificate` does it and prints
these steps; a renewal; one from an authority in its place) leaves those nodes offline until each is pinned again: the
agent's log says the TLS handshake failed (`RemoteCertificateValidationCallback`). For each, either

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

### Access from the internet with your public IP

With a **static** public IP address (one your internet provider gives you for good; ask them if unsure: a dynamic one
changes, and the certificate with it), the panel gets a free certificate for that address from **Let's Encrypt**, the
non-profit certificate authority every browser trusts. A friend then opens `https://<your public IP>:8443` in any
browser and gets the panel, with no warning and nothing to install.

1. Turn it on, when you install or later:
   - in `PulsarSetup.exe`: **Show advanced settings**, then the page **Advanced: access from the internet**: your
     public IP;
   - with the script: `.\Install-Pulsar.ps1 -PublicIp 203.0.113.10` (with `-Mode AllInOne` or `-Mode Panel`; with
     `-RestoreFrom` it only opens port 80, since the setting comes with the backup's database);
   - later, in the panel: **Nodes → Public IP certificate (Let's Encrypt)**: tick **Use a Let's Encrypt certificate for
     my public IP**, type the address and **Save**. The installer opens port 80 in Windows Firewall only for
     `-PublicIp`, so open it once yourself, in an elevated PowerShell (`-Profile Any` if you installed with
     `-FirewallPublic`; `-Uninstall` removes this rule with the others):

     ```powershell
     New-NetFirewallRule -Name 'Pulsar-Panel-ACME' -DisplayName "Pulsar Panel (Let's Encrypt check)" -Direction Inbound -Protocol TCP -LocalPort 80 -Program "$env:ProgramFiles\Pulsar\Panel\Pulsar.Api.exe" -Action Allow -Profile Domain,Private
     ```

   Turning it on accepts Let's Encrypt's [Subscriber Agreement](https://letsencrypt.org/repository/). The email is
   optional (Let's Encrypt may write about the account; it sends no expiry reminders).
2. On your router, forward **TCP 80** and **TCP 8443** (the panel's port) to this PC, and **TCP 2022** if friends use
   SFTP (and the game servers' ports for players). Port 80 is where Let's Encrypt checks that the address is yours (its
   *http-01* check): the panel answers that check there and nothing else, and listens there only while this is on. The
   firewall rules are for Domain and Private networks: if Windows treats your home network as Public, set it to Private
   (**Settings → Network & internet → Properties**) or install with `-FirewallPublic`.
3. The panel asks for the certificate at once. The section shows **Certificate in use**, until when it is valid, the last
   renewal, the next attempt and, when one failed, what Let's Encrypt said.

What to know:

- An IP address certificate lasts 160 hours, a little over 6 days (Let's Encrypt's `shortlived` profile, the only one
  for IP addresses). The panel renews it at half its lifetime (about every 3 and a half days), or earlier when Let's
  Encrypt asks (ARI), without a restart; **Renew now** asks at once.
- It is served only to connections from the internet to that address. On your local network, and by a name
  (`https://localhost:8443`, `https://my-pc:8443`), the panel keeps its own certificate, as before. At home, open the
  panel by its local address: a browser at home that opens the public IP usually comes back through the router (hairpin
  NAT) as a local one and gets the panel's own certificate, with its warning.
- When a renewal fails (port 80 closed or not forwarded, another public IP, Let's Encrypt unreachable), the panel tries
  again after 15 minutes, then less and less often, up to once a day, and never more than 3 times an hour (**Renew now**
  included), so Let's Encrypt never blocks the address. Add the event **Public IP certificate** to a channel in
  **Admin → Notifications** to be told when it fails and when the certificate is about to expire. If no new one comes,
  once the current one expires visitors from the internet see a security warning again; nothing else stops working.
- **Let's Encrypt environment → Staging** tries the whole flow against Let's Encrypt's test environment: browsers do
  not trust its certificates. Switch back to **Production** for the real one. In production the panel keeps a certificate
  only if it leads to a root this PC trusts (ISRG Root X1; Windows installs it by itself when it first opens a site that
  uses it): the section shows the error otherwise.
- Nodes on your local network, or that reach the panel by a name, are not affected. A node that reaches the panel by
  the public IP from the internet sees the Let's Encrypt certificate there: add it with a node code made while you
  reach the panel by that public IP (the code then names no certificate, and the node checks Let's Encrypt's as any
  website's: its Windows needs Let's Encrypt's root, ISRG Root X1, which it has once it opened a site that uses Let's
  Encrypt). A node pinned to the panel's own certificate at the public IP refuses the Let's Encrypt one (it fails
  closed): enroll it again that way, or let it use an agents' port it already moved to, which keeps the panel's own
  certificate ([Changing the panel's certificate](#changing-the-panels-certificate)).
- Port 80 taken by another program (IIS, another web server) keeps the check from working: the section says so, and
  `-PublicIp` refuses to install until that program is stopped. The port can be changed with
  `"PublicIpCertificate": { "ChallengePort": 8080 }` in the panel's `appsettings.Production.json` only when the router
  forwards its own port 80 to that port: Let's Encrypt always connects to port 80.

### Behind a reverse proxy

The panel serves HTTPS itself and needs no proxy. If one is put in front of it anyway (nginx, Caddy, IIS), name the
proxy's address in the panel's `appsettings.Production.json`, including when it runs on the panel's own machine:

```json
"Security": { "TrustedProxies": [ "127.0.0.1", "::1" ] }
```

The panel reads `X-Forwarded-For` and `X-Forwarded-Proto` only from the addresses listed there. Without it, every
browser looks like the proxy's address: they share one sign-in rate limit (a few wrong passwords lock everyone out for a
minute), the audit log names the proxy, and a proxy that decrypts TLS makes the panel refuse the requests as plain HTTP.
Nodes and SFTP clients connect to the panel directly, not through the proxy.

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
| The .NET runtime and, for the panel, the ASP.NET Core runtime: the newest 10.0 patch, at least the one the release was built with (its `release.json`) | Microsoft's release metadata (`https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json`) and the Windows x64 installers it lists, from Microsoft's download hosts only | The SHA-512 the metadata lists; the Authenticode signature: valid (Windows chains it to a root it trusts), in the file itself, by a certificate for code signing whose subject is `O=Microsoft Corporation, L=Redmond, S=Washington, C=US` and whose chain ends at Microsoft Root Certificate Authority 2011 (or 2010), none of it revoked; the version information (`Microsoft .NET Runtime - 10.0.<n> (x64)`, version 10.0.<n>) and the version |
| PostgreSQL: the newest release of major version 18 (never a newer major version on its own: that needs a data upgrade), never older than the release's floor (`postgresMinimum` in its `release.json`), and EDB's newest build of it | postgresql.org's list of versions (`https://www.postgresql.org/versions.json`); EDB's installer, `https://get.enterprisedb.com/postgresql/postgresql-18.<minor>-<build>-windows-x64.exe` | The Authenticode signature: valid, in the file itself, by a certificate for code signing whose subject is `O=EnterpriseDB Corporation, L=Wilmington, S=Delaware, C=US`, issued by `DigiCert Trusted G4 Code Signing RSA4096 SHA384 2021 CA1` (that very certificate, thumbprint `7B0F360B775F76C94A12CA48445AA2D2A875701C`), whose chain ends at DigiCert Trusted Root G4 (`DDFB16CD4931C973A2037D3FC83A4D7D775D05E4`), none of it revoked; the version information (a `PostgreSQL` product of version 18.<minor>) |

Every download also has a time limit: 10 minutes, and a second more for each 128 KiB of its size.

Revocation is checked by the installer itself, online, for each certificate of the chain but its root (Windows
PowerShell's `Get-AuthenticodeSignature` does not): a revoked one is refused (exit code 5). When it cannot be checked
(no internet, an offline install), the installer says so in a warning, in its log too, and goes on with the other checks.

The signers' identities are those of EDB's signed PostgreSQL 18 programs and Microsoft's .NET 10 installers when the
release was made. Before a release, its Sandbox runs (`scripts/sandbox/install`, and `scripts/sandbox/setup` with
`-Networking`, which downloads them as an easy install does) must confirm them on the real downloads: EDB's installer's signer, issuer and chain (`Get-AuthenticodeSignature` on it, its
`SignerCertificate.Issuer`, and the thumbprints of the chain Windows builds for it) and its version information (`(Get-Item <installer>).VersionInfo`: a `ProductName` with `PostgreSQL` in it and a
`ProductVersion` or `FileVersion` starting with `18.<minor>`, or a `ProductVersion` of exactly `18`, which is what
EDB's installers of PostgreSQL 18 say; the server one installs must then report `18.<minor>` before anything uses it,
or the install fails and says to remove that PostgreSQL). When a publisher changes them (a new certificate
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
  version, which its version information must confirm, or name its major version, and then the server it installs;
  of 18, at least the release's floor), signed by EnterpriseDB.

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
- `PULSAR-CERTIFICATE <sha256>`, at the end of a panel's install, and of `-RenewCertificate`: the lowercase
  hexadecimal SHA-256 of its HTTPS certificate's DER, the fingerprint browsers show (the setup shows it on its last
  page); after `-RenewCertificate` it is the new certificate's, and the one nodes pin;
- `PULSAR-ERROR <code> <message>`, on one line, when it fails.

It ends with one of these exit codes (without `-Unattended`, a failure is a PowerShell error, exit code 1 under
`powershell -File`):

| Code | Meaning |
|---|---|
| `0` | Done. |
| `1` | It failed; the message, and the log, say what failed and what was left (with `-RenewCertificate`, whether the old certificate was put back). |
| `2` | Wrong or missing parameters, secrets or node code, a PFX its password does not open, or what it would install is installed already (`-Update` updates it; a `pulsar` database in PostgreSQL, or a `pulsar` user that is not the leftover of a failed install, [Starting over](#starting-over)); with `-RenewCertificate`, no panel installed here (or in another folder than `-InstallRoot`'s), a parameter it does not take, or a name of the old certificate's that `-HostName` neither keeps nor drops on purpose. Nothing was changed. |
| `3` | This machine, the installer's folder or the account cannot install Pulsar as they are: not elevated, a 32-bit PowerShell, a folder others can change, Windows updates missing, an agent from the MSI, a port the panel would listen on taken, a PostgreSQL port, service or folder taken; with `-RenewCertificate`, the panel's certificate folder holds files a stopped renewal left, or a link. Nothing was changed (but for the .NET runtimes, when the Windows updates check fails after they were installed). |
| `4` | A download, or the panel at a node code's address, could not be reached. Nothing was changed. |
| `5` | A download or a given installer failed its checks (signature, SHA-512, version), or Microsoft's or postgresql.org's metadata was not as expected. Nothing was changed. |
| `6` | The certificate the panel presents is not the node code's (another machine, or a proxy serving its own), or may not be trusted or pinned (it can issue certificates, does not name the panel's address...); with `-RenewCertificate`, the panel's certificate is not a self-signed one the installer made (an authority's, or your own). Nothing was changed. |
| `7` | `-Update` replaced programs, the updated Pulsar did not work (the panel not ready, the agent not signing in at its panel), and the update was rolled back: the previous version was put back and works, so Pulsar is as it was, and the message says why ([update.md](update.md#backup-checks-and-rollback)). A rollback that fails ends with `1`, saying what state things are in. |
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
  `-Uninstall`, or from **Apps** when a setup installed it (it keeps the database and data). A panel's install that
  failed once its database existed has undone itself (the message says so, or names what it could not undo); when
  that run had also installed PostgreSQL 18, whose `postgres` password it kept nowhere, remove PostgreSQL 18 from Apps
  and delete `C:\Program Files\PostgreSQL\18` (it holds nothing of Pulsar's) before running it again, or give
  `postgres` a password of yours ([The postgres superuser's password](#the-postgres-superusers-password)) and give it
  to the installer.
- **"The panel would listen on port … which is taken"**: another program listens on that port, or Windows keeps it
  (Hyper-V and WinNAT reserve ranges: `netsh interface ipv4 show excludedportrange protocol=tcp`). Stop that program,
  or choose another port (`-Port`, `-AgentsPort`, `-SftpPort`, or the setup's advanced settings). Nothing was changed.
- **"… cannot be the panel's certificate"**: the PFX does not open with the password given, or holds no private key.
  Give its password (`PULSAR_CERTIFICATE_PASSWORD`, or the setup's field), or export it again with its key. Nothing
  was changed.
- **"PostgreSQL already has a 'pulsar' role" / "… a 'pulsar' database"**: see [Starting over](#starting-over).
  Nothing was changed.
- **"could not be downloaded" / "could not be reached"**: the machine has no internet, or a proxy or firewall stops
  HTTPS to Microsoft's, postgresql.org's or EDB's servers. Nothing was installed; see
  [Offline installs](#offline-installs).
- **"is not validly signed" / "is signed by …, not by …" / "does not match the SHA-512"**: a download (or an installer
  given offline) is not what its publisher made, or Windows cannot check its signature (on a machine without internet,
  Windows may lack the root certificate the signature chains to; open any HTTPS site once, or import the root). It was
  not run and nothing was changed: download it again, from the publisher's site. **"… with a certificate that is
  revoked"**: its publisher withdrew the certificate that signed it; do not run it, and get a newer one from them.
  **"Whether the certificate that signed … has been revoked could not be checked"** is a warning: the authority could
  not be asked (no internet), and the installer went on with its other checks.
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
  `Kestrel:Endpoints:Https:Certificate` in the panel's `appsettings.Production.json` and restart the service). If
  it warns again at an address that worked before, the PC's address probably changed: [Your server's IP
  changed](#your-servers-ip-changed).

### Starting over

The panel's database stays in PostgreSQL when the panel is removed (`-Uninstall`, or **Apps**), and its `pulsar`
user's password goes with the panel's settings: an install over it stops before anything changes (exit code 2). The
installer drops by itself only what an install that failed left: its own `pulsar` user (marked as its own) with no
database (an install that fails after making the database drops it before it ends). Any `pulsar` database stays until
you decide, even an empty one, and the installer does not look into it: whoever has the panel's database password
owns that database, and its owner chooses what a session in it runs with (its `search_path`, and functions of its
own), so a superuser connecting to it, as the installer would, could be made to run them. An install stopped half way
(the machine lost power, say) can leave an empty one: drop it as below. For the same reason, open or dump that
database as `pulsar`, never as `postgres`: the commands below connect as `postgres` to the `postgres` database only.

- **To keep the data**: install the panel from a backup made by `Backup-Pulsar.ps1` ([backup.md](backup.md)) with
  `-Mode Panel -RestoreFrom`, after dropping the database and user as below (the backup brings its data back). If you
  have no backup and need the data, dump it first as `pulsar` (with `$bin` below; the first command gives `pulsar` a
  new password, asked for twice, since the old one went with the panel's settings, and the second asks for it):

  ```powershell
  & "$bin\psql.exe" -h localhost -U postgres -d postgres -c "\password pulsar"
  & "$bin\pg_dump.exe" -h localhost -U pulsar -Fc -f D:\pulsar-before-reinstall.dump pulsar
  ```
- **To start from nothing**: drop them, in an elevated PowerShell on the panel's machine (adjust the PostgreSQL
  version in the path; psql asks for the `postgres` password, which, when the installer installed PostgreSQL, you set
  first as in [The postgres superuser's password](#the-postgres-superusers-password)):

  ```powershell
  $bin = 'C:\Program Files\PostgreSQL\18\bin'
  & "$bin\psql.exe" -h localhost -U postgres -d postgres -c "DROP DATABASE IF EXISTS pulsar WITH (FORCE)" -c "DROP ROLE IF EXISTS pulsar"
  ```

  Then run the installer again. With the database gone, `C:\ProgramData\Pulsar\Panel` (the old panel's keys and logs)
  protects nothing: delete it too before installing again (a new panel makes its own keys, and `-RestoreFrom` brings
  the backup's). The installer makes `C:\ProgramData\Pulsar\Panel` and `C:\ProgramData\Pulsar\Updates` with their
  permissions in the call that creates them (SYSTEM and Administrators, and the panel's service account in `Panel`;
  nothing inherited from `C:\ProgramData`, where any user may add folders), and checks the ones that are there before it
  changes anything: owned by SYSTEM or Administrators, not a link, nobody else allowed in. One that is not (a local user
  made it first, say) stops the install with exit code 3 and the names it found: move out of it what you need, delete the
  folder and run the installer again. `C:\ProgramData\Pulsar` itself, if only its permissions are open (an earlier
  install left it inheriting them), the installer closes by itself.
- **Or** give the panel another database with `-ConnectionString` (step 1).

## Uninstalling

Installed with a setup: **Settings → Apps → Pulsar → Uninstall** (silently: the `UninstallString` of its entry, with
`/VERYSILENT /SUPPRESSMSGBOXES`). It runs the copy of `Install-Pulsar.ps1 -Uninstall` the setup kept in
`C:\Program Files\Pulsar\Setup\payload`, as below; when that fails, it says why and removes nothing more, keeping its
entry to try again. There is no option to delete the data: it is kept, as below. Otherwise, from the release folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-Pulsar.ps1 -Uninstall
```

It removes the services, the programs (`Backup-Pulsar.ps1` in the panel's folder too; it tries again for ten seconds
or so when a file is held open, deletes the panel's `appsettings.Production.json`, which holds the database and
certificate passwords, first, and, if a folder still could not be removed, says which and ends with exit code `1`
instead of reporting success: close what holds it and delete it yourself), the firewall rules
(`Pulsar-Panel-HTTPS`, `Pulsar-Panel-Agents` for the agents' port, `Pulsar-Panel-SFTP` for SFTP and `Pulsar-Panel-ACME`
for Let's Encrypt's check) and the trust the script gave the panel's
certificate (in all-in-one mode, or with `-TrustCertificate`; a certificate this machine trusted already stays
trusted). It removes only certificates it marked as its own when it trusted them: one that an install made before
that mark only noted in the panel's data folder is named in a warning instead, for you to remove in `certlm.msc`
(*Trusted Root Certification Authorities*) if it is the panel's (the panel's service account can change that note, so
the script does not act on it). It keeps the
database, `C:\ProgramData\Pulsar` and the game servers' folders, and PostgreSQL and the .NET runtimes, which other
programs may use: remove them from **Apps** when nothing else needs them, after Pulsar (the agent's program needs the
.NET runtime to unenroll; PostgreSQL's data folder stays until you delete it). Delete game servers from the panel
first: that also removes their Windows accounts.

What stays in `C:\ProgramData\Pulsar` is sensitive, and only administrators can open it: the panel's keys (needed to
read the database's secrets), its HTTPS certificate's PFX file and the password in the settings, the logs, and
`Updates\backups`, the update backups, each of which is a full database dump with the keys and the settings
(`appsettings.Production.json`, with the database and certificate passwords). Delete `C:\ProgramData\Pulsar` yourself
(and the database, if you keep none of it) when you no longer need any of that; the uninstaller leaves it because the
keys are the only way to read the database's secrets again. The programs' own folders keep nothing: the panel's
settings file is deleted first, and a folder that still cannot be removed is reported with exit code `1`. The setups'
texts say the same, in English and Spanish.

If the .NET runtime the agent needs is gone already, `-Uninstall` cannot run the agent's program: it says so in a
warning, stops and removes the agent's service (`sc.exe`), and keeps the program in `C:\Program Files\Pulsar\Agent`
with the node key, the configuration and the game servers' Windows accounts; its last line says Pulsar is removed
"but for the agent's program and data". A service still running is asked to stop its game servers as it stops, as
`unenroll` does. One that was not running cannot be asked, and the script does not end its game servers itself (only
the agent's program can tell its host processes from others): they keep running, each in a host process, a
`PulsarAgent.exe` run from `C:\ProgramData\Pulsar\Agent\host-programs`. End them in Task Manager (**Details**, *End
process tree*: their games stop without saving), or let `unenroll` do it below. Install the .NET 10 runtime again,
run `& 'C:\Program Files\Pulsar\Agent\PulsarAgent.exe' unenroll` in an elevated PowerShell, and delete that folder.
Until then the node key keeps working: delete the node in the panel (it stays there, offline, until you do). An agent installed with the MSI is not the script's to remove: it says so and removes
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
sc.exe stop PulsarAgent  # needs no .NET runtime; its game servers keep running in their own processes
sc.exe delete PulsarAgent
Remove-Item C:\ProgramData\Pulsar\Agent\config\agent.json
```

While the .NET runtime is there, run `& 'C:\Program Files\Pulsar\Agent\PulsarAgent.exe' service stop --stop-servers`
before them: it also stops the game servers gracefully. Otherwise stop the games yourself (Task Manager), and delete
their Windows accounts (`pulsar_<id>`, Computer Management, Local Users and Groups). With the service and the
configuration gone, `unenroll` finds nothing to remove and succeeds. The node key stays in the
key store, and the node stays in the panel until you delete it there.
