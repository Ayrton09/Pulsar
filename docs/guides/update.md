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
- **Agent**: the PulsarAgent service stops, **and so do the node's game servers**, which run under it (each gets its
  game's normal stop); its programs are replaced and it starts again, and so do the servers that were running and those
  set to start automatically. Players are disconnected meanwhile: pick a quiet moment for each node.

The settings files (`appsettings.Production.json`) are never replaced.

## If something goes wrong

- The panel does not start after an update: its log (`C:\ProgramData\Pulsar\Panel\logs`) says why. To go back,
  restore the backup made before the update with the previous version's ZIP (`Install-Pulsar.ps1 -Uninstall`, then
  `-Mode Panel -RestoreFrom …`, [backup.md](backup.md)).
- A node shows as offline: on the node, `& 'C:\Program Files\Pulsar\Agent\PulsarAgent.exe' status --check`.
