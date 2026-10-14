# Installing Pulsar

Pulsar has two parts:

- **The panel**: the web interface and API. It keeps its data in PostgreSQL. One per installation.
- **The agent**: runs on every Windows machine that hosts game servers (a *node*) and connects to the panel.

Both can run on the same PC (the usual setup for a home server), or the panel can run on one machine and nodes on
others. Pulsar's installer does either; this guide covers both.

## Requirements

- Windows 11 or Windows Server 2022/2025, 64-bit, with an administrator account. (Windows 10 22H2 works on a
  best-effort basis.)
- Windows updates installed. Pulsar's programs (.NET 10) do not start on a Windows that lacks them: Windows Server 2022
  as installed from its original image stops them with "Your Windows doesn't fully support CET". Run Windows Update
  (Settings > Windows Update, or `sconfig` on Server Core) and restart before installing; the installer checks this
  first and changes nothing if it fails.
- For the panel: PostgreSQL 15 or later (step 1), about 500 MB of disk and 1 GB of RAM.
- For each node: the disk space of its games (Counter-Strike 2 needs about 85 GB) and their memory. Nodes connect to
  the panel; they open no port of their own besides their game servers' ports.
- Internet access on the nodes (games are downloaded from Steam with SteamCMD).

## 1. PostgreSQL (panel machine)

1. Download the PostgreSQL installer for Windows from <https://www.postgresql.org/download/windows/> (the
   "Download the installer" link, by EDB). Any version from 15 on; the newest is best.
2. Run it and keep the defaults: install directory, data directory, **port 5432**. Stack Builder is not needed.
3. When it asks for the password of the database superuser (`postgres`), choose one and keep it: Pulsar's installer
   asks for it once, to create Pulsar's own database and user. Pulsar never stores it. With several PostgreSQL versions
   installed, Pulsar's installer uses the newest.

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

It asks for the PostgreSQL `postgres` password (step 1) and for a password for Pulsar's first administrator, `admin`
(at least 12 characters; `-AdminUserName` chooses another name). Then it:

1. creates the `pulsar` database and user in the local PostgreSQL (with a random password);
2. installs the panel in `C:\Program Files\Pulsar\Panel` as the **PulsarPanel** service, running as its own virtual
   account (`NT SERVICE\PulsarPanel`), with its data in `C:\ProgramData\Pulsar\Panel` (keys, logs, certificate);
3. makes a self-signed HTTPS certificate for this PC's names and `localhost`, valid for 5 years, and trusts it on this
   PC (the agent and this PC's browsers check it). It is marked as unable to issue other certificates and as good for
   a web server's identity only (server authentication), so trusting it vouches only for the names it lists, never
   for other sites, code or clients; the installer prints its thumbprint and names;
4. opens the panel's port (**8443**, `-Port` chooses another) in Windows Firewall, on Domain and Private networks
   (`-FirewallPublic` also opens it on networks Windows treats as public; `-NoFirewall` leaves the firewall alone);
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
`-HostName panel.example.com` (letters, digits, `.`, `-` and `_`, or a leading `*.` for a wildcard; several names are
separated by commas). To use a real certificate instead of a self-signed one, pass it with
`-CertificatePath C:\certs\panel.pfx -CertificatePassword (Read-Host -AsSecureString)`. Agents check the panel's
certificate without downloading anything (so whoever answers at the panel's address cannot make them fetch addresses
of its choosing): the PFX must contain the intermediate certificates (the full chain), and each node must already trust
the authority's root in **Local Computer → Trusted Root Certification Authorities**. On a fresh Windows a public
authority's root may be missing until something validates a site with it: open the panel's address once in Edge on the
node, or import the root. The agent's log then says `PartialChain` (an intermediate is missing) or `UntrustedRoot`
(the root is).

Then, for each node: in the panel, **Nodes → Add node**, give it a name and copy the command it shows. On the node,
extract the same ZIP into a folder only administrators can change (step 2), open PowerShell as administrator in it and
run that command. It looks like:

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
  (8 by default), three of them for one user, and answers others "too many file transfers on this node" right away.
  Revoking a node's key, disabling or deleting the node closes its connections at once.

  An agent's sign-in holds a connection for a second or two. Nodes behind one NAT address share its budgets: 20 lets
  them sign in about ten at a time and the others retry within seconds. Raise the per-address and per-network budgets
  (up to `MaxPendingConnections`) for a site with hundreds of nodes behind one address that restart together. The
  panel refuses to start with a budget below 1 or out of order (per address ≤ per network ≤ pending ≤
  `MaxConnections`, and per node ≤ `MaxConnections`).
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
  when the panel runs on the node, or its public name leads back to the node through the router, a game server
  reaches it there too. The agent runs as SYSTEM and is not affected. With no game server yet there is nothing to
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

Back up the panel as described in [backup.md](backup.md): without its keys, the database's secrets cannot be read.

## Troubleshooting

- **The installer stops with an error**: it says what failed. Run it again after fixing it; if the panel or the agent
  was already installed, remove it first with `-Uninstall` (it keeps the database and data).
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

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-Pulsar.ps1 -Uninstall
```

It removes the services, the programs (`Backup-Pulsar.ps1` in the panel's folder too), the firewall rules
(`Pulsar-Panel-HTTPS`, and `Pulsar-Panel-Agents` for the agents' port) and the trust the script gave the panel's
certificate (in all-in-one mode, or with `-TrustCertificate`; a certificate this machine trusted already stays
trusted). It removes only certificates it marked as its own when it trusted them: one that an install made before
that mark only noted in the panel's data folder is named in a warning instead, for you to remove in `certlm.msc`
(*Trusted Root Certification Authorities*) if it is the panel's (the panel's service account can change that note, so
the script does not act on it). It keeps the
database, `C:\ProgramData\Pulsar` and the game servers' folders. Delete game servers from the panel first: that also
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
