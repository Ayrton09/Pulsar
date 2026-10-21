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
- **Or an SSH key** you added on your account page (see *SSH keys*): no password, and no code either.

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
- **Speed:** Windows' `sftp` 9.5 picks `aes256-ctr`, which costs its own process more than GCM does; on a fast link
  `sftp -c aes256-gcm@openssh.com -P 2022 alice@host` moves files faster (on one machine, about 240 MB/s instead of
  150). Both are offered; the client chooses.
- **Resuming:** a download resumes from where it stopped; an upload resumes by appending (FileZilla, `reput` in
  OpenSSH) or through a temporary `.filepart` that replaces the file once complete (WinSCP).
- Clients that only know the `password` method (no keyboard-interactive) can sign in users without two-factor
  authentication only.

Only SFTP is available: no shell, commands, terminals or port forwarding.

## SSH keys

A key you add on your account page (**My account → SSH keys**) signs you in to SFTP instead of your password. It signs
you in **on its own, also with two-factor authentication on**: the key is the second factor (something you have), so the
client asks for neither the password nor the code. That is handy for scripts and for clients that keep a session open,
and it is why adding a key asks for your password and, with two-factor authentication on, the code your authenticator
app shows now (a recovery code does not do here): a stolen browser session alone cannot add a way in. Keep the private
key to yourself and give it a passphrase; if a computer with one of your keys is lost, delete that key.

Accepted: **Ed25519** (the best choice), **ECDSA** (P-256, P-384, P-521) and **RSA** of 2048 bits or more, which signs
with SHA-2 (`rsa-sha2-256` or `rsa-sha2-512`; never the old SHA-1 `ssh-rsa` signatures, which clients stopped using).
DSA keys, security keys (`sk-ssh-ed25519@openssh.com` and the like) and certificates are refused. A user has at most 10
keys, and a key can be on one account only: someone who added your public key to their own account would otherwise
have your client sign in there, to their files, without a prompt. If the panel says a key of yours has been added
already and it is not on your list, tell an administrator (the audit log says who added it) and use another key.

### Make a key

- **PuTTYgen** (it comes with PuTTY and WinSCP; WinSCP opens it from *Tools → Run PuTTYgen*): choose **EdDSA**
  (Ed25519), **Generate**, type a passphrase, **Save private key** (a `.ppk` file for WinSCP and FileZilla), and copy the
  box **Public key for pasting into OpenSSH authorized_keys file** (one line, `ssh-ed25519 AAAA… eddsa-key-20261019`).
  The "Save public key" file is in another format (SSH2, RFC 4716) that the panel does not take.
- **OpenSSH** (Windows has it too: `ssh-keygen` in PowerShell): `ssh-keygen -t ed25519`. It writes the private key
  (`id_ed25519`) and the public key (`id_ed25519.pub`, one line) in `~/.ssh`.

Then on **My account → SSH keys**, **Add key**: paste the public key's line, give it a name (or leave it empty to use the
comment at the end of the line), and confirm with your password (and code). Never paste the private key: the panel
refuses it and says so. Each key is listed with its type, its SHA256 fingerprint as `ssh-keygen -lf` prints it, when it
was added and when it last signed in.

### Use it in a client

- **WinSCP:** in the login dialog, **Advanced… → SSH → Authentication → Private key file**: the `.ppk` (WinSCP converts an
  OpenSSH key when you pick one). Leave the password empty.
- **FileZilla:** in the Site Manager, protocol SFTP, **Logon Type: Key file** and the key file (`.ppk` or OpenSSH); or add
  it once for every site in *Edit → Settings → Connection → SFTP*.
- **OpenSSH:** `sftp -i ~/.ssh/id_ed25519 -o IdentitiesOnly=yes -P 2022 alice@panel.example.com`. `IdentitiesOnly=yes`
  offers only that key: without it, `ssh` offers every key it has, and each one that is not yours on the panel counts
  towards the limits below.

The user name is your panel user name as for the password (`alice`, or `alice.abcd2345` for one server's folder as the
root).

### What to know

- The key goes through the same account checks as a password: a disabled user, a locked-out account (wrong passwords) or
  a user whose role requires two-factor authentication they have not set up yet cannot sign in with it either. Sign-ins
  and failed sign-ins are audited with `publickey` and the key's fingerprint.
- A key that is not yours never counts towards the account's lockout (only passwords and codes do), and signing in with a
  key does not clear the count of wrong passwords. Key attempts take turns of the per-address limit
  (`RateLimiting:Auth`), and one connection may offer at most `Sftp:MaxKeyAttemptsPerConnection` keys (6): past them,
  the next question is answered no and the next signed attempt ends the connection. A request with an algorithm the
  panel does not take (`ssh-rsa`, which SSH.NET tries first, DSA, security keys, certificates), or with a key that is
  not a well-formed one of its algorithm (a point off its curve, say), is refused and counts as one of them; one past them ends the connection. Whatever the method, a connection makes at most 64 sign-in requests
  (far more than a client trying all its keys, a password and codes needs): the 65th ends it.
- Whether a key would do: clients ask before they sign with it, and the panel answers yes only for a key the user named
  has added, as OpenSSH does. Someone who has one of your public keys (they are public by nature, for example on GitHub)
  and guesses your user name can learn that the key is yours on this panel; they still cannot sign in without the private
  key. A key not added to that user, or a user name that does not exist, gets the same no, after the same database query.
- Deleting a key ends the SFTP sessions it signed in (the others stay). An administrator (`user.manage`) can delete all of
  a user's keys on the Users page, which ends every session one of them signed in. A new password, turning two-factor
  authentication off or on, or an administrator's reset of either ends the user's sessions but keeps their keys (as with
  API keys): delete the keys too if they may be in the wrong hands. A session signed in with a key also ends within 15
  seconds of a request when the key is gone by any other way.

## Sign-in rules

The same as the browser's sign-in:

- Wrong passwords count towards the account's lockout (5 in a row lock it for 15 minutes), each one, however many
  connections they come in, at the same time or not: an account's checks run one at a time, over SFTP and in the
  browser alike, and each keeps its audit entry. Wrong codes count too.
- Disabled and locked-out users, and unknown user names, get the same answer as a wrong password, after the same time.
- Attempts from one address are limited (`RateLimiting:Auth`, 10 a minute by default, counted apart from the web's),
  and one connection may try a password 3 times. Signed attempts with an SSH key take turns of the same limit; the
  questions whether a key would do have one of their own, 6 times as large.
- A user who must use two-factor authentication (their own choice or a role's) is asked for the code; one whose role
  requires it but who has not set it up yet must do so in the browser first. **Recovery codes are not accepted over
  SFTP:** they are for getting back into the panel and setting up a new authenticator there. An SSH key the user added
  signs them in without the code (see *SSH keys*).
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
| `MaxKeyAttemptsPerConnection` | 6 | SSH keys one connection may offer (questions, refused algorithms included, and signed attempts each) |
| `AuthenticationTimeout` | 00:00:30 | From the connection to a completed sign-in |
| `IdleTimeout` | 00:15:00 | A session with no request for this long is closed |
| `MaxHandlesPerSession` | 32 | Open files and folders of one session |
| `MaxPacketBytes` | 262144 | The largest SFTP packet (clients are told, `limits@openssh.com`) |
| `MaxReorderBytes` | 1048576 | Writes kept until a gap before them is filled, per session (each also counts 96 bytes; 256 at most) |
| `ReadWindowBytes` | 262144 | How far reads may skip around a running download before it starts again |

## How it works, and what to know

- Each request becomes what the web file manager does: listings by pages (up to 100,000 entries, not only the first 2,000),
  a stat, a download from an offset, a streamed upload, a move, a folder made or deleted. They go through the panel's
  checks and the agent's path rules, run as the server's Windows account, and use the same transfers between the panel
  and the node as the web (`files.transfers_busy` when a node already has 8 running, a user 3).
- Requests are handled one at a time, in order; their replies go out in that order while the next request is handled,
  at most 2 waiting behind the one being sent (each at most `MaxPacketBytes`). Reads are served in order from one
  download; a read a little ahead or just behind is served from what was kept, anything else starts a new download at
  that offset (audited as a download).
- Uploads stream to the node and are saved when the file is closed, complete or not at all; a file opened for writing
  is replaced as a whole (the time the client asks for is set afterwards). Writing at the end of an existing file, or
  with the append flag, adds to it in place (resumed uploads). Writes elsewhere in an existing file are refused, and a
  file cannot be open for reading and writing at once. Partial overwrite is not supported: an existing file opened
  without truncation and written from its start for fewer bytes than it holds is not saved (the close fails and the
  file stays as it was), where a local file would keep the rest; clients that upload whole files truncate them first.
- Writes that arrive ahead of a gap are kept until it is filled: each counts for its bytes plus 96 towards
  `MaxReorderBytes`, and a session keeps at most 256 of them; past either, that file's upload fails.
- A listing returns at most 100,000 entries of one folder: each page reads the folder again on the node, which reads
  at most 100,000 entries of it for a page, and the listing stops there (the panel log says so).
- `mkdir` makes one folder, in a folder that exists (`No such file` otherwise), as on a local disk.
- A file open for reading takes one of its node's transfer places (8 for a node, 3 for one user) only while it is
  being read: one read to its end, or not read for 30 seconds, gives the place back (the next read starts again).
- A read far from the previous one (more than 256 KiB back or ahead) starts the file's download from the node again,
  audited like a download; a session does that at most 30 times a minute, then such reads fail for the rest of the
  minute. Reading in order, as file clients do, never counts.
- A connection holds at most 4 channels at once (one more closes it) and runs at most 2 SFTP sessions at once
  (clients that open one per transfer open the next once one closes), and a channel that has not started SFTP within
  10 seconds is closed.
- Permissions and sizes sent with `chmod` or `setstat` are accepted and ignored (Windows has none to set); times are set.
- `rmdir` removes empty folders only. The node deletes a folder with what it holds, so the panel lists it first and
  refuses one that is not empty; something written into the folder between that listing and the deletion is deleted
  with it.
