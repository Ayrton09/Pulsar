# Updating Pulsar

Updates replace Pulsar's programs and keep everything else: settings, database, keys, nodes and game servers.

## Before updating

1. Read the release notes of every version between yours and the new one (the project's Releases page).
2. Back up the panel ([backup.md](backup.md)) to a place of your own, away from this machine: an update backs the panel
   up itself first (below) and puts the previous version back if the new one does not work, but those copies are on
   the same disk as the panel.

## Order

Update the **panel first, then the nodes** (from the panel, its page does it in that order). A panel accepts agents of its own version and of the previous one, so nodes
keep working while you get to each of them; an agent newer than its panel is refused until the panel is updated.

## From the panel

With nothing to download or carry (Phase 19): **Administration → Updates**, for users with *Manage settings*.

- **What is available.** The panel asks the project's GitHub releases for the newest one once a day (and when you click
  **Check now**), and offers it only when its `pulsar-update.json` is signed by an update key this version of Pulsar
  trusts: a release changed after it was signed, or signed by any other key, is refused, and the page says why. The
  page shows the panel's version, the new one with its notes, and every node with its version. While a newer release is
  available, a banner on every page says so.
- **Privacy.** Each daily look is two small requests to GitHub, which sees the panel's address. **Check for new versions
  once a day** turns that off; the panel then asks only when you click **Check now**.
- **Updating.** **Update the panel** first: it updates the panel's machine (the panel and its agent). Then **Update** on
  each node, or **Update all**. Each asks for your current password and, with two-factor authentication on, the code
  from your app (an API key cannot start an update). The panel is away for about a minute while it restarts; game
  servers keep running throughout, and players stay connected.
- **What happens on each machine.** The panel tells the node's agent only which release to install. The agent downloads
  it from GitHub itself, checks its signature against the keys it trusts, that it is newer than what runs there, and the
  ZIP's SHA-256, unpacks it in `C:\ProgramData\Pulsar\Updates` and runs its `Install-Pulsar.ps1 -Update`, with the backup,
  checks and rollback [below](#backup-checks-and-rollback). The node's row shows each step (*Downloading*, *Installing*)
  and how it ended: *Updated*, *Put back* (the new version did not start, so the previous one runs again, with its data
  as it was) or *Failed*, with why.
- **Who cannot.** A node whose agent the agent MSI maintains, or a development agent, says so and is updated as before
  (below). A node too old for the release (its notes say from which version it updates) is updated with the setups.
- **Repository.** Installations download releases anonymously, so this works once the project's repository is public.

## With the setups

On the panel's machine, download and check the new `PulsarSetup-<version>.exe` as for an install
([install.md](install.md#easy-install)) and run it. It finds what this machine has (the PulsarPanel and PulsarAgent
services), shows the license and what it will update, and runs `Install-Pulsar.ps1 -Update -Unattended` (below) from
`C:\Program Files\Pulsar\Setup\payload`, with the same steps, checks and log. It asks for nothing else: settings,
passwords, nodes and game servers stay as they are. On each node, run the new `PulsarAgentSetup-<version>.exe` (from
the Releases page, or the updated panel's `https://<panel>/downloads/PulsarAgentSetup.exe`), or `PulsarSetup.exe`
where the panel runs too. A setup older than what is installed refuses to run, since the database may have changes an
older panel cannot read: it compares its version with the one a setup recorded and with the installed programs' own
(`Pulsar.Api.exe`, `PulsarAgent.exe`), whoever installed them, and `Install-Pulsar.ps1 -Update` refuses a release
older than those programs too (exit code 2, nothing changed). Silently: `PulsarSetup-<version>.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART` (no secret is
needed), with the exit codes of [silent installs](install.md#silent-installs-with-the-setups). When the update fails
before it changes anything, the setup says why and leaves Pulsar as it was; otherwise see
[If something goes wrong](#if-something-goes-wrong).

An agent installed with the agent MSI is updated with its MSI (below): `PulsarAgentSetup.exe` refuses that machine,
and `PulsarSetup.exe` updates only its panel.

## Updating

On each machine, download and check the new release ZIP as in [install.md](install.md) (step 2), and extract it, in
Windows PowerShell as administrator, into a new folder in `C:\Program Files`, as step 2 shows:

```powershell
New-Item -ItemType Directory "$env:ProgramFiles\PulsarSetup-<version>"
Unblock-File .\pulsar-<version>-windows-x64.zip
Expand-Archive .\pulsar-<version>-windows-x64.zip "$env:ProgramFiles\PulsarSetup-<version>"
```

The update runs as administrator and copies the new programs into `C:\Program Files\Pulsar`, where they run as
services: from a folder other accounts could change (a game server's account among them), whoever changed a program
there would take over the machine. A folder made in `C:\Program Files` is administrators' from the moment it exists;
one made directly under `C:\` and restricted afterwards is open to every account until then (install.md, step 2, says
why that is not enough). The installer refuses a folder others could change and changes nothing, but that check is
part of the installer: it cannot stop someone who already changed it. Then, in that PowerShell, go to the new folder
and run:

```powershell
cd "$env:ProgramFiles\PulsarSetup-<version>"
powershell -ExecutionPolicy Bypass -File .\Install-Pulsar.ps1 -Update
```

It updates whatever this machine has, the panel, the agent or both. First it makes sure the machine has the .NET
runtimes the new release needs, at least the patch it was built with: a release's programs run on the shared .NET
runtime, so a newer release may need a newer patch than Microsoft Update installed so far. What is missing is
downloaded and checked as on a first install ([install.md](install.md#what-the-installer-downloads); offline,
`-DotNetInstallers`), before anything is replaced. Then:

- **Panel**: the PulsarPanel service stops, its programs are replaced (the copy of `Backup-Pulsar.ps1` in its folder
  too, which a nightly backup task runs) and it starts again, applying any database changes at start. Browsers
  reconnect on their own; signed-in users stay signed in.
- **Agent**: the PulsarAgent service stops, its programs are replaced and it starts again. **The node's game servers
  keep running**: each runs in a host process of its own, which the agent takes over again when it starts, and players
  stay connected, and their consoles continue where they were. (With `Pulsar:ServerHosts` set to `false` in the agent's
  settings, the servers stop with the service and start again with it instead.)

An agent installed with the MSI is updated with the newer package, silently and without the panel's values:
`msiexec /i pulsar-agent-<version>-windows-x64.msi /qn`. It keeps the enrollment and leaves the games running, as above.
Intune and group policies can deploy the new package the same way. An older package does not replace a newer one
("A newer version of the Pulsar Agent is installed"). `Install-Pulsar.ps1 -Update` leaves such an agent alone (it
updates only the panel, if the machine has one): the MSI and the script each maintain only the agents they installed.

The settings files (`appsettings.Production.json`) are never replaced, and neither is the panel's `downloads` folder
(the agent's setup it offers new nodes, [install.md](install.md#the-agents-setup)). Everything else in the programs'
folders is replaced, not added to: releases before the easy install (Phase 18) carried their own copy of the .NET
runtime there, which the new programs would otherwise load instead of the shared one that Microsoft Update patches.
`-Update -Unattended` updates without asking, with the progress lines, log and exit codes of
[unattended installs](install.md#unattended-installs).

A nightly backup task that runs `Backup-Pulsar.ps1` from an extracted release folder (`C:\PulsarSetup`, as this guide
once said) should run the copy in `C:\Program Files\Pulsar\Panel` instead, and the old folder be deleted
([backup.md](backup.md)): it runs as SYSTEM, and the old folder let any account change it. `-Update` warns about every
scheduled task that runs a `Backup-Pulsar.ps1` other than that copy. Delete old setup folders made directly under
`C:\` too: anyone could have changed what is in them.

## Backup, checks and rollback

Every `Install-Pulsar.ps1 -Update` (the setups' too, and the panel's one-click update, which is described in
[One-click updates](#one-click-updates)) protects itself, in this order:

1. **Before it replaces anything**, with the panel on this machine, it first checks that the panel's database user can
   set the schema `public` aside (a rollback needs it; otherwise the update stops, saying how to fix it, before changing
   anything), then **stops the panel** and backs it up with the release's own `Backup-Pulsar.ps1` (the database, the keys
   and certificates, the settings) into `C:\ProgramData\Pulsar\Updates\backups\<version>-<yyyyMMdd-HHmmss>`, where
   `<version>` is the one being replaced. The panel is stopped so that nothing it writes after the dump is lost by a
   rollback: the panel is unavailable from the backup until the update ends (a few minutes), and if the update is
   refused before anything is replaced the panel is started again. The newest three of these backups are kept; older ones are
   deleted by the next update. It also notes the database migrations already applied (`__EFMigrationsHistory`) and
   copies the programs it is about to replace to `C:\ProgramData\Pulsar\Updates\previous\Panel` and `\Agent`. That copy
   is built beside the old one and swapped in, so the last copy is never half deleted; and when the installed Pulsar
   does not pass its checks (a stopped service, a panel that does not answer) it does **not** replace a copy of the same version that is
   there (a copy of another version is replaced: programs older than the database are no rollback, and the warning says so; `previous\previous.json` records which version the copy is of), so running `-Update` again on a broken Pulsar does not overwrite the last good version with the broken one: a
   failed update then goes back to that good copy. If the backup fails (for example, a panel whose database is on
   another machine needs PostgreSQL's `pg_dump` on this one), the update does not start and nothing is changed. Last,
   it writes `C:\ProgramData\Pulsar\Updates\in-progress.json` (see [A power cut during an update](#a-power-cut-during-an-update)).
2. It replaces the agent and the panel as described above. The settings file is created in the new folder with the old file's own permissions (it holds passwords and is not readable by every user), never with the folder's. The settings file is created in the new folder with the old file's own permissions (it holds passwords), never with the
   folder's. Each program is built whole beside the installed one
   (`Panel.new`, `Agent.new`) and swapped in with two renames (the old folder to `.old`, the new one into place), then
   the old one is deleted: nothing is deleted from the installed program before the new one is complete, so a failure
   leaves the old program, or the new one, never half of each. If a program has a file open the first rename fails and the
   old program stays as it was.
3. **It checks that the result works.** The panel must answer its readiness check (`/health/ready`) within 5 minutes.
   The agent's service must be running, still be running 20 seconds later, and sign in at its panel
   (`PulsarAgent.exe status --check`, retried for up to 2 minutes). A node whose panel cannot be reached therefore
   rolls back: update the panel first, and have it running, when updating a node.
4. **If anything fails after the first change** (an error while replacing, or a check that does not pass), it puts
   the previous version back: it stops the services, restores the database from the backup **if the new version changed
   it** (the set of applied migrations differs from before the update, or could not be read: an older panel cannot read
   a newer database), puts the previous programs back from `previous` (settings files are never replaced, going forward
   or back), starts the services and checks that the previous version works. If the migrations are the same, the
   database is left as it is, so what the new panel wrote stays; if they differ, what was written since the backup is
   lost. The panel's keys are not restored (an older panel reads a key ring with a key more). The database is restored
   in one transaction after the new version's tables are renamed aside, so a restore that fails puts them back as they
   were.

A rolled back update ends with exit code `7` (with `-Unattended`; [exit codes](install.md#unattended-installs)) and the
message "The update to X failed and Pulsar was put back to Y: …" with the reason. Pulsar is then the previous version
and working; read the reason and the log, then update again when it is fixed (a new release, or a fixed machine). If
even the rollback fails, the exit code is `1` and the message says exactly what was done and what was not, where the
panel's backup is (restore it as [backup.md](backup.md) says) and where the previous programs are; a service that
could not be put back stays stopped, and a panel whose database could not be restored stays stopped with the new
program in place, since an older panel on newer tables is worse than a stopped one.

### A power cut during an update

If the machine loses power (or the update's process is killed) after the programs began to be replaced, the next
`-Update` run finds `in-progress.json` in the Updates folder and **rolls the interrupted update back first**, with the
same steps as a failed update (item 4 above), and only then goes on with its own. Before that it puts right a program
folder left between the two renames (`Panel.old` with no `Panel`: the old one is moved back; a half-built `Panel.new`
is deleted). If that rollback fails, the new update does not start, `in-progress.json` stays for the next try, and the
message says what is left, where the backup and the previous programs are, and how to go on: run `-Update` again with
**`-DiscardInterruptedUpdate`**, which rolls nothing back, keeps Pulsar as it is (check first that it works) and moves the
journal aside as `Updates\discarded-update-<time>.json`, a record of what was dropped. It is never done without that
switch. When an update ends, rolled back or not, its journal is first marked `completed` (written beside it, flushed,
moved over it) and then deleted; a completed journal that could not be deleted (an antivirus holding it) is only
discarded by the next run, never rolled back. If it can be neither marked nor deleted the update ends with an error that
says so, to delete it by hand before the next one. `-Uninstall` deletes it too. Only an update killed in the instant
between its last check and the marking would be rolled back by the next run (at the price of the writes since, if the
database had changed).

Every update, whatever its end, writes `C:\ProgramData\Pulsar\Updates\last-update.json` (UTF-8):

```json
{"from":"1.0.0","to":"1.0.1","parts":["agent","panel"],"status":"succeeded","error":null,"log":"C:\\ProgramData\\Pulsar\\Setup\\logs\\install-….log","attempt":"0f8e…","finishedAt":"2026-10-20T20:35:15Z"}
```

`status` is `succeeded`, `rolled_back` (the previous version is back and works) or `failed` (an error before
anything was replaced, or a rollback that did not work); `error` is the message, without secrets, at most 2000
characters; `attempt` is the panel's id for the update (`-UpdateAttempt`, which one-click updates give), null for an
update started by hand. The file is the last update's only: a run that stops before it starts (another update is running, or the
`Updates` folder is not as it must be) writes none. Only one update runs at a time (`update.lock` in the same folder).

`C:\ProgramData\Pulsar\Updates` and the folders in it are for SYSTEM and Administrators alone, as the installer makes
them: the update restores a database and runs programs from there as SYSTEM, so the script refuses (exit code `3`,
nothing changed) an `Updates` folder that exists but could be changed by anyone else, or is a link. Delete such a
folder (someone else may have made it) and run the update again.

### One-click updates

An update started from the panel ([From the panel](#from-the-panel)) runs the same `Install-Pulsar.ps1 -Update
-Unattended`, from `C:\ProgramData\Pulsar\Updates\<version>-<random>`, with everything above, and the agent reports
`last-update.json` back to the panel (and, when the installer ends without updating while the agent still runs, its exit
code and where its log is). Each request has an id of its own, which the agent passes to the installer
(`-UpdateAttempt`) and sends with everything it reports about it: the agent sends the last result again after every
reconnection, and the panel takes a result only for the request it names, so an earlier update's end (of the same
version too) never closes a later one, and a step reported late never moves an update back. Results without an id (an
agent or a release older than 1.0.3) are taken in order only: an end once the agent said it started the update, the
installer's result once the installer was started. It takes only releases of this project's GitHub repository whose `pulsar-update.json` is
signed by an update key Pulsar trusts ([releasing.md](releasing.md) is how the maintainer makes them).

## If something goes wrong

- An update that failed and was rolled back (exit code `7`) left Pulsar as it was: see `last-update.json` and the log
  it names. The pre-update backups are in `C:\ProgramData\Pulsar\Updates\backups`.
- The panel does not start after an update: its log (`C:\ProgramData\Pulsar\Panel\logs`) says why. To go back,
  restore the backup made before the update with the previous version's ZIP (`Install-Pulsar.ps1 -Uninstall`, drop the
  `pulsar` database and user that `-Uninstall` keeps, as [install.md](install.md#starting-over) shows, then
  `-Mode Panel -RestoreFrom …`, [backup.md](backup.md)). Without that drop the install stops before changing anything:
  it never replaces a database with data in it.
- A node shows as offline: on the node, `& 'C:\Program Files\Pulsar\Agent\PulsarAgent.exe' status --check`.
