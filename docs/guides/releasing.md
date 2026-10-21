# Releasing Pulsar

This guide is for the project's maintainer. A release that installed panels and agents can update to is built and
signed on the maintainer's PC and published to the project's GitHub repository, `Ayrton09/Pulsar`. The update key never
goes to GitHub: GitHub only carries the files, so whoever could publish a release there still cannot make a panel or an
agent take a file nobody signed ([update.md](update.md), "One-click updates").

How it fits together:

- A release has `pulsar-<version>-windows-x64.zip` (the programs and `Install-Pulsar.ps1`), the agent MSI, the two setups
  and a `.sha256` file for each, as before, plus two files for updates:
  - `pulsar-update.json`, the **manifest**: the version, the oldest version that may update to this one
    (`minimumUpdateFrom`), the release notes, and every file of the release with its size and SHA-256;
  - `pulsar-update.json.sig`, its **signature** by an update key.
- Every Pulsar build has the public keys it trusts built in (`UpdateKeys.Trusted` in
  `src/Pulsar.Contracts/Updates.cs`). It takes an update only if the signature on the manifest verifies against one of
  them, and then only the files the signed manifest names, with the sizes and SHA-256 it gives.

## What you need

- The repository's checkout, the .NET SDK, Node.js, and Inno Setup 7 (for the setups; `-RequireSetups` fails without it),
  as in [CONTRIBUTING.md](../../CONTRIBUTING.md).
- The GitHub CLI, [`gh`](https://cli.github.com), signed in as a maintainer of the repository (`gh auth login`). The
  scripts never ask for a token and never take one as a parameter.
- The repository must be **public**: installations download releases without signing in. `Publish-GitHubRelease.ps1`
  refuses a private repository (`-AllowPrivate` overrides that for a repository you are only testing with).

## Making the update key (once)

```powershell
dotnet run --project tools/Pulsar.UpdateSigner -c Release -- new-key D:\keys\pulsar-update.key
```

It asks for a passphrase twice (typed without echo) and writes the key file, encrypted with it, then prints the
**public key** and its id. The passphrase is never a command-line argument: the signer asks for it at the console, or
reads the `PULSAR_UPDATE_KEY_PASSPHRASE` environment variable when there is no console to type in. In a terminal where
typing without echo does not work, `scripts\release\New-UpdateSigningKey.ps1` asks for it with PowerShell's prompt, which
shows an asterisk for each character. It builds the signer first (with no MSBuild or compiler server left running, which
would keep the environment they started with) and puts the passphrase in the environment of the signer's own run only,
taking it out as soon as that ends:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\release\New-UpdateSigningKey.ps1 -Path D:\keys\pulsar-update.key
```

- **Keep an offline copy** of the key file and the passphrase (a USB stick in a drawer, a password manager's vault). A
  lost key cannot sign updates, and every installation would need a release built with a new trusted key installed by
  hand (a setup, or the ZIP) before it updates again.
- Keep the key out of the repository and off GitHub, including its secrets and Actions. Anyone who gets the key file
  and the passphrase can sign updates that every installation takes.
- `dotnet run --project tools/Pulsar.UpdateSigner -c Release -- public-key <key file>` prints the public key again.

**Add the public key to `UpdateKeys.Trusted`** in `src/Pulsar.Contracts/Updates.cs` and release a version built with it
**before** you sign anything with that key: the signer refuses a key that `UpdateKeys.Trusted` does not list, and an
installation takes an update signed by a key only when its installed version trusts it. The first release that lists a
key therefore reaches installations by the setups or the ZIP (or by a release signed with an older key that is trusted
already, see [Key rotation](#key-rotation)).

## Building a release

Pick the version, exactly `major.minor.patch` (for example `1.0.1`), and write the release notes in a plain text file
(UTF-8, at most 16 KB, shown as text to the administrator who decides to update). Then, in PowerShell:

```powershell
.\scripts\release\Build-Release.ps1 -Version 1.0.1 -RequireSetups -UpdateSigningKey D:\keys\pulsar-update.key -ReleaseNotes notes.txt
```

(add `-SigningThumbprint …` to Authenticode-sign the programs as well,
[the roadmap's "Code signing"](../architecture/09-roadmap.md); add `-RequireUpdateSignature` to fail any build that would
not end up with a signed manifest.) **Commit everything first**: before it builds anything, the script reads the commit
you are on and refuses a checkout with uncommitted changes (modified or untracked files; ignored files such as the built
web app do not count), because the release would not be what any commit holds. `-AllowDirtyTree` builds anyway, for
trying things out: the release is marked `dirty`, `Publish-GitHubRelease.ps1` never publishes it, and it cannot be combined with
`-UpdateSigningKey` or `-RequireUpdateSignature` (such a build never gets the update key's signature). The commit is
written into `release.json` and into `pulsar-<version>.commit`, which is published with the release and named by the
signed manifest. The script builds everything in a work folder, as before, and, only when every file is
built, checksummed and verified:

1. writes `pulsar-update.json` for the files it is about to publish (not the `.sha256` files), with `-MinimumUpdateFrom`
   (default `1.0.0`) and the notes. A version that is not exactly `major.minor.patch` (`0.0.0-dev`, a pre-release)
   gets a warning and no manifest;
2. builds `tools/Pulsar.UpdateSigner`, asks you for the key's passphrase at the console (or reads
   `PULSAR_UPDATE_KEY_PASSPHRASE`, which no build step that runs before the signer inherits) and signs the manifest;
3. runs the signer's `verify` on it, against the keys built into the programs, and fails the build if it does not verify;
4. publishes the files into `artifacts\release` all at once, as before.

A build that fails never signs. Check the result any time with
`dotnet run --project tools/Pulsar.UpdateSigner -c Release -- verify artifacts\release\pulsar-update.json`.

## Publishing

```powershell
.\scripts\release\Publish-GitHubRelease.ps1 -Version 1.0.1
```

Add `-CheckOnly` first to run every check and upload nothing. The script checks, before it uploads anything, that `gh` is
there and the repository is public; that `artifacts\release` has the manifest and its signature for that version and
the signature verifies against the keys Pulsar trusts; that every file the manifest names exists with the size and
SHA-256 it says (and that the `.sha256` files agree); and that the release `v1.0.1` and its tag do not exist yet. It
then runs `gh release create v1.0.1 …` as a normal release, not a draft or a pre-release, so that it is the **latest**
release: that is the one panels find (`releases/latest`). It uploads the files the manifest names, their `.sha256`
files, `pulsar-update.json` and `pulsar-update.json.sig`, and makes the tag at the commit you are on (`-Target` names
another one, resolved to its full hash); that commit must be pushed, and **must be the commit the release was built
at**: the script reads it from `pulsar-<version>.commit` (its SHA-256 is in the signed manifest) and refuses to publish
when HEAD or `-Target` is another commit, when the manifest does not name that file (a release built before this
check: build it again), or when the build was dirty. The title is `Pulsar 1.0.1` and the notes are the manifest's (or `-Notes`).

The release workflow (`.github/workflows/release.yml`) is run by hand and is not how update releases are made: it has no
update key. A tag created by `gh release create` does not start it.

## Versions

- **Versions strictly increase.** A panel or agent offers an update only to a version newer than the one installed,
  and an update never goes back to an older one. Never replace the files of a published version: installations may
  have cached or taken it already. If a release is wrong, publish the next version (`1.0.2`) with the fix; delete the
  bad release from GitHub if it should not be offered (the newest release that is not a draft or a pre-release is the
  one installations find, so the previous one becomes the latest again).
- **`-MinimumUpdateFrom`** is the oldest installed version that may update to this release directly. Use it when the
  update can only start from a version that has something the older ones lack (a database change that needs an
  intermediate step, a new requirement in the update itself). An installation older than that is told to take the
  releases in between, or to use the setups. It must be `major.minor.patch` and not newer than the release.

## Key rotation

To replace the update key (a suspected leak, or a precaution), without stranding installations:

1. Make the new key ([above](#making-the-update-key-once)). Add its public key to `UpdateKeys.Trusted` **next to** the
   old one (do not remove the old).
2. Build and publish a release signed by the **old** key: installations take it (they trust the old key), and from
   then on they trust the new key too.
3. Wait until the installations you care about have taken that release (the setups and the ZIP carry it as well).
   Releases signed by the new key from then on are taken by those installations.
4. In a later release, signed by the new key, remove the old key from `UpdateKeys.Trusted`. An installation that
   never took the bridging release still trusts only the old key and cannot take this one: it updates by the setups.

If the old key was **leaked**, step 2 is the weak point (whoever has it can sign too): ship the bridging release at
once, tell users to update by hand with a ZIP or setup downloaded from the repository's releases page and checked
against its SHA-256, and retire the old key in the next release. A key that cannot be trusted any more must not be
left in `UpdateKeys.Trusted` for longer than it takes installations to take the release that drops it.
