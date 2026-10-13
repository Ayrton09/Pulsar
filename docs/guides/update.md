# Updating Pulsar

Updates replace Pulsar's programs and keep everything else: settings, database, keys, nodes and game servers.

## Before updating

1. Read the release notes of every version between yours and the new one (the project's Releases page).
2. Back up the panel ([backup.md](backup.md)): an update may change the database, and a backup is the way back.

## Order

Update the **panel first, then the nodes**. A panel accepts agents of its own version and of the previous one, so nodes
keep working while you get to each of them; an agent newer than its panel is refused until the panel is updated.

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

It updates whatever this machine has, the panel, the agent or both:

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

The settings files (`appsettings.Production.json`) are never replaced.

A nightly backup task that runs `Backup-Pulsar.ps1` from an extracted release folder (`C:\PulsarSetup`, as this guide
once said) should run the copy in `C:\Program Files\Pulsar\Panel` instead, and the old folder be deleted
([backup.md](backup.md)): it runs as SYSTEM, and the old folder let any account change it. `-Update` warns about every
scheduled task that runs a `Backup-Pulsar.ps1` other than that copy. Delete old setup folders made directly under
`C:\` too: anyone could have changed what is in them.

## If something goes wrong

- The panel does not start after an update: its log (`C:\ProgramData\Pulsar\Panel\logs`) says why. To go back,
  restore the backup made before the update with the previous version's ZIP (`Install-Pulsar.ps1 -Uninstall`, then
  `-Mode Panel -RestoreFrom …`, [backup.md](backup.md)).
- A node shows as offline: on the node, `& 'C:\Program Files\Pulsar\Agent\PulsarAgent.exe' status --check`.
