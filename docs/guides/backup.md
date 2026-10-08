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

On the panel's machine, in PowerShell as administrator, from the folder of the extracted release ZIP:

```powershell
powershell -ExecutionPolicy Bypass -File .\Backup-Pulsar.ps1 -Destination D:\Backups
```

It writes `D:\Backups\pulsar-backup-<date>` with the database dump (`pulsar.dump`), the keys and certificates
(`panel-data`) and the settings. The panel keeps running. The folder holds secrets: only SYSTEM and Administrators can
read it. Keep a copy away from the machine (another disk, another computer, offline storage).

To do it every night, create a task in Task Scheduler that runs, as SYSTEM:

```
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\PulsarSetup\Backup-Pulsar.ps1 -Destination D:\Backups
```

and delete old backup folders now and then.

If PostgreSQL runs on another machine, pass the folder of PostgreSQL's programs (`pg_dump.exe`, from a PostgreSQL
installation of the same or a later major version) with `-PostgresBin`.

## Backing up game servers

Each server's **Backups** tab makes backups on its node (and schedules can make them every night). They are kept in
`C:\ProgramData\Pulsar\Agent\backups` on that node. Restoring one is done from the same tab.

To keep them safe from the loss of the node, copy them to S3-compatible storage (AWS S3, Cloudflare R2, Backblaze B2,
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
a server or a storage leaves the copies already in the bucket.

## Restoring the panel

### On the same machine (the database was lost or damaged)

1. Stop the panel: `Stop-Service PulsarPanel`.
2. Recreate the database from the dump, as the `postgres` user (PowerShell as administrator; adjust the PostgreSQL
   version in the path):

   ```powershell
   $bin = 'C:\Program Files\PostgreSQL\17\bin'
   & "$bin\psql.exe" -U postgres -c "DROP DATABASE IF EXISTS pulsar WITH (FORCE)" -c "CREATE DATABASE pulsar OWNER pulsar"
   & "$bin\pg_restore.exe" -U postgres -d pulsar --no-owner --role=pulsar D:\Backups\pulsar-backup-<date>\pulsar.dump
   ```

3. Start the panel: `Start-Service PulsarPanel`.

### On a new machine

1. Install PostgreSQL ([install.md](install.md), step 1) and extract the release ZIP of the **same or a later**
   version of Pulsar.
2. Copy the backup folder to the new machine and run, in PowerShell as administrator:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Install-Pulsar.ps1 -Mode Panel -RestoreFrom D:\Backups\pulsar-backup-<date>
   ```

   It creates the database in the local PostgreSQL (asking for the `postgres` password), restores the dump into it,
   puts the keys, certificates and settings back, and starts the panel on the port it used before. Users sign in with
   their passwords and two-factor codes as before.
3. Make the address nodes use for the panel (the `-PanelUrl` they were installed with) lead to the new machine, for
   example by pointing its DNS name there. Nodes then reconnect on their own: the panel has the same signing key and
   the same certificate, and the certificate already carries that name. A node that reached the panel by the old
   machine's own name needs that name to resolve to the new machine; otherwise enroll it again.

If the panel and an agent shared the machine that was lost, install the agent again on the new one from the panel:
delete the old node and add it again (**Nodes → Add node**), as its key was in that machine's key store. Its servers'
folders must be restored separately (they are on that machine's disk).

## Moving the panel to another machine

Back up on the old machine, stop its panel (`Stop-Service PulsarPanel`, or `Install-Pulsar.ps1 -Uninstall` if no
agent runs there), and restore on the new one as above.
