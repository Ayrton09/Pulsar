# Backing up and restoring Pulsar

## What to back up

| What | Where | Why |
|---|---|---|
| The panel's database | PostgreSQL (`pulsar` database) | Users, roles, nodes, servers, schedules, audit log |
| The panel's keys | `C:\ProgramData\Pulsar\Panel\keys` and `keyring` | Without them the database's secrets cannot be read: game server tokens and passwords, users' two-factor keys, and the panel's signing key (nodes trust that key: without it every node must be enrolled again) |
| The panel's HTTPS certificate | `C:\ProgramData\Pulsar\Panel\tls` | Nodes and browsers trust it |
| The panel's settings | `C:\Program Files\Pulsar\Panel\appsettings.Production.json` | Database and certificate passwords |
| Game servers' backups | each node's `C:\ProgramData\Pulsar\Agent\backups` | Made by the panel's Backups tab and schedules; they stay on the node |

The database and the keys always go together: a database backup without the keys loses every secret in it, and the
keys alone hold no data.

The keys are encrypted with the panel's key ring certificate (`keyring\keyring.pfx`, whose password is in the
settings), not with Windows' machine DPAPI, so a backup can be restored on another machine.

## Backing up the panel

On the panel's machine, in PowerShell as administrator, run the copy the installer keeps in the panel's folder:

```powershell
powershell -ExecutionPolicy Bypass -File "C:\Program Files\Pulsar\Panel\Backup-Pulsar.ps1" -Destination D:\PulsarBackups
```

It writes `D:\PulsarBackups\pulsar-backup-<date>` with the database dump (`pulsar.dump`), the keys and certificates
(`panel-data`) and the settings. The panel keeps running. The folder holds secrets: only SYSTEM and Administrators can
read it, so the destination must be on a drive that keeps permissions (NTFS or ReFS): on a FAT or exFAT drive (most
USB sticks), or a share that ignores permissions, the script stops before writing anything. Keep a copy away from the
machine (another disk, another computer, offline storage).

**The destination.** Give `-Destination` a folder that does not exist yet, directly on a drive (`D:\PulsarBackups`) or
in a folder only administrators can change: the script makes it for SYSTEM and Administrators alone, and uses it again
on later runs. Do not make it yourself (with `New-Item` or Explorer): a folder made directly on a drive lets every
signed-in account change it, game servers' accounts included, and such an account could rename it aside while the
database is dumped and put one of its own in its place, which would get the keys. The script refuses a destination that
anyone but SYSTEM, Administrators and TrustedInstaller could change, rename or replace (a folder above it included),
and checks it again before it copies the keys. A destination made as earlier versions of this guide had it
(`D:\Backups`, which took the drive's permissions) is usually refused for that reason: give a new one, and move the
old backups into it.

To do it every night, create a task in Task Scheduler that runs, as SYSTEM:

```
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\Program Files\Pulsar\Panel\Backup-Pulsar.ps1" -Destination D:\PulsarBackups
```

and delete old backup folders now and then. Point the task at that copy, never at one in a folder of your own (the
extracted release, a download folder): the task runs the script as SYSTEM, so whoever can change the script gets the
machine and the panel's secrets, and a folder made directly under `C:\` lets every signed-in account change it, game
servers' accounts included. The installer puts the copy in the panel's folder, which only administrators can change,
and replaces it with every update; that folder is what protects it. The script also refuses to run (backing up
nothing) from a folder that accounts other than SYSTEM, Administrators and TrustedInstaller could change, which catches
a task pointed at the wrong copy, but that check is inside the script: whoever could already rewrite the script could
take it out. If you made the task as earlier versions of this guide said, for `C:\PulsarSetup\Backup-Pulsar.ps1`,
change its path to the one above and delete that folder: the old copy there does not check where it runs, and anyone
could have changed it (`Install-Pulsar.ps1 -Update` warns about such tasks).

If PostgreSQL runs on another machine, pass the folder of PostgreSQL's programs (`pg_dump.exe`, from a PostgreSQL
installation of the same or a later major version) with `-PostgresBin`. The script runs `pg_dump.exe` as administrator
(as SYSTEM in the task), so it refuses one, `-PostgresBin`'s or a local service's, that anyone but SYSTEM,
Administrators and TrustedInstaller could change, in a folder they could add files to or below one they could rename,
before running it at all, as `Install-Pulsar.ps1` refuses such a `psql.exe`: keep PostgreSQL in Program Files (its
installer's default).

## Backing up game servers

Each server's **Backups** tab makes backups on its node (and schedules can make them every night). They are kept in
`C:\ProgramData\Pulsar\Agent\backups` on that node. Restoring one is done from the same tab.

### While the server runs

A backup does not need the server stopped. When the game runs, Pulsar first asks it to save its world (for the games
whose template says how: Minecraft, Rust, ARK, Palworld, 7 Days to Die and Project Zomboid), then takes a snapshot of
the disk with Windows' Volume Shadow Copy service and copies the files from it: every file as it was at the same
moment, including the ones the game keeps open. The snapshot lasts only while the backup reads it; the commands appear
in the server's console, and Minecraft is told to save again (`save-on`) right after the snapshot.

Each backup says how it was made: *Copied from a snapshot of the disk*, or *Copied while the game ran, without a
snapshot* with the reason. Without a snapshot, files the game wrote meanwhile may be half written and files it held
open are left out. The usual reasons:

- **The agent is not running as an administrator**: a development agent. The installed service runs as SYSTEM and can.
- **This disk cannot have snapshots**: the servers' folder is on a FAT or exFAT drive or a network share. Move it to an
  NTFS or ReFS volume.
- **The disk has no room for the snapshot**: Windows keeps a snapshot's changes in the volume's *shadow storage*. Check
  it with `vssadmin list shadowstorage` and, as administrator, give it more room, for example
  `vssadmin resize shadowstorage /for=D: /on=D: /maxsize=10%`.
- **The Volume Shadow Copy service is not running**: its startup type must be *Manual* (the default) or *Automatic*.
- **Snapshots are turned off on this node**: `"Pulsar": { "BackupSnapshots": false }` in the agent's
  `appsettings.Production.json` (next to `PulsarAgent.exe`) turns them off; remove it to turn them on again.
- **The game started while its files were read**: the backup began with the server stopped, so it took no snapshot,
  and the game was started before it finished. It says it was copied live because files the game wrote meanwhile may
  be half written; make another one if that matters.

### Restoring clean

Restoring writes the backup's files over the current ones and keeps the files the backup does not have. Tick **Delete
the files made after this backup** to delete those first: for a game world, regions explored after the backup would
otherwise stay next to the restored ones. Only files that the rules the backup was made with would have taken are
deleted: the agent records those rules in the backup, and a backup made before this version uses the template's
current rules. The installed game, logs and other files outside them stay. A backup that left out files the game held
open cannot be restored clean (it would delete them), and neither can a node whose agent is older than this version
(update the agent).

If a newer file cannot be deleted because a program has it open, the backup's files are still restored and the
restore reports which file stayed: close what holds it and restore again.

Through the API: `POST /api/v1/servers/{id}/backups/{backupId}/restore?clean=true`.

### Remote copies

To keep backups safe from the loss of the node, copy them to S3-compatible storage (AWS S3, Cloudflare R2, Backblaze B2,
or your own MinIO or SeaweedFS):

1. Create a bucket at the provider, and an access key that can read, write and delete in **that bucket only**.
2. In the panel, **Administration → Backup storage → Add storage**:
   - **Endpoint:** empty for AWS S3; `https://<account>.r2.cloudflarestorage.com` for R2; `https://s3.<region>.backblazeb2.com` for B2;
     your server's address otherwise (MinIO on a machine at home: `http://192.168.1.20:9000`). It must be `https://`,
     except for storage on the node itself or on your local network: plain `http://` is accepted only to a loopback,
     private or link-local address (`127.0.0.0/8`, `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, `169.254.0.0/16`,
     `::1`, `fc00::/7`, `fe80::/10`). The panel refuses `http://` to a public IP address; for a host name, the node
     resolves it when it copies a backup and refuses it unless every address it resolves to is one of those (it then
     connects only to those addresses).
   - **Region:** the bucket's (AWS, B2), `auto` (R2), `us-east-1` (most others).
   - **Bucket**, a **folder** in it, the **access key id** and **secret access key**; keep **Bucket in the path** on
     except for AWS S3.
3. In each server's **Backups** tab, **Remote copy**: choose the storage.

From then on every new backup is uploaded after it is made, and the tab says whether each copy is there (a failed copy
leaves the backup on the node and is notified to the channels that want *Backup failed*). If the node loses a backup,
restoring or downloading it brings it back from the storage. Deleting a backup in Pulsar deletes its copy too; deleting
a server or a storage leaves the copies already in the bucket. While a storage holds copies its endpoint and bucket
cannot change (Pulsar finds the copies there): new keys, the region, the folder for new copies and *Bucket in the
path* can. To move to another bucket, add a storage for it and choose it in the servers.

## Restoring the panel

### On the same machine (the database was lost or damaged)

1. Stop the panel: `Stop-Service PulsarPanel`.
2. Recreate the database as the `postgres` user, in the `postgres` database, then restore the dump into it as
   `pulsar`, never as `postgres`: a dump carries the panel's own objects (functions among them), and whoever had the
   `pulsar` role's password could have planted one that would run as the superuser in a restore made as `postgres`
   (`--role` does not stop it). `pg_restore` asks for the `pulsar` role's password: it is in the panel's connection
   string (`ConnectionStrings:Pulsar` in `C:\Program Files\Pulsar\Panel\appsettings.Production.json`) or, if you no longer have it, set a
   new one with `\password pulsar` and put it in that connection string too (PowerShell as administrator; adjust the
   PostgreSQL version in the path):

   ```powershell
   $bin = 'C:\Program Files\PostgreSQL\18\bin'
   & "$bin\psql.exe" -X -h localhost -U postgres -d postgres -c "DROP DATABASE IF EXISTS pulsar WITH (FORCE)" -c "CREATE DATABASE pulsar OWNER pulsar"
   & "$bin\pg_restore.exe" -h localhost -U pulsar -d pulsar --no-owner D:\PulsarBackups\pulsar-backup-<date>\pulsar.dump
   ```

   Or restore with the installer, which does the same: `Install-Pulsar.ps1 -RestoreFrom` (docs/guides/install.md).

3. Start the panel: `Start-Service PulsarPanel`.

### On a new machine

1. Install PostgreSQL ([install.md](install.md), step 1) and extract the release ZIP of the **same or a later**
   version of Pulsar into a new folder in `C:\Program Files` ([install.md](install.md), step 2).
2. Copy the backup folder to the new machine and run, in PowerShell as administrator:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Install-Pulsar.ps1 -Mode Panel -RestoreFrom D:\PulsarBackups\pulsar-backup-<date>
   ```

   It creates the database in the local PostgreSQL (asking for the `postgres` password), restores the dump into it,
   puts the keys, certificates and settings back, and starts the panel on the port it used before. Users sign in with
   their passwords and two-factor codes as before.
3. Make the address nodes use for the panel (the `-PanelUrl` they were installed with) lead to the new machine, for
   example by pointing its DNS name there. Nodes then reconnect on their own: the panel has the same signing key and
   the same certificate, and the certificate already carries that name. A node that reached the panel by the old
   machine's own name needs that name to resolve to the new machine; otherwise enroll it again. If the new machine
   has another IP address, the restored certificate does not list it: `Install-Pulsar.ps1 -RenewCertificate` makes
   one that does ([Your server's IP changed](install.md#your-servers-ip-changed)).

If the panel and an agent shared the machine that was lost, install the agent again on the new one from the panel:
delete the old node and add it again (**Nodes → Add node**), as its key was in that machine's key store. Its servers'
folders must be restored separately (they are on that machine's disk).

## Moving the panel to another machine

Back up on the old machine, stop its panel (`Stop-Service PulsarPanel`, or `Install-Pulsar.ps1 -Uninstall` if no
agent runs there), and restore on the new one as above.
