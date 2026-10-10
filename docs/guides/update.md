# Updating Pulsar

Updates replace Pulsar's programs and keep everything else: settings, database, keys, nodes and game servers.

## Before updating

1. Read the release notes of every version between yours and the new one (the project's Releases page).
2. Back up the panel ([backup.md](backup.md)): an update may change the database, and a backup is the way back.

## Order

Update the **panel first, then the nodes**. A panel accepts agents of its own version and of the previous one, so nodes
keep working while you get to each of them; an agent newer than its panel is refused until the panel is updated.

## Updating

On each machine, download and check the new release ZIP as in [install.md](install.md) (step 2), extract it to a new
folder, open PowerShell as administrator there and run:

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-Pulsar.ps1 -Update
```

It updates whatever this machine has, the panel, the agent or both:

- **Panel**: the PulsarPanel service stops, its programs are replaced and it starts again, applying any database
  changes at start. Browsers reconnect on their own; signed-in users stay signed in.
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

## If something goes wrong

- The panel does not start after an update: its log (`C:\ProgramData\Pulsar\Panel\logs`) says why. To go back,
  restore the backup made before the update with the previous version's ZIP (`Install-Pulsar.ps1 -Uninstall`, then
  `-Mode Panel -RestoreFrom …`, [backup.md](backup.md)).
- A node shows as offline: on the node, `& 'C:\Program Files\Pulsar\Agent\PulsarAgent.exe' status --check`.
