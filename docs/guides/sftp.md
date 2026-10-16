# SFTP

Every server's files can be reached with an SFTP client (WinSCP, FileZilla, Windows' own `sftp`, OpenSSH), not only
through the web file manager: handy for uploading many maps or mods or keeping a folder in sync. The panel runs the SFTP
server itself, on one port for every node; nodes accept no connections of their own. Plain FTP is not offered: it sends
passwords in clear.

## Connect

- **Host:** the panel's host name (or the one the administrator set, see *Settings*).
- **Port:** 2022 unless the administrator changed it.
- **User name and password:** your panel user name and password. Two-factor users are asked for the code from their
  authenticator app after the password (`Verification code:`).

```
sftp -P 2022 alice@panel.example.com
```

With the user name `alice`, the root lists one folder per server whose files you may see, named after the server and its
short id (`My_Server-abcd2345`). With `alice.abcd2345` (your user name, a dot and a server's short id) that server's
folder is the root: handy for a bookmark per server. The root itself cannot be changed.

What you may do is what the web file manager lets you do: you need `server.files.view` on a server to see and download
its files, and `server.files.edit` to upload, rename, delete or make folders. Both are checked again at every request,
so a role taken away applies at once. The same rules as the web apply: Pulsar's `.pulsar` folder and names with invisible
format characters (such as U+202E) are not shown at all, links are listed but never followed, changes are refused while
an install, update or restore runs, a server whose node is offline answers with an error, and an upload is at most the
web's upload limit (`Files:MaxUploadBytes`, 1 GiB by default). Every change and download is in the audit log, with
"SFTP", the client's program and address; sign-ins and failed sign-ins too.

### Check the host key at the first connection

The first time, the client shows the server's host key fingerprint and asks whether to trust it. Compare it with the one
the panel shows (and the panel log prints at every start: `SFTP listening on …, host key SHA256:…`): they must be the
same, for example `SHA256:q8URNWahibMuP6HE3x1bfk3+IBKJXOI1AsIo1/SmroU`. If they differ, do not connect: something
between you and the panel is answering in its place. Signed-in users read it with `GET /api/v1/sftp`, which also gives
the host, the port and their user name.

The key is ECDSA P-256, made at the panel's first start and kept in the database, encrypted with the panel's Data
Protection keys (like the panel signing key): back up both with the panel. If the key ring is lost, SFTP does not start
(the panel log says why) rather than make a new key silently; to make a new one on purpose, clear it
(`UPDATE sftp_settings SET host_public_key = NULL, protected_host_key = NULL;`) and restart the panel. Every client then
warns that the host key changed, and users have to check the new fingerprint.

### OpenSSH 10.1 and later: the post-quantum warning

OpenSSH 10.1 and newer print a warning that the connection does not use post-quantum key exchange. The SSH library the
panel uses has none yet; the connection is still encrypted with ECDH (`ecdh-sha2-nistp384` or `nistp256`) and AES-256
(GCM, or CTR with an encrypt-then-MAC HMAC-SHA2). It can be silenced for this host with `WarnWeakCrypto no` in
`~/.ssh/config` (`Host panel.example.com`).

## Clients

- **WinSCP, FileZilla:** protocol SFTP, port 2022; they ask for the password and, for two-factor users, the code.
- **Windows' `sftp` and OpenSSH:** `sftp -P 2022 alice@host`. In a script (`-b`), `sftp` turns password prompts off
  (`BatchMode=yes`): add `-o BatchMode=no`.
- **Resuming:** a download resumes from where it stopped; an upload resumes by appending (FileZilla, `reput` in
  OpenSSH) or through a temporary `.filepart` that replaces the file once complete (WinSCP).
- Clients that only know the `password` method (no keyboard-interactive) can sign in users without two-factor
  authentication only.

Only SFTP is available: no shell, commands, terminals or port forwarding. SSH keys come in a later phase.

## Sign-in rules

The same as the browser's sign-in:

- Wrong passwords count towards the account's lockout (5 in a row lock it for 15 minutes), each one, however many
  connections they come in. Wrong codes count too.
- Disabled and locked-out users, and unknown user names, get the same answer as a wrong password, after the same time.
- Attempts from one address are limited (`RateLimiting:Auth`, 10 a minute by default, counted apart from the web's),
  and one connection may try a password 3 times.
- A user who must use two-factor authentication (their own choice or a role's) is asked for the code; one whose role
  requires it but who has not set it up yet must do so in the browser first. **Recovery codes are not accepted over
  SFTP:** they are for getting back into the panel and setting up a new authenticator there.
- A session ends when the user is disabled or deleted, their password, two-factor setup or roles change, they sign out
  of the panel (sign-outs end all of the user's live connections, SFTP ones included), or any role changes. It ends too
  after 15 minutes without a request.

## Settings

An administrator (`settings.manage`) changes them at runtime with `GET`/`PUT /api/v1/sftp/settings`
(`{ "enabled": true, "port": 2022, "publicHost": null }`; the answer adds the host key's fingerprint and whether the
server is listening). Turning SFTP on or off or changing its port restarts it and ends every SFTP connection; the change
is audited (`sftp.settings_changed`). `publicHost` is the host name users are told to connect to (a host name or an IP
address); empty, it is the host they reach the panel by. Remember to open the new port in the firewall.

It listens where the panel's HTTPS endpoints listen: on every address when one of them does (the installer's default),
else on the addresses they name (the loopback for `localhost`). `Sftp:BindAddress` in `appsettings.Production.json` sets
an address (or `*`) instead.

Limits, in the `Sftp` section of the panel's settings (the defaults suit most panels):

| Setting | Default | What |
|---|---|---|
| `MaxConnections` | 64 | Connections at once; one more is closed on arrival |
| `MaxUnauthenticatedConnections` | 16 | Of those, not signed in yet |
| `MaxConnectionsPerAddress` | 8 | From one address (an IPv4 address, an IPv6 /64, the panel's own machine as one) |
| `MaxSessionsPerUser` | 4 | Signed-in sessions of one user; one more is refused with a message |
| `MaxPasswordAttemptsPerConnection` | 3 | Password checks before the connection is closed |
| `AuthenticationTimeout` | 00:00:30 | From the connection to a completed sign-in |
| `IdleTimeout` | 00:15:00 | A session with no request for this long is closed |
| `MaxHandlesPerSession` | 32 | Open files and folders of one session |
| `MaxPacketBytes` | 262144 | The largest SFTP packet (clients are told, `limits@openssh.com`) |
| `MaxReorderBytes` | 1048576 | Writes kept until a gap before them is filled, per session |
| `ReadWindowBytes` | 262144 | How far reads may skip around a running download before it starts again |

## How it works, and what to know

- Each request becomes what the web file manager does: listings by pages (every entry, not only the first 2,000),
  a stat, a download from an offset, a streamed upload, a move, a folder made or deleted. They go through the panel's
  checks and the agent's path rules, run as the server's Windows account, and use the same transfers between the panel
  and the node as the web (`files.transfers_busy` when a node already has 8 running, a user 3).
- Requests are answered one at a time, in order. Reads are served in order from one download; a read a little ahead or
  just behind is served from what was kept, anything else starts a new download at that offset (audited as a download).
- Uploads stream to the node and are saved when the file is closed, complete or not at all; a file opened for writing
  is replaced as a whole (the time the client asks for is set afterwards). Writing at the end of an existing file, or
  with the append flag, adds to it in place (resumed uploads). Writes elsewhere in an existing file are refused, and a
  file cannot be open for reading and writing at once.
- Permissions and sizes sent with `chmod` or `setstat` are accepted and ignored (Windows has none to set); times are set.
- `rmdir` removes empty folders only. The node deletes a folder with what it holds, so the panel lists it first and
  refuses one that is not empty; something written into the folder between that listing and the deletion is deleted
  with it.
