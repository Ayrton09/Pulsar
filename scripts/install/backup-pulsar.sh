#!/usr/bin/env bash
# Backs up the Pulsar panel installed on this machine by install-pulsar.sh: its database, its keys and certificates,
# and its settings (docs/guides/backup.md). Run it as root, from the copy the installer keeps:
#
#   sudo /opt/pulsar/backup-pulsar.sh --destination /root/pulsar-backups
#
# It writes a folder pulsar-backup-<date> under the destination, in the format of Windows' Backup-Pulsar.ps1, so a
# backup restores on either system (install-pulsar.sh --restore-from, or Install-Pulsar.ps1 -RestoreFrom):
#   pulsar.dump                  PostgreSQL dump of the panel's database (pg_dump custom format)
#   panel-data/                  the panel's Data Protection keys, key ring certificate and HTTPS certificate
#   appsettings.Production.json  the panel's settings (the certificates' passwords; the database's, if it has one)
#   backup.json                  the Pulsar version, the migrations the database had applied and the SHA-256 of every
#                                file above: a restore checks them, and refuses a backup of a newer Pulsar
#
# The database's secrets can only be read with the keys, so the three always go together. The folder holds secrets:
# it is readable by root only, the destination must be a folder only root can change, and a copy belongs away from
# this machine. Game servers' own backups stay on their nodes.
set -Eeuo pipefail
# A failed command inside $(...) ends it too (bash clears -e there otherwise); a pipe whose reader stops early (head,
# grep -q) is never used on a long output, since pipefail would count the writer's SIGPIPE as a failure.
shopt -s inherit_errexit
umask 077
export LC_ALL=C
export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin

readonly INSTALL_ROOT=/opt/pulsar
readonly PANEL_DIR=$INSTALL_ROOT/panel
readonly SETTINGS=/etc/pulsar/panel.json
readonly STATE=/etc/pulsar/install.json
readonly PANEL_DATA=/var/lib/pulsar/panel
readonly ACCOUNT=pulsar

fail() {
    printf 'ERROR: %s\n' "$2" >&2
    exit "$1"
}

DESTINATION=
while (($# > 0)); do
    case $1 in
    --destination) DESTINATION=${2:?--destination needs a folder}; shift ;;
    -h | --help)
        sed -n '2,/^set -Eeuo/p' "${BASH_SOURCE[0]}" | sed '$d; s/^# \{0,1\}//'
        exit 0
        ;;
    *) fail 2 "Unknown option $1 (--help says how to run it)." ;;
    esac
    shift
done
[[ -n $DESTINATION ]] || fail 2 'Give the folder to write the backup in: --destination <folder>.'
[[ $(id -u) -eq 0 ]] || fail 3 'Run this script as root (sudo).'
[[ -f $SETTINGS && -f $STATE && -d $PANEL_DATA ]] || fail 3 "The panel is not installed here by install-pulsar.sh ($SETTINGS, $STATE and $PANEL_DATA must exist)."
for tool in jq pg_dump psql sha256sum runuser; do
    command -v "$tool" >/dev/null 2>&1 || fail 3 "$tool is missing (the PostgreSQL client programs, jq): install it first."
done

# Whether a path, and every folder above it, can be changed only by root (a sticky folder above, like /tmp, lets
# others add entries, not rename or replace root's).
path_owned_by_root() {
    local path=$1 mode
    while :; do
        [[ ! -L $path && $(stat -c %u -- "$path") == 0 ]] || return 1
        mode=$(stat -c %a -- "$path")
        if (((8#$mode & 8#022) != 0)); then
            [[ $path != "$1" && -d $path ]] && (((8#$mode & 8#1000) != 0)) || return 1
        fi
        [[ $path == / ]] && return 0
        path=$(dirname -- "$path")
    done
}

# This script runs as root (a timer may run it every night): whoever else could change it could run code as root.
script=$(readlink -f -- "${BASH_SOURCE[0]}")
path_owned_by_root "$script" || fail 3 "$script, or a folder above it, can be changed by another user than root: run the copy in $INSTALL_ROOT."

if [[ ! -e $DESTINATION ]]; then
    mkdir -p -- "$DESTINATION"
    chmod 0700 -- "$DESTINATION"
fi
[[ -d $DESTINATION ]] || fail 2 "--destination $DESTINATION is not a folder."
DESTINATION=$(cd -- "$DESTINATION" && pwd -P)
path_owned_by_root "$DESTINATION" || fail 2 "--destination $DESTINATION, or a folder above it, can be changed by another user than root: the backup holds the panel's keys."

target=$DESTINATION/pulsar-backup-$(date -u +%Y%m%d-%H%M%S)
[[ ! -e $target ]] || fail 1 "$target exists already: run it again in a second."
mkdir -m 0700 -- "$target"
trap 'status=$?; if ((status != 0)); then rm -rf -- "$target"; echo "The backup failed; nothing was left in $DESTINATION." >&2; fi' EXIT

# The database: as the panel's system user over the local socket (peer), or with the connection string's password in
# the environment of pg_dump (never on its command line).
connection=$(jq -r '.ConnectionStrings.Pulsar' "$SETTINGS")
host='' port=5432 database='' user='' password='' sslmode=prefer rootcertificate=''
IFS=';' read -r -a parts <<<"$connection"
for part in "${parts[@]}"; do
    key=$(printf '%s' "${part%%=*}" | tr '[:upper:]' '[:lower:]' | tr -d ' ')
    value=${part#*=}
    case $key in
    host | server) host=$value ;;
    port) port=$value ;;
    database | db) database=$value ;;
    username | user | userid) user=$value ;;
    password | pwd) password=$value ;;
    rootcertificate) rootcertificate=$value ;;
    sslmode)
        case ${value,,} in
        require) sslmode=require ;;
        verifyca | verify-ca) sslmode=verify-ca ;;
        verifyfull | verify-full) sslmode=verify-full ;;
        disable) sslmode=disable ;;
        esac
        ;;
    esac
done
target_db="host=$host port=$port dbname=$database user=$user"
pg() {
    local program=$1
    shift
    if [[ $(jq -r '.database' "$STATE") == local ]]; then
        runuser -u "$ACCOUNT" -- "$program" "$@" "$target_db"
    else
        if [[ -n $rootcertificate ]]; then
            local -x PGSSLROOTCERT="$rootcertificate"
        fi
        PGPASSWORD=$password PGSSLMODE=$sslmode "$program" "$@" "$target_db"
    fi
}

printf '==> Dumping the database to %s\n' "$target/pulsar.dump"
pg pg_dump -Fc >"$target/pulsar.dump"
migrations=$(pg psql -X -qtA -v ON_ERROR_STOP=1 -c 'SELECT migration_id FROM "__EFMigrationsHistory" ORDER BY 1')

printf '==> Copying the keys, certificates and settings\n'
mkdir -m 0700 "$target/panel-data"
# The keys folder is the panel's user's: only its plain files with a key's name are copied, never a link or a name a
# restore would refuse (a compromised panel must not be able to spoil its own backups). The certificates' folders are root's.
mkdir -m 0700 "$target/panel-data/keys"
while IFS= read -r -d '' entry; do
    name=${entry##*/}
    if [[ -L $entry || ! -f $entry || ! $name =~ ^[A-Za-z0-9._-]+$ ]]; then
        printf 'WARNING: %s is not a plain key file: it is left out of the backup.\n' "$entry" >&2
        continue
    fi
    # -P: should the file become a link meanwhile, the link is copied (and refused at a restore), never followed.
    cp -P --no-preserve=ownership -- "$entry" "$target/panel-data/keys/$name"
done < <(find "$PANEL_DATA/keys" -mindepth 1 -maxdepth 1 -print0)
for folder in keyring tls; do
    [[ -d $PANEL_DATA/$folder ]] && cp -R --no-preserve=ownership -- "$PANEL_DATA/$folder" "$target/panel-data/"
done
install -m 0600 -- "$SETTINGS" "$target/appsettings.Production.json"
chown -R root:root "$target"
chmod -R go-rwx "$target"

printf '==> Writing backup.json\n'
version=$(jq -r '.version // empty' "$PANEL_DIR/release.json" 2>/dev/null || true)
[[ $version =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || {
    printf "WARNING: The panel's version could not be read, so backup.json does not name it.\n" >&2
    version=
}
files=$(cd "$target" && find . -type f ! -name backup.json -printf '%P\n' | sort | while IFS= read -r file; do
    printf '%s\t%s\n' "$file" "$(sha256sum -- "$file" | awk '{print $1}')"
done | jq -Rn '[inputs | split("\t") | {key: .[0], value: .[1]}] | from_entries')
jq -n --arg version "$version" --arg created "$(date -u +%FT%TZ)" --arg migrations "$migrations" --argjson files "$files" \
    '{format: 1, pulsarVersion: (if $version == "" then null else $version end), created: $created,
      migrations: ($migrations | split("\n") | map(select(. != ""))), files: $files}' >"$target/backup.json"
chmod 0600 "$target/backup.json"

trap - EXIT
printf 'Backup written to %s. Copy it somewhere safe, away from this machine.\n' "$target"
