#!/usr/bin/env bash
# Installs, updates or removes the Pulsar panel on this Linux machine, or renews its self-signed certificate
# (docs/guides/install.md, "Linux"). Run it as root from the folder of the release's Linux tarball
# (pulsar-<version>-linux-x64.tar.gz), which must be a folder only root can change: the script refuses any other.
#
#   sudo ./install-pulsar.sh                       install the panel (asks for the first administrator's password)
#   sudo ./install-pulsar.sh --update              replace the programs, keep settings and data (backup, check, roll back)
#   sudo ./install-pulsar.sh --restore-from DIR    install the panel from a backup (backup-pulsar.sh, or Windows' Backup-Pulsar.ps1)
#   sudo /opt/pulsar/install-pulsar.sh --renew-certificate   new self-signed certificate for this machine's addresses now
#   sudo /opt/pulsar/install-pulsar.sh --uninstall           remove the service and programs (keeps the database and data)
#
# Only the panel runs on Linux for now: its nodes (the machines that run game servers) are Windows machines with the
# agent, added from the panel (Nodes > Add node).
#
# The panel's database is created in this machine's PostgreSQL (15 or later; installed from the distribution's packages
# on Debian and Ubuntu when there is none), reached over its local socket as the system user 'pulsar' (peer
# authentication: no database password exists), unless PULSAR_CONNECTION_STRING names another database. Its HTTPS
# certificate is made here for this machine's names and private addresses (--host-name adds more), unless --certificate
# gives one.
#
# Secrets never go on the command line, which other users can read: they come from the environment
# (PULSAR_ADMIN_PASSWORD, PULSAR_CONNECTION_STRING, PULSAR_CERTIFICATE_PASSWORD), from --secrets-file (a JSON file only
# root can read: {"adminPassword": ..., "connectionString": ..., "certificatePassword": ...}), or are asked for on the
# terminal. Every run writes a log, without secrets, to /var/log/pulsar/setup.
#
# Exit codes, as Install-Pulsar.ps1's: 0 done; 1 failed (the message says what was left); 2 wrong or missing options,
# nothing changed; 3 this machine or folder cannot install Pulsar as it is, nothing changed; 6 the certificate cannot be
# used or renewed, nothing changed; 7 --update put the previous version back because the new one did not work.
set -Eeuo pipefail
# A failed command inside $(...) ends it too (bash clears -e there otherwise); a pipe whose reader stops early (head,
# grep -q) is never used on a long output, since pipefail would count the writer's SIGPIPE as a failure.
shopt -s inherit_errexit
umask 077
export LC_ALL=C
export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin

readonly EXIT_FAILED=1 EXIT_USAGE=2 EXIT_UNSUPPORTED=3 EXIT_CERTIFICATE=6 EXIT_ROLLED_BACK=7

readonly INSTALL_ROOT=/opt/pulsar
readonly PANEL_DIR=$INSTALL_ROOT/panel
readonly CONFIG_DIR=/etc/pulsar
readonly SETTINGS=$CONFIG_DIR/panel.json
readonly STATE=$CONFIG_DIR/install.json
readonly DATA_ROOT=/var/lib/pulsar
readonly PANEL_DATA=$DATA_ROOT/panel
readonly UPDATE_ROOT=$DATA_ROOT/updates
readonly LOG_ROOT=/var/log/pulsar
readonly PANEL_LOGS=$LOG_ROOT/panel
readonly SETUP_LOGS=$LOG_ROOT/setup
readonly SERVICE=pulsar-panel
readonly UNIT=/etc/systemd/system/$SERVICE.service
readonly ACCOUNT=pulsar
readonly DATABASE=pulsar
# What marks the 'pulsar' role in PostgreSQL as this script's (COMMENT ON ROLE), as Install-Pulsar.ps1 marks its own.
# One run at a time: a lock in /run, which only root may write (/run/lock is open to every user, who could take it first).
readonly LOCK_FILE=/run/pulsar-install.lock
readonly ROLE_COMMENT='Pulsar panel (made by install-pulsar.sh)'
readonly POSTGRES_MINIMUM_MAJOR=15
readonly UPDATE_BACKUPS_KEPT=3
readonly READY_MINUTES=3

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
readonly SCRIPT_DIR

# --- Options ---------------------------------------------------------------------------------------------------------
MODE=install
PORT=8443
AGENTS_PORT=0
SFTP_PORT=2022
HOST_NAMES=()
HOST_NAMES_GIVEN=false
CERTIFICATE_PATH=
ADMIN_USER='admin'
PUBLIC_IP=
NO_FIREWALL=false
RESTORE_FROM=
TRUST_RESTORE_SOURCE=false
SECRETS_FILE=${PULSAR_SECRETS_FILE:-}
UNATTENDED=false
DISCARD_INTERRUPTED_UPDATE=false

ADMIN_PASSWORD=${PULSAR_ADMIN_PASSWORD:-}
CONNECTION_STRING=${PULSAR_CONNECTION_STRING:-}
CERTIFICATE_PASSWORD=${PULSAR_CERTIFICATE_PASSWORD:-}
unset PULSAR_ADMIN_PASSWORD PULSAR_CONNECTION_STRING PULSAR_CERTIFICATE_PASSWORD

usage() {
    sed -n '2,/^set -Eeuo/p' "${BASH_SOURCE[0]}" | sed '$d; s/^# \{0,1\}//'
    cat <<'EOF'
Options:
  --port N                The panel's HTTPS port (default 8443).
  --agents-port N         A second HTTPS port for agents with client certificates (default: none).
  --sftp-port N           The panel's SFTP port (default 2022).
  --host-name NAME        Another name or IP address for the self-signed certificate (repeat it for more).
  --certificate FILE      A PFX certificate to use instead of a self-signed one (password: PULSAR_CERTIFICATE_PASSWORD).
  --admin-user NAME       The first administrator's user name (default admin).
  --public-ip IP          The panel's static public IP: a Let's Encrypt certificate for it, kept renewed (opens port 80).
  --no-firewall           Leave ufw and firewalld alone.
  --restore-from DIR      Install from a backup folder (with --trust-restore-source for one others could change).
  --secrets-file FILE     Secrets as JSON, readable by root only.
  --unattended            Never prompt: what is missing is an error.
  --update                Update the installed panel to this release.
  --discard-interrupted-update  With --update: go on when an earlier update was cut off and cannot be rolled back.
  --uninstall             Remove the panel's service and programs.
  --renew-certificate     Replace the self-signed certificate (names given at the install are kept).
  --help                  This text.
EOF
}

# --- Output, log and errors ------------------------------------------------------------------------------------------
LOG_FILE=
WORK=
CLEANUP_ACTIONS=()

step() { printf '==> %s\n' "$*"; }
warn() { printf 'WARNING: %s\n' "$*" >&2; }
fail() {
    local code=$1
    shift
    printf 'ERROR: %s\n' "$*" >&2
    exit "$code"
}

cleanup() {
    local action
    for action in "${CLEANUP_ACTIONS[@]}"; do eval "$action" || true; done
    if [[ -n $WORK && -d $WORK ]]; then rm -rf -- "$WORK"; fi
}
trap cleanup EXIT

start_log() {
    install -d -m 0755 -o root -g root "$LOG_ROOT"
    install -d -m 0700 -o root -g root "$SETUP_LOGS"
    LOG_FILE=$SETUP_LOGS/install-$(date -u +%Y%m%d-%H%M%S)-$$.log
    : >"$LOG_FILE"
    chmod 0600 "$LOG_FILE"
    # What is printed goes to the log too; secrets are never printed.
    exec > >(tee -a "$LOG_FILE") 2>&1
    step "Pulsar install script ($MODE), log: $LOG_FILE"
}

# A private folder in /run (memory, root only) for keys and secrets while the script runs.
make_work() {
    WORK=$(mktemp -d /run/pulsar-setup.XXXXXXXX)
    chmod 0700 "$WORK"
}

# A secret into a private file (for openssl -pass file:, curl -H @file), never through a command line.
secret_file() {
    local path=$WORK/$1
    printf '%s' "$2" >"$path"
    chmod 0600 "$path"
    printf '%s' "$path"
}

new_secret() {
    # base64url of random bytes: letters, digits, '-' and '_' only (safe in JSON and in a connection string).
    head -c "$1" /dev/urandom | base64 -w0 | tr '+/' '-_' | tr -d '='
}

has_control_characters() { [[ $1 == *[[:cntrl:]]* ]]; }

# --- Checks before anything changes ----------------------------------------------------------------------------------
require_root() {
    [[ $(id -u) -eq 0 ]] || fail "$EXIT_UNSUPPORTED" 'Run this script as root (sudo).'
}

require_system() {
    [[ -d /run/systemd/system ]] || fail "$EXIT_UNSUPPORTED" 'Pulsar runs as a systemd service, and this machine does not run systemd.'
    [[ $(uname -m) == x86_64 ]] || fail "$EXIT_UNSUPPORTED" "This release is for x86_64 machines, and this one is $(uname -m)."
}

# Whether a path, and every folder above it, can be changed only by root: owned by root and writable by nobody else
# (a sticky folder like /tmp lets others add files, but not rename or replace root's).
path_owned_by_root() {
    local path=$1 mode owner
    while :; do
        owner=$(stat -c %u -- "$path") || return 1
        mode=$(stat -c %a -- "$path") || return 1
        [[ $owner == 0 ]] || return 1
        if [[ -L $path ]]; then return 1; fi
        if (((8#$mode & 8#022) != 0)); then
            # A sticky folder above (like /tmp) lets others add entries there, not rename or replace root's.
            [[ $path != "$1" && -d $path ]] && (((8#$mode & 8#1000) != 0)) || return 1
        fi
        [[ $path == / ]] && return 0
        path=$(dirname -- "$path")
    done
}

# This script runs as root and installs what is next to it: nobody else may have been able to change it (the tarball's
# folder, its files, and the folders above it), as Install-Pulsar.ps1 checks its own folder.
require_trusted_location() {
    path_owned_by_root "$SCRIPT_DIR" || fail "$EXIT_UNSUPPORTED" \
        "$SCRIPT_DIR (or a folder above it) can be changed by another user than root. Extract the release as root into a folder only root can change, for example: sudo mkdir -m 700 /root/pulsar-release && sudo tar -xzf pulsar-<version>-linux-x64.tar.gz -C /root/pulsar-release --no-same-owner"
    local found
    found=$(find "$SCRIPT_DIR" \( ! -uid 0 -o -perm /022 -o -type l \) -print -quit)
    [[ -z $found ]] || fail "$EXIT_UNSUPPORTED" \
        "$found can be changed by another user than root (or is a link). Extract the release as root (tar --no-same-owner) into a folder only root can change."
}

require_release() {
    [[ -f $SCRIPT_DIR/panel/Pulsar.Api ]] ||
        fail "$EXIT_UNSUPPORTED" "$SCRIPT_DIR/panel is not the panel of a Pulsar release (Pulsar.Api is missing): run the script from the extracted release."
    [[ -f $SCRIPT_DIR/release.json ]] || fail "$EXIT_UNSUPPORTED" "$SCRIPT_DIR/release.json is missing: run the script from the extracted release."
}

release_version() { jq -r '.version // empty' "$1"; }

# Version a.b.c compared: prints -1, 0 or 1.
compare_versions() {
    local IFS=.
    local -a a b
    read -r -a a <<<"${1%%[-+]*}"
    read -r -a b <<<"${2%%[-+]*}"
    local i
    for i in 0 1 2; do
        if ((10#${a[i]:-0} < 10#${b[i]:-0})); then
            echo -1
            return
        fi
        if ((10#${a[i]:-0} > 10#${b[i]:-0})); then
            echo 1
            return
        fi
    done
    echo 0
}

valid_port() { [[ $1 =~ ^[0-9]{1,5}$ ]] && ((10#$1 >= 1 && 10#$1 <= 65535)); }

port_in_use() { [[ -n $(ss -Hltn "sport = :$1" 2>/dev/null) ]]; }

# --- Packages --------------------------------------------------------------------------------------------------------
APT_UPDATED=false
has_apt() { command -v apt-get >/dev/null 2>&1; }

apt_install() {
    if ! has_apt; then return 1; fi
    if [[ $APT_UPDATED == false ]]; then
        step 'Reading the package lists (apt-get update)'
        DEBIAN_FRONTEND=noninteractive apt-get update -qq
        APT_UPDATED=true
    fi
    step "Installing from the distribution's packages: $*"
    DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends "$@" >/dev/null
}

has_library() {
    local libraries
    libraries=$(ldconfig -p 2>/dev/null) || return 1
    grep -q "$1" <<<"$libraries"
}

# What the script and the panel need from the system: the tools, and the libraries .NET uses (ICU, OpenSSL 3).
require_tools() {
    local missing=() tool
    for tool in curl jq openssl ss ip flock runuser useradd tar sha256sum; do
        command -v "$tool" >/dev/null 2>&1 || missing+=("$tool")
    done
    local packages=()
    if ((${#missing[@]} > 0)); then
        for tool in "${missing[@]}"; do
            case $tool in
            curl) packages+=(curl ca-certificates) ;;
            jq) packages+=(jq) ;;
            openssl) packages+=(openssl) ;;
            ss | ip) packages+=(iproute2) ;;
            flock | runuser) packages+=(util-linux) ;;
            useradd) packages+=(passwd) ;;
            *) packages+=("$tool") ;;
            esac
        done
    fi
    if ! has_library 'libicuuc\.so\.'; then
        local icu
        icu=''
        if has_apt; then icu=$(apt-cache search --names-only '^libicu[0-9]+$' 2>/dev/null | awk '{print $1}' | sort -V | tail -1); fi
        packages+=("${icu:-libicu}")
    fi
    has_library 'libssl\.so\.3' || packages+=(libssl3)
    ((${#packages[@]} == 0)) && return 0
    mapfile -t packages < <(printf '%s\n' "${packages[@]}" | sort -u)
    if has_apt; then
        apt_install "${packages[@]}" || fail "$EXIT_UNSUPPORTED" "Could not install ${packages[*]} with apt-get."
    else
        fail "$EXIT_UNSUPPORTED" "Install these first with your distribution's package manager: ${packages[*]} (and ICU and OpenSSL 3, which .NET needs)."
    fi
}

# --- Secrets ---------------------------------------------------------------------------------------------------------
read_secrets_file() {
    [[ -z $SECRETS_FILE ]] && return 0
    [[ -f $SECRETS_FILE && ! -L $SECRETS_FILE ]] || fail "$EXIT_USAGE" "--secrets-file $SECRETS_FILE is not a file."
    path_owned_by_root "$SECRETS_FILE" || fail "$EXIT_USAGE" "--secrets-file $SECRETS_FILE, or a folder above it, can be changed by another user than root."
    local mode
    mode=$(stat -c %a -- "$SECRETS_FILE")
    (((8#$mode & 8#077) == 0)) || fail "$EXIT_USAGE" "--secrets-file $SECRETS_FILE can be read by other users than root (chmod 600 it)."
    jq -e 'type == "object"' "$SECRETS_FILE" >/dev/null 2>&1 || fail "$EXIT_USAGE" "--secrets-file $SECRETS_FILE is not a JSON object."
    local value
    value=$(jq -r '.adminPassword // empty' "$SECRETS_FILE")
    [[ -n $value ]] && ADMIN_PASSWORD=$value
    value=$(jq -r '.connectionString // empty' "$SECRETS_FILE")
    [[ -n $value ]] && CONNECTION_STRING=$value
    value=$(jq -r '.certificatePassword // empty' "$SECRETS_FILE")
    [[ -n $value ]] && CERTIFICATE_PASSWORD=$value
    return 0
}

read_secret() {
    local prompt=$1 first second
    [[ $UNATTENDED == true ]] && fail "$EXIT_USAGE" "$prompt: not given (--unattended asks for nothing; use the environment or --secrets-file)."
    { : </dev/tty; } 2>/dev/null || fail "$EXIT_USAGE" "$prompt: not given, and there is no terminal to ask on (use the environment or --secrets-file)."
    while :; do
        IFS= read -r -s -p "$prompt: " first </dev/tty
        printf '\n' >/dev/tty
        IFS= read -r -s -p 'Again: ' second </dev/tty
        printf '\n' >/dev/tty
        [[ $first == "$second" ]] && break
        printf 'They differ; once more.\n' >/dev/tty
    done
    REPLY=$first
}

# --- PostgreSQL ------------------------------------------------------------------------------------------------------
PG_SOCKET=
PG_PORT=

pg_super() { runuser -u postgres -- psql -X -v ON_ERROR_STOP=1 -qtA "$@"; }

local_postgres_running() {
    id postgres >/dev/null 2>&1 && command -v psql >/dev/null 2>&1 && pg_super -c 'SELECT 1' >/dev/null 2>&1
}

# The local PostgreSQL the panel's database goes into: installed from the distribution's packages when there is none,
# 15 or later, its socket folder and port noted.
prepare_local_postgres() {
    if ! local_postgres_running; then
        if ! id postgres >/dev/null 2>&1 || ! command -v psql >/dev/null 2>&1; then
            has_apt || fail "$EXIT_UNSUPPORTED" "This machine has no PostgreSQL, and this script installs one only with apt-get (Debian, Ubuntu). Install PostgreSQL $POSTGRES_MINIMUM_MAJOR or later and run the script again, or give PULSAR_CONNECTION_STRING."
            apt_install postgresql
        fi
        systemctl enable --now postgresql >/dev/null 2>&1 || true
        local i
        for i in $(seq 1 30); do
            local_postgres_running && break
            sleep 1
        done
        local_postgres_running || fail "$EXIT_UNSUPPORTED" 'PostgreSQL is installed but does not answer as the postgres user over its local socket: start it (systemctl start postgresql) and run the script again.'
    fi
    local version
    version=$(pg_super -c 'SHOW server_version_num')
    if ((version / 10000 < POSTGRES_MINIMUM_MAJOR)); then
        fail "$EXIT_UNSUPPORTED" "Pulsar needs PostgreSQL $POSTGRES_MINIMUM_MAJOR or later, and this machine runs $(pg_super -c 'SHOW server_version'). Install a newer one (PostgreSQL's own apt repository has every version: https://www.postgresql.org/download/linux/) or give PULSAR_CONNECTION_STRING."
    fi
    PG_PORT=$(pg_super -c 'SHOW port')
    PG_SOCKET=$(pg_super -c 'SHOW unix_socket_directories' | cut -d, -f1 | sed 's/^ *//; s/ *$//')
    [[ $PG_SOCKET == /* ]] || fail "$EXIT_UNSUPPORTED" "PostgreSQL listens on no local socket folder ($PG_SOCKET): the panel reaches it there."
    step "PostgreSQL $(pg_super -c 'SHOW server_version' | awk '{print $1}') (socket $PG_SOCKET, port $PG_PORT)"
    check_postgres_rules
}

# The panel's role has no password: only peer authentication (the system user 'pulsar' itself) may let anyone sign in
# as it. A pg_hba.conf rule that could let another local user or a remote one in as 'pulsar', or into its database,
# without proving who they are (trust, a user name map, PAM, LDAP, RADIUS and the like) stops the install. Rules that
# ask for a password are harmless: the role has none. Every rule is looked at, whatever its order.
check_postgres_rules() {
    local rules
    rules=$(pg_super -F '|' -c "SELECT line_number, type, auth_method, coalesce(array_to_string(options, ' '), '')
        FROM pg_hba_file_rules
        WHERE error IS NULL
          AND database && ARRAY['all', 'sameuser', 'samerole', '$DATABASE']
          AND user_name && ARRAY['all', '$ACCOUNT']
          AND (auth_method NOT IN ('peer', 'scram-sha-256', 'md5', 'password', 'reject')
               OR coalesce(array_to_string(options, ' '), '') LIKE '%map=%')
        ORDER BY line_number") ||
        fail "$EXIT_UNSUPPORTED" "PostgreSQL's pg_hba.conf rules cannot be read (pg_hba_file_rules). Nothing was changed."
    if [[ -n $rules ]]; then
        fail "$EXIT_UNSUPPORTED" "PostgreSQL's pg_hba.conf has rules that would let others sign in as '$ACCOUNT' (which has no password) without proving who they are: $(tr '\n' ';' <<<"$rules" | sed 's/;$//') (line|type|method|options). Use peer for local connections and scram-sha-256 for the others, reload PostgreSQL and run the script again. Nothing was changed."
    fi
}

local_connection_string() {
    printf 'Host=%s;Port=%s;Database=%s;Username=%s' "$PG_SOCKET" "$PG_PORT" "$DATABASE" "$ACCOUNT"
}

role_exists() { [[ $(pg_super -c "SELECT 1 FROM pg_roles WHERE rolname = '$ACCOUNT'") == 1 ]]; }
database_exists() { [[ $(pg_super -c "SELECT 1 FROM pg_database WHERE datname = '$DATABASE'") == 1 ]]; }

assert_no_panel_database() {
    if database_exists; then
        fail "$EXIT_USAGE" "PostgreSQL already has a '$DATABASE' database: an earlier panel's. Restore it with --restore-from and its backup, or remove it to start over (docs/guides/install.md, \"Starting over\"). Nothing was changed."
    fi
    if role_exists && [[ $(pg_super -c "SELECT shobj_description(oid, 'pg_authid') FROM pg_roles WHERE rolname = '$ACCOUNT'") != "$ROLE_COMMENT" ]]; then
        fail "$EXIT_USAGE" "PostgreSQL already has a '$ACCOUNT' role that this script did not make. Nothing was changed."
    fi
}

MADE_DATABASE=false
create_panel_database() {
    step "Creating the '$DATABASE' database and role in the local PostgreSQL (peer authentication: no password)"
    if ! role_exists; then
        pg_super -c "CREATE ROLE $ACCOUNT LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION"
        pg_super -c "COMMENT ON ROLE $ACCOUNT IS '$ROLE_COMMENT'"
    fi
    pg_super -c "CREATE DATABASE $DATABASE OWNER $ACCOUNT ENCODING 'UTF8' TEMPLATE template0"
    MADE_DATABASE=true
    local answer
    answer=$(runuser -u "$ACCOUNT" -- psql -X -qtA -c 'SELECT 1' "host=$PG_SOCKET port=$PG_PORT dbname=$DATABASE user=$ACCOUNT" 2>&1) || true
    [[ $answer == 1 ]] || fail "$EXIT_FAILED" "The system user '$ACCOUNT' cannot sign in to the '$DATABASE' database over the local socket ($answer). PostgreSQL's pg_hba.conf must allow it (the default 'local all all peer' does)."
}

drop_panel_database() {
    pg_super -c "DROP DATABASE IF EXISTS $DATABASE WITH (FORCE)" || return 1
    if role_exists && [[ $(pg_super -c "SELECT shobj_description(oid, 'pg_authid') FROM pg_roles WHERE rolname = '$ACCOUNT'") == "$ROLE_COMMENT" ]]; then
        pg_super -c "DROP ROLE $ACCOUNT" || return 1
    fi
}

# The panel's database as libpq reads it, for pg_dump, pg_restore and psql (backups and updates): a key=value string,
# and PGPASSWORD/PGSSLMODE in the environment of the call (never on a command line).
pg_target() {
    local connection=$1 key value part
    local host='' port='' database='' user='' password='' sslmode=''
    local IFS=';'
    for part in $connection; do
        key=${part%%=*}
        value=${part#*=}
        key=$(printf '%s' "$key" | tr '[:upper:]' '[:lower:]' | tr -d ' ')
        case $key in
        host | server) host=$value ;;
        port) port=$value ;;
        database | db) database=$value ;;
        username | user | userid) user=$value ;;
        password | pwd) password=$value ;;
        sslmode) sslmode=$(printf '%s' "$value" | tr '[:upper:]' '[:lower:]') ;;
        esac
    done
    PG_TARGET="host=$host port=${port:-5432} dbname=$database user=$user"
    PG_PASSWORD=$password
    case $sslmode in
    require | verifyca | verify-ca) PG_SSLMODE=require ;;
    verifyfull | verify-full) PG_SSLMODE=verify-full ;;
    disable) PG_SSLMODE=disable ;;
    *) PG_SSLMODE=prefer ;;
    esac
}

# Runs a PostgreSQL client program on the panel's database: as 'pulsar' over the local socket (peer), or with the
# connection string's password in the environment.
pg_panel() {
    local program=$1
    shift
    pg_target "$(jq -r '.ConnectionStrings.Pulsar' "$SETTINGS")"
    if [[ $(jq -r '.database // "local"' "$STATE" 2>/dev/null || echo local) == local ]]; then
        runuser -u "$ACCOUNT" -- "$program" "$@" "$PG_TARGET"
    else
        PGPASSWORD=$PG_PASSWORD PGSSLMODE=$PG_SSLMODE "$program" "$@" "$PG_TARGET"
    fi
}

# The migrations the panel's database has applied (EF Core's history table, in the panel's snake_case naming); fails
# when it cannot be read.
applied_migrations() {
    pg_panel psql -X -qtA -v ON_ERROR_STOP=1 -c 'SELECT migration_id FROM "__EFMigrationsHistory" ORDER BY 1'
}

# The migration ids a panel's programs carry, read from its Pulsar.Infrastructure.dll as Install-Pulsar.ps1 reads them.
shipped_migrations() {
    grep -aoE '[0-9]{14}_[A-Za-z][A-Za-z0-9_]*' "$1/Pulsar.Infrastructure.dll" 2>/dev/null | sort -u || true
}

# --- Certificates ----------------------------------------------------------------------------------------------------
CERT_NAMES=()
CERT_IPS=()

valid_host_name() {
    local text=$1
    [[ $text =~ ^((\*\.)?[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]+)*\.?|[0-9A-Fa-f.]*:[0-9A-Fa-f:.]*)$ ]] || return 1
    return 0
}

# Whether a --host-name is an IP address (printed back) or a name (nothing printed); fails on neither, as
# ConvertTo-CertificateAddress does.
certificate_address() {
    local text=$1 octet='(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])'
    if [[ $text =~ ^$octet(\.$octet){3}$ ]]; then
        local first=${text%%.*}
        if ((first == 0 || first >= 224)); then
            echo "$text is not an address a server has: give the one other computers reach the panel by." >&2
            return 1
        fi
        printf '%s' "$text"
        return 0
    fi
    if [[ $text == *:* ]]; then
        if ! valid_ipv6 "$text"; then
            echo "$text is not an IPv6 address (give it without brackets, a zone or a port, like 2001:db8::20)." >&2
            return 1
        fi
        local lower=${text,,}
        if [[ $lower == ::ffff:* ]]; then
            echo "$text is an IPv4 address written as IPv6: give it as IPv4." >&2
            return 1
        fi
        if [[ $lower == :: || $lower == ff* ]]; then
            echo "$text is not an address a server has: give the one other computers reach the panel by." >&2
            return 1
        fi
        printf '%s' "$lower"
        return 0
    fi
    local last=${text%.}
    last=${last##*.}
    if [[ ${last,,} =~ ^(0x[0-9a-f]*|[0-9]+)$ ]]; then
        echo "$text is neither a name (its last part is a number) nor an IPv4 address: write an address as four numbers from 0 to 255, like 192.168.1.20." >&2
        return 1
    fi
    return 0
}

# Whether text is an IPv6 address as written in a certificate request: eight groups of up to four hex digits, one '::'
# at most for the zeros left out, and an IPv4 address in place of the last two groups allowed.
valid_ipv6() {
    local text=${1,,} part count=0
    local -a parts
    [[ $text =~ ^[0-9a-f:.]+$ && $text == *:* && $text != *::*::* && $text != *:::* ]] || return 1
    [[ $text == :* && $text != ::* ]] && return 1
    [[ $text == *: && $text != *:: ]] && return 1
    if [[ $text == *.* ]]; then
        local octet='(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])'
        [[ ${text##*:} =~ ^$octet(\.$octet){3}$ ]] || return 1
        text=${text%:*}:0:0
    fi
    IFS=: read -r -a parts <<<"${text/::/:}"
    for part in "${parts[@]}"; do
        [[ -z $part ]] && continue
        [[ $part =~ ^[0-9a-f]{1,4}$ ]] || return 1
        count=$((count + 1))
    done
    if [[ $text == *::* ]]; then ((count <= 7)); else ((count == 8)); fi
}

private_ipv4() {
    local a b
    IFS=. read -r a b _ _ <<<"$1"
    ((a == 10 || (a == 172 && b >= 16 && b <= 31) || (a == 192 && b == 168)))
}

# This machine's private IPv4 addresses (RFC 1918) on interfaces that have a default route (a router), as
# Select-LanAddresses picks them: not the virtual networks that reach only this machine's containers or VMs.
lan_addresses() {
    local devices line device address
    devices=$(ip -4 route show default 2>/dev/null | awk '{for (i = 1; i < NF; i++) if ($i == "dev") print $(i + 1)}' | sort -u)
    [[ -z $devices ]] && return 0
    while read -r line; do
        device=$(awk '{print $2}' <<<"$line")
        address=$(awk '{print $4}' <<<"$line")
        address=${address%%/*}
        grep -qxF -- "$device" <<<"$devices" || continue
        [[ $line == *tentative* || $line == *dadfailed* ]] && continue
        private_ipv4 "$address" && printf '%s %s\n' "$address" "$([[ $line == *dynamic* ]] && echo dynamic || echo static)"
    done < <(ip -4 -o addr show scope global 2>/dev/null)
}

# What the self-signed certificate lists: localhost, this machine's names, the --host-name names; 127.0.0.1, ::1, its
# private addresses, and the --host-name addresses.
certificate_names() {
    CERT_NAMES=(localhost)
    CERT_IPS=(127.0.0.1 ::1)
    local short full name address lan_line dynamic=()
    short=$(hostname -s 2>/dev/null || hostname)
    full=$(hostname -f 2>/dev/null || true)
    [[ -n $short && $short != localhost ]] && CERT_NAMES+=("$short")
    [[ -n $full && $full == *.* && $full != "$short" ]] && CERT_NAMES+=("$full")
    while read -r lan_line; do
        [[ -z $lan_line ]] && continue
        CERT_IPS+=("${lan_line%% *}")
        [[ $lan_line == *dynamic ]] && dynamic+=("${lan_line%% *}")
    done < <(lan_addresses)
    local lan_count=$((${#CERT_IPS[@]} - 2))
    for name in "${HOST_NAMES[@]}"; do
        address=$(certificate_address "$name") || fail "$EXIT_USAGE" "--host-name: see above. Nothing was changed."
        if [[ -n $address ]]; then CERT_IPS+=("$address"); else CERT_NAMES+=("$name"); fi
    done
    mapfile -t CERT_NAMES < <(printf '%s\n' "${CERT_NAMES[@]}" | awk '!seen[$0]++')
    mapfile -t CERT_IPS < <(printf '%s\n' "${CERT_IPS[@]}" | awk '!seen[$0]++')
    step "A self-signed HTTPS certificate for ${CERT_NAMES[*]} ${CERT_IPS[*]}"
    if ((lan_count == 0)); then
        warn 'This machine has no private IPv4 address on a network with a router, so the certificate lists none: other computers must use its name (or pass --host-name with the address they use).'
    fi
    if ((${#dynamic[@]} > 0)); then
        warn "A DHCP server gave this machine ${dynamic[*]}, which the certificate lists and which may change: reserve it in the router (a DHCP reservation), or renew the certificate when it changes (--renew-certificate)."
    fi
}

# Makes the self-signed certificate (RSA 2048, five years, CA:FALSE, server authentication only, as Install-Pulsar.ps1
# makes it) as $1.pfx (password in file $2) with its $1.cer (DER) and $1.crt (PEM). The key exists only in $WORK.
make_self_signed() {
    local base=$1 password_file=$2 config=$WORK/cert.cnf san='' i=1 j=1 name address
    for name in "${CERT_NAMES[@]}"; do
        san+="DNS.$i = $name"$'\n'
        i=$((i + 1))
    done
    for address in "${CERT_IPS[@]}"; do
        san+="IP.$j = $address"$'\n'
        j=$((j + 1))
    done
    cat >"$config" <<EOF
[req]
distinguished_name = dn
x509_extensions = panel
prompt = no
[dn]
CN = localhost
[panel]
basicConstraints = critical, CA:FALSE
keyUsage = critical, digitalSignature, keyEncipherment
extendedKeyUsage = serverAuth
subjectKeyIdentifier = hash
subjectAltName = @names
[names]
$san
EOF
    openssl req -x509 -config "$config" -newkey rsa:2048 -sha256 -days 1826 -nodes \
        -keyout "$WORK/panel.key" -out "$base.crt" 2>/dev/null || fail "$EXIT_FAILED" 'openssl could not make the self-signed certificate.'
    openssl pkcs12 -export -inkey "$WORK/panel.key" -in "$base.crt" -name 'Pulsar panel' \
        -passout "file:$password_file" -out "$base.pfx" || fail "$EXIT_FAILED" 'openssl could not write the certificate as PFX.'
    rm -f -- "$WORK/panel.key"
    openssl x509 -in "$base.crt" -outform DER -out "$base.cer"
    check_server_certificate "$base.crt" true
}

# A certificate trusted by browsers and pinned by agents must not be able to issue certificates, and is for a web
# server's identity only (as New-SelfSignedPanelBundle checks the one it made).
check_server_certificate() {
    local crt=$1 strict=$2 text
    text=$(openssl x509 -in "$crt" -noout -text)
    if [[ $strict == true ]]; then
        grep -q 'CA:FALSE' <<<"$text" || fail "$EXIT_CERTIFICATE" 'The self-signed certificate came out able to issue certificates.'
        [[ $(grep -A1 'Extended Key Usage' <<<"$text" | tail -1 | sed 's/^ *//') == 'TLS Web Server Authentication' ]] ||
            fail "$EXIT_CERTIFICATE" 'The self-signed certificate came out usable for more than a web server.'
    fi
}

make_key_ring_certificate() {
    local pfx=$1 password_file=$2 config=$WORK/keyring.cnf
    cat >"$config" <<'EOF'
[req]
distinguished_name = dn
x509_extensions = keyring
prompt = no
[dn]
CN = Pulsar panel key ring
[keyring]
basicConstraints = critical, CA:FALSE
keyUsage = critical, keyEncipherment, dataEncipherment
EOF
    openssl req -x509 -config "$config" -newkey rsa:3072 -sha256 -days 36500 -nodes \
        -keyout "$WORK/keyring.key" -out "$WORK/keyring.crt" 2>/dev/null || fail "$EXIT_FAILED" 'openssl could not make the key ring certificate.'
    openssl pkcs12 -export -inkey "$WORK/keyring.key" -in "$WORK/keyring.crt" -name 'Pulsar panel key ring' \
        -passout "file:$password_file" -out "$pfx" || fail "$EXIT_FAILED" 'openssl could not write the key ring certificate as PFX.'
    rm -f -- "$WORK/keyring.key" "$WORK/keyring.crt"
}

# The PEM certificate in a PFX (password in a file), or nothing when it does not open (OpenSSL 3 needs -legacy for
# PFX files made with older algorithms).
pfx_certificate() {
    local pfx=$1 password_file=$2
    openssl pkcs12 -in "$pfx" -passin "file:$password_file" -nokeys -clcerts 2>/dev/null ||
        openssl pkcs12 -legacy -in "$pfx" -passin "file:$password_file" -nokeys -clcerts 2>/dev/null
}

pfx_has_key() {
    local pfx=$1 password_file=$2
    { openssl pkcs12 -in "$pfx" -passin "file:$password_file" -nocerts -nodes 2>/dev/null ||
        openssl pkcs12 -legacy -in "$pfx" -passin "file:$password_file" -nocerts -nodes 2>/dev/null; } | grep -q 'PRIVATE KEY'
}

certificate_sha256() { openssl x509 -in "$1" -outform DER | sha256sum | awk '{print $1}'; }

# curl's --pinnedpubkey for a certificate: the SHA-256 of its public key.
certificate_pin() {
    printf 'sha256//%s' "$(openssl x509 -in "$1" -noout -pubkey | openssl pkey -pubin -outform DER | openssl dgst -sha256 -binary | base64 -w0)"
}

format_fingerprint() { sed 's/../&:/g; s/:$//' <<<"${1^^}"; }

# --- Folders, account, settings, service -----------------------------------------------------------------------------
ensure_account() {
    if ! id "$ACCOUNT" >/dev/null 2>&1; then
        step "Creating the system user '$ACCOUNT' the panel runs as"
        useradd --system --user-group --home-dir "$DATA_ROOT" --no-create-home --shell /usr/sbin/nologin \
            --comment 'Pulsar panel' "$ACCOUNT"
    fi
}

# The panel's folders, with their owners and permissions set whether they existed or not: a folder another user made
# first must not stay theirs.
ensure_folders() {
    install -d -m 0755 -o root -g root "$INSTALL_ROOT" "$DATA_ROOT" "$LOG_ROOT"
    install -d -m 0750 -o root -g "$ACCOUNT" "$CONFIG_DIR" "$PANEL_DATA" "$PANEL_DATA/tls" "$PANEL_DATA/keyring"
    # A keys folder an earlier panel left is the panel's user's: root never writes into it (a link there would take the
    # write elsewhere) and a new panel's key ring could not read its keys anyway. It is moved aside, by a rename.
    if [[ -e $PANEL_DATA/keys || -L $PANEL_DATA/keys ]] && [[ $MODE == install ]]; then
        local aside
        aside=$PANEL_DATA/keys.before-$(date -u +%Y%m%d-%H%M%S)
        mv -- "$PANEL_DATA/keys" "$aside"
        chown -h root:root "$aside"
        chmod 0700 "$aside"
        warn "The keys an earlier panel left in $PANEL_DATA/keys are in $aside now (they protect nothing without that panel's database)."
    fi
    # Root's while the install fills it (a restore's keys); given to the panel's user before the panel starts.
    install -d -m 0700 -o root -g root "$PANEL_DATA/keys"
    install -d -m 0750 -o "$ACCOUNT" -g "$ACCOUNT" "$PANEL_LOGS"
    install -d -m 0700 -o root -g root "$UPDATE_ROOT" "$UPDATE_ROOT/backups"
}

# Copies a release's panel into a new folder next to $PANEL_DIR, owned by root, readable by everyone (the programs hold
# no secret), changeable by root only; prints its path.
stage_programs() {
    local staged
    staged=$(mktemp -d "$INSTALL_ROOT/.panel-new.XXXXXX")
    cp -R --no-preserve=mode,ownership -- "$SCRIPT_DIR/panel/." "$staged/"
    cp -- "$SCRIPT_DIR/release.json" "$staged/release.json"
    chown -R root:root "$staged"
    find "$staged" -type d -exec chmod 0755 {} +
    find "$staged" -type f -exec chmod 0644 {} +
    chmod 0755 "$staged/Pulsar.Api"
    [[ -f $staged/createdump ]] && chmod 0755 "$staged/createdump"
    printf '%s' "$staged"
}

install_scripts() {
    local script
    for script in install-pulsar.sh backup-pulsar.sh; do
        [[ -f $SCRIPT_DIR/$script ]] && install -m 0700 -o root -g root "$SCRIPT_DIR/$script" "$INSTALL_ROOT/$script"
    done
    return 0
}

# Writes the panel's settings ($1, a JSON text) to $SETTINGS: root may change them, the panel may read them (they hold
# the certificates' passwords), nobody else. Written next to it and moved into place with its permissions.
write_settings() {
    local staged
    staged=$(mktemp "$CONFIG_DIR/.panel.json.XXXXXX")
    printf '%s\n' "$1" >"$staged"
    chown root:"$ACCOUNT" "$staged"
    chmod 0640 "$staged"
    mv -f -- "$staged" "$SETTINGS"
}

write_state() {
    local staged
    staged=$(mktemp "$CONFIG_DIR/.install.json.XXXXXX")
    printf '%s\n' "$1" >"$staged"
    chmod 0644 "$staged"
    mv -f -- "$staged" "$STATE"
}

write_unit() {
    local staged
    staged=$(mktemp /etc/systemd/system/.pulsar-panel.XXXXXX)
    cat >"$staged" <<EOF
# Made by install-pulsar.sh (docs/guides/install.md, "Linux"); an update writes it again.
[Unit]
Description=Pulsar game server panel
Documentation=https://github.com/Ayrton09/Pulsar/blob/main/docs/guides/install.md
After=network-online.target postgresql.service
Wants=network-online.target

[Service]
# Ready once it listens: .NET tells systemd (it finds systemd as its parent in /proc, so no ProtectProc=invisible).
Type=notify
User=$ACCOUNT
Group=$ACCOUNT
WorkingDirectory=$PANEL_DIR
ExecStart=$PANEL_DIR/Pulsar.Api
Environment=PULSAR_SETTINGS_FILE=$SETTINGS
Environment=DOTNET_NOLOGO=1
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
Restart=on-failure
RestartSec=5
TimeoutStartSec=300
TimeoutStopSec=30
UMask=0077
# Ports below 1024 (Let's Encrypt's check on port 80, or HTTPS on 443) without running as root.
AmbientCapabilities=CAP_NET_BIND_SERVICE
CapabilityBoundingSet=CAP_NET_BIND_SERVICE
NoNewPrivileges=true
ProtectSystem=strict
ReadWritePaths=$PANEL_DATA/keys $PANEL_LOGS
ProtectHome=true
PrivateTmp=true
PrivateDevices=true
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectKernelLogs=true
ProtectControlGroups=true
ProtectClock=true
ProtectHostname=true
RestrictSUIDSGID=true
RestrictRealtime=true
RestrictNamespaces=true
LockPersonality=true
RemoveIPC=true
RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6 AF_NETLINK
SystemCallArchitectures=native
SystemCallFilter=@system-service
SystemCallErrorNumber=EPERM

[Install]
WantedBy=multi-user.target
EOF
    chmod 0644 "$staged"
    mv -f -- "$staged" "$UNIT"
    systemctl daemon-reload
}

# Runs the panel's program as its user with a clean environment (nothing of root's: no secret of this run reaches it).
run_panel_program() {
    (cd "$PANEL_DIR" && runuser -u "$ACCOUNT" -- env -i PATH=/usr/sbin:/usr/bin:/sbin:/bin LANG=C.UTF-8 \
        HOME="$DATA_ROOT" DOTNET_NOLOGO=1 PULSAR_SETTINGS_FILE="$SETTINGS" ./Pulsar.Api "$@")
}

migrate_database() {
    step 'Preparing the database'
    local output
    if ! output=$(run_panel_program migrate 2>&1); then
        printf '%s\n' "$output" | tail -20 >&2
        return 1
    fi
}

panel_journal_tail() { journalctl -u "$SERVICE" -n 25 --no-pager 2>/dev/null || true; }

start_panel() {
    step 'Starting the panel'
    systemctl enable "$SERVICE" >/dev/null 2>&1
    if ! systemctl restart "$SERVICE"; then
        panel_journal_tail >&2
        return 1
    fi
}

# --- Talking to the panel (curl, pinned to its certificate's key) -----------------------------------------------------
PIN=
JAR=
HEADERS=

panel_client() {
    PIN=$1
    JAR=$WORK/cookies
    HEADERS=$WORK/headers
    : >"$JAR"
    : >"$HEADERS"
}

# Sends a request to the panel on this machine; the body (JSON) comes on stdin when $3 is "body". Prints the answer,
# and fails on a status that is not 2xx. The XSRF token goes in a header file, never on a command line.
panel_call() {
    local method=$1 path=$2 with_body=${3:-} status
    local args=(-sS --max-time 30 --insecure --pinnedpubkey "$PIN" -b "$JAR" -c "$JAR" -H @"$HEADERS"
        -X "$method" -o "$WORK/answer" -w '%{http_code}')
    if [[ $with_body == body ]]; then args+=(-H 'Content-Type: application/json' --data-binary @-); fi
    status=$(curl "${args[@]}" "https://localhost:$PORT$path") || status=000
    local token
    token=$(awk '$6 == "__Host-pulsar-xsrf" || $6 == "pulsar-xsrf" {print $7}' "$JAR" | tail -1)
    if [[ -n $token ]]; then printf 'X-XSRF-TOKEN: %b\n' "${token//%/\\x}" >"$HEADERS"; fi
    if [[ $status != 2* ]]; then
        printf 'The panel answered %s %s with %s: %s\n' "$method" "$path" "$status" "$(head -c 400 "$WORK/answer" 2>/dev/null)" >&2
        return 1
    fi
    cat "$WORK/answer"
}

wait_panel_ready() {
    local deadline=$((SECONDS + READY_MINUTES * 60))
    while ((SECONDS < deadline)); do
        if curl -fsS --max-time 10 --insecure --pinnedpubkey "$PIN" -o /dev/null "https://localhost:$PORT/health/ready" 2>/dev/null; then
            return 0
        fi
        if ! systemctl is-active --quiet "$SERVICE" && [[ $(systemctl show -p ActiveState --value "$SERVICE") == failed ]]; then
            echo 'The panel service stopped:' >&2
            panel_journal_tail >&2
            return 1
        fi
        sleep 2
    done
    echo "The panel did not become ready in $READY_MINUTES minutes:" >&2
    panel_journal_tail >&2
    return 1
}

# --- Firewall --------------------------------------------------------------------------------------------------------
FIREWALL_RULES=()

firewall_tool() {
    if command -v ufw >/dev/null 2>&1 && [[ $(ufw status 2>/dev/null || true) == *'Status: active'* ]]; then
        echo ufw
    elif command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --state >/dev/null 2>&1; then
        echo firewalld
    fi
}

open_port() {
    local port=$1 label=$2 tool
    tool=$(firewall_tool)
    # A port the firewall allows already is left as it is, and never removed by --uninstall: that rule is not ours.
    case $tool in
    ufw)
        if grep -qE "^$port/tcp[[:space:]]+ALLOW" <<<"$(ufw status)"; then
            step "TCP port $port ($label) is allowed in ufw already"
            return 0
        fi
        step "Allowing TCP port $port ($label) in ufw"
        ufw allow "$port/tcp" comment "Pulsar panel ($label)" >/dev/null
        FIREWALL_RULES+=("ufw:$port")
        ;;
    firewalld)
        if firewall-cmd --quiet --query-port="$port/tcp" && firewall-cmd --quiet --permanent --query-port="$port/tcp"; then
            step "TCP port $port ($label) is allowed in firewalld already"
            return 0
        fi
        step "Allowing TCP port $port ($label) in firewalld"
        firewall-cmd --quiet --permanent --add-port="$port/tcp"
        firewall-cmd --quiet --add-port="$port/tcp"
        FIREWALL_RULES+=("firewalld:$port")
        ;;
    *)
        warn "No ufw or firewalld is active here: if another firewall guards this machine, allow TCP port $port ($label) in it."
        ;;
    esac
}

close_ports() {
    local rule tool port
    for rule in "$@"; do
        tool=${rule%%:*}
        port=${rule#*:}
        valid_port "$port" || continue
        case $tool in
        ufw) ufw delete allow "$port/tcp" >/dev/null 2>&1 || true ;;
        firewalld)
            firewall-cmd --quiet --permanent --remove-port="$port/tcp" >/dev/null 2>&1 || true
            firewall-cmd --quiet --remove-port="$port/tcp" >/dev/null 2>&1 || true
            ;;
        esac
    done
}

# --- Backups (as backup-pulsar.sh writes them, and Install-Pulsar.ps1 reads them) -------------------------------------
RESTORE_COPY=

# Copies the backup folder into the private work folder, never through a link, and checks it against its backup.json:
# every file listed with its SHA-256, nothing more; a backup of a newer Pulsar, or of a database with migrations this
# release does not ship, is refused. Only the copy is read afterwards.
copy_restore_source() {
    local source=$1
    [[ -d $source && ! -L $source ]] || fail "$EXIT_USAGE" "--restore-from $source is not a folder. Nothing was changed."
    source=$(cd -- "$source" && pwd -P)
    if [[ $TRUST_RESTORE_SOURCE != true ]]; then
        path_owned_by_root "$source" || fail "$EXIT_USAGE" "--restore-from $source, or a folder above it, can be changed by another user than root (the backup holds the panel's keys and a database dump restored as root). Move it to a folder only root can change, or pass --trust-restore-source if nobody else could have touched it. Nothing was changed."
        local found
        found=$(find "$source" \( ! -uid 0 -o -perm /022 \) -print -quit)
        [[ -z $found ]] || fail "$EXIT_USAGE" "$found, in the backup, can be changed by another user than root. Nothing was changed."
    fi
    local link
    link=$(find "$source" -type l -print -quit)
    [[ -z $link ]] || fail "$EXIT_USAGE" "The backup holds a link ($link): it is not restored. Nothing was changed."
    RESTORE_COPY=$WORK/restore
    mkdir -m 0700 "$RESTORE_COPY"
    cp -R --no-preserve=mode,ownership -- "$source/." "$RESTORE_COPY/"
    chown -R root:root "$RESTORE_COPY"

    local manifest=$RESTORE_COPY/backup.json
    [[ -f $manifest ]] || fail "$EXIT_USAGE" "$source has no backup.json: it is not a backup this script restores (backup-pulsar.sh or Backup-Pulsar.ps1 writes one). Nothing was changed."
    jq -e '(.format | tostring) == "1" and (.files | type == "object") and has("pulsarVersion") and has("migrations")' "$manifest" >/dev/null 2>&1 ||
        fail "$EXIT_USAGE" "backup.json is not one this script reads. Nothing was changed."
    local listed actual problems=()
    listed=$(jq -r '.files | to_entries[] | "\(.value | ascii_downcase)  \(.key)"' "$manifest")
    while read -r line; do
        [[ -z $line ]] && continue
        local path=${line#*  }
        if [[ ! $path =~ ^[A-Za-z0-9._\ -]+(/[A-Za-z0-9._\ -]+)*$ || /$path/ == */./* || /$path/ == */../* ]]; then
            fail "$EXIT_USAGE" 'backup.json lists a file whose name is not one of a backup'"'"'s. Nothing was changed.'
        fi
    done <<<"$listed"
    if ! (cd "$RESTORE_COPY" && sha256sum --quiet -c - <<<"$listed" >/dev/null 2>&1); then
        problems+=('a file is missing or is not what backup.json says (its SHA-256 differs)')
    fi
    actual=$(cd "$RESTORE_COPY" && find . -type f ! -name backup.json -printf '%P\n' | sort)
    if [[ $actual != "$(jq -r '.files | keys[]' "$manifest" | sort)" ]]; then
        problems+=('the backup holds files backup.json does not list')
    fi
    local version release
    version=$(jq -r '.pulsarVersion // empty' "$manifest")
    release=$(release_version "$SCRIPT_DIR/release.json")
    if [[ -n $version && $(compare_versions "$version" "$release") == 1 ]]; then
        problems+=("it was made by Pulsar $version, newer than this release ($release): a panel does not run on the data of a newer one")
    fi
    local shipped unknown=()
    shipped=$(shipped_migrations "$SCRIPT_DIR/panel")
    while read -r id; do
        [[ -z $id ]] && continue
        grep -q "^$id" <<<"$shipped" || unknown+=("$id")
    done < <(jq -r '.migrations // [] | .[]' "$manifest")
    ((${#unknown[@]} > 0)) && problems+=("its database has migrations this release does not have (${unknown[*]}): it comes from a newer Pulsar")
    if ((${#problems[@]} > 0)); then
        fail "$EXIT_USAGE" "The backup cannot be restored: $(
            IFS=';'
            echo "${problems[*]}"
        ). Nothing was changed."
    fi
    for path in pulsar.dump appsettings.Production.json panel-data/keyring/keyring.pfx panel-data/tls/panel.pfx; do
        [[ -f $RESTORE_COPY/$path ]] || fail "$EXIT_USAGE" "The backup has no $path. Nothing was changed."
    done
}

# Whether this machine's pg_restore reads a dump (a newer pg_dump's format is refused: a backup from a PostgreSQL 18
# panel needs PostgreSQL 18 here).
check_dump_readable() {
    local output
    if ! output=$(pg_restore -l "$1" 2>&1 >/dev/null); then
        fail "$EXIT_UNSUPPORTED" "This machine's pg_restore cannot read the backup's database dump ($output). It comes from a newer PostgreSQL: install that version here (PostgreSQL's own apt repository has it: https://www.postgresql.org/download/linux/), then run the script again. Nothing was changed."
    fi
}

# --- Install ---------------------------------------------------------------------------------------------------------
check_install_options() {
    for port in "$PORT" "$SFTP_PORT"; do valid_port "$port" || fail "$EXIT_USAGE" "$port is not a TCP port."; done
    if [[ $AGENTS_PORT != 0 ]]; then valid_port "$AGENTS_PORT" || fail "$EXIT_USAGE" "$AGENTS_PORT is not a TCP port."; fi
    local ports=("$PORT" "$SFTP_PORT")
    [[ $AGENTS_PORT != 0 ]] && ports+=("$AGENTS_PORT")
    [[ -n $PUBLIC_IP ]] && ports+=(80)
    if [[ $(printf '%s\n' "${ports[@]}" | sort | uniq -d) ]]; then fail "$EXIT_USAGE" "The panel's ports must differ (${ports[*]})."; fi
    for port in "${ports[@]}"; do
        port_in_use "$port" && fail "$EXIT_USAGE" "The panel would listen on port $port, which another program uses. Stop it or choose another port. Nothing was changed."
    done
    [[ $ADMIN_USER =~ ^[A-Za-z0-9._@-]{1,64}$ ]] || fail "$EXIT_USAGE" "--admin-user must be 1 to 64 letters, digits, '.', '_', '@' or '-'."
    local name
    for name in "${HOST_NAMES[@]}"; do
        valid_host_name "$name" || fail "$EXIT_USAGE" "--host-name $name is not a name or an IP address."
        certificate_address "$name" >/dev/null || fail "$EXIT_USAGE" "--host-name: see above. Nothing was changed."
    done
    if [[ -n $PUBLIC_IP ]]; then
        [[ -n $(certificate_address "$PUBLIC_IP") ]] || fail "$EXIT_USAGE" "--public-ip $PUBLIC_IP is not an IP address."
    fi
    if [[ -n $CONNECTION_STRING ]] && has_control_characters "$CONNECTION_STRING"; then
        fail "$EXIT_USAGE" 'PULSAR_CONNECTION_STRING has control characters.'
    fi
    if [[ -n $CERTIFICATE_PATH ]]; then
        [[ -f $CERTIFICATE_PATH ]] || fail "$EXIT_USAGE" "--certificate $CERTIFICATE_PATH is not a file."
        local password_file certificate
        password_file=$(secret_file certificate-password "$CERTIFICATE_PASSWORD")
        certificate=$(pfx_certificate "$CERTIFICATE_PATH" "$password_file") ||
            fail "$EXIT_CERTIFICATE" "$CERTIFICATE_PATH does not open with the password given (PULSAR_CERTIFICATE_PASSWORD). Nothing was changed."
        pfx_has_key "$CERTIFICATE_PATH" "$password_file" ||
            fail "$EXIT_CERTIFICATE" "$CERTIFICATE_PATH holds no private key: it cannot be the panel's certificate. Nothing was changed."
        [[ -n $certificate ]] || fail "$EXIT_CERTIFICATE" "$CERTIFICATE_PATH holds no certificate. Nothing was changed."
    fi
}

assert_not_installed() {
    if [[ -e $UNIT || -e $PANEL_DIR || -e $SETTINGS ]]; then
        fail "$EXIT_USAGE" "The panel is installed already ($UNIT, $PANEL_DIR or $SETTINGS exists): update it with --update, or remove it with --uninstall first. Nothing was changed."
    fi
}

# Undoes what a failed install did: its service, programs, settings and firewall rules, and the database and role it
# made (empty but for what the failed run put there).
undo_install() {
    set +e
    systemctl disable --now "$SERVICE" >/dev/null 2>&1
    rm -f -- "$UNIT"
    systemctl daemon-reload >/dev/null 2>&1
    close_ports "${FIREWALL_RULES[@]}"
    rm -f -- "$SETTINGS" "$STATE" "$INSTALL_ROOT/install-pulsar.sh" "$INSTALL_ROOT/backup-pulsar.sh"
    rm -rf -- "$PANEL_DIR" "$INSTALL_ROOT"/.panel-new.*
    rmdir -- "$INSTALL_ROOT" 2>/dev/null
    local left=()
    if [[ $MADE_DATABASE == true ]]; then
        drop_panel_database || left+=("the '$DATABASE' database and role in PostgreSQL")
    fi
    rm -f -- "$PANEL_DATA/tls/panel.pfx" "$PANEL_DATA/tls/panel.cer" "$PANEL_DATA/tls/panel.crt" "$PANEL_DATA/keyring/keyring.pfx"
    rm -rf -- "${PANEL_DATA:?}/keys"
    set -e
    if ((${#left[@]} > 0)); then
        echo "Undoing this install left ${left[*]}: remove them before running this script again (docs/guides/install.md, \"Starting over\")." >&2
    else
        echo 'What this install had done is undone.' >&2
    fi
}

install_panel() {
    local restoring=false
    [[ -n $RESTORE_FROM ]] && restoring=true
    assert_not_installed
    check_install_options
    if [[ $restoring == true ]]; then
        step "Checking the backup in $RESTORE_FROM"
        copy_restore_source "$RESTORE_FROM"
    fi
    if [[ -z $CONNECTION_STRING ]]; then
        prepare_local_postgres
        assert_no_panel_database
        [[ $restoring == true ]] && check_dump_readable "$RESTORE_COPY/pulsar.dump"
    elif [[ $restoring == true ]]; then
        command -v pg_restore >/dev/null 2>&1 || apt_install postgresql-client || fail "$EXIT_UNSUPPORTED" 'pg_restore is needed to restore the backup: install PostgreSQL client programs.'
        check_dump_readable "$RESTORE_COPY/pulsar.dump"
    fi
    if [[ $restoring == false ]]; then
        if [[ -z $ADMIN_PASSWORD ]]; then
            read_secret "Password for the panel's first administrator, '$ADMIN_USER' (at least 12 characters)"
            ADMIN_PASSWORD=$REPLY
        fi
        ((${#ADMIN_PASSWORD} >= 12)) || fail "$EXIT_USAGE" "The administrator's password needs at least 12 characters. Nothing was changed."
        has_control_characters "$ADMIN_PASSWORD" && fail "$EXIT_USAGE" "The administrator's password has control characters. Nothing was changed."
    fi

    ensure_account
    trap 'status=$?; if ((status != 0)); then undo_install; fi; cleanup; exit $status' EXIT
    ensure_folders

    local database=local connection
    if [[ -n $CONNECTION_STRING ]]; then
        database=external
        connection=$CONNECTION_STRING
    else
        create_panel_database
        connection=$(local_connection_string)
    fi
    if [[ $restoring == true ]]; then
        step 'Restoring the database from the backup'
        if [[ $database == local ]]; then
            runuser -u "$ACCOUNT" -- pg_restore --no-owner --no-privileges --exit-on-error \
                -d "host=$PG_SOCKET port=$PG_PORT dbname=$DATABASE user=$ACCOUNT" <"$RESTORE_COPY/pulsar.dump"
        else
            pg_target "$connection"
            PGPASSWORD=$PG_PASSWORD PGSSLMODE=$PG_SSLMODE pg_restore --no-owner --no-privileges --exit-on-error \
                -d "$PG_TARGET" "$RESTORE_COPY/pulsar.dump"
        fi
    fi

    step "Installing the panel in $PANEL_DIR"
    local staged
    staged=$(stage_programs)
    mv -- "$staged" "$PANEL_DIR"
    install_scripts

    local tls_password keyring_password certificate_sha pfx=$PANEL_DATA/tls/panel.pfx self_signed=true
    if [[ $restoring == true ]]; then
        step 'Restoring the keys, certificates and settings from the backup'
        local old=$RESTORE_COPY/appsettings.Production.json
        tls_password=$(jq -r '.Kestrel.Endpoints.Https.Certificate.Password // ""' "$old")
        keyring_password=$(jq -r '.DataProtection.CertificatePassword // ""' "$old")
        local old_port
        old_port=$(jq -r '.Kestrel.Endpoints.Https.Url // ""' "$old" | sed -nE 's/.*:([0-9]+)\/?$/\1/p')
        [[ -n $old_port ]] && PORT=$old_port
        old_port=$(jq -r '.Kestrel.Endpoints.Agents.Url // ""' "$old" | sed -nE 's/.*:([0-9]+)\/?$/\1/p')
        [[ -n $old_port ]] && AGENTS_PORT=$old_port
        for old_port in "$PORT" "$AGENTS_PORT"; do
            [[ $old_port == 0 ]] && continue
            valid_port "$old_port" || fail "$EXIT_USAGE" "The backup's settings name the port $old_port, which is not a TCP port. Nothing was changed."
            port_in_use "$old_port" && fail "$EXIT_USAGE" "The backup's panel listened on port $old_port, which another program uses here. Stop it and run the script again."
        done
        install -m 0640 -o root -g "$ACCOUNT" "$RESTORE_COPY/panel-data/tls/panel.pfx" "$pfx"
        install -m 0640 -o root -g "$ACCOUNT" "$RESTORE_COPY/panel-data/keyring/keyring.pfx" "$PANEL_DATA/keyring/keyring.pfx"
        if [[ -d $RESTORE_COPY/panel-data/keys ]]; then
            cp -R --no-preserve=mode,ownership -- "$RESTORE_COPY/panel-data/keys/." "$PANEL_DATA/keys/"
        fi
        local certificate
        certificate=$(pfx_certificate "$pfx" "$(secret_file tls-password "$tls_password")") ||
            fail "$EXIT_CERTIFICATE" "The backup's HTTPS certificate does not open with the password in its settings."
        printf '%s\n' "$certificate" | openssl x509 -out "$PANEL_DATA/tls/panel.crt"
        openssl x509 -in "$PANEL_DATA/tls/panel.crt" -outform DER -out "$PANEL_DATA/tls/panel.cer"
        # Whether it is self-signed (subject = issuer): only such a certificate is renewed by --renew-certificate.
        [[ $(openssl x509 -in "$PANEL_DATA/tls/panel.crt" -noout -subject | sed 's/^subject=//') == "$(openssl x509 -in "$PANEL_DATA/tls/panel.crt" -noout -issuer | sed 's/^issuer=//')" ]] || self_signed=false
    else
        tls_password=$(new_secret 24)
        keyring_password=$(new_secret 24)
        if [[ -n $CERTIFICATE_PATH ]]; then
            self_signed=false
            install -m 0640 -o root -g "$ACCOUNT" "$CERTIFICATE_PATH" "$pfx"
            tls_password=$CERTIFICATE_PASSWORD
            pfx_certificate "$pfx" "$(secret_file tls-password "$tls_password")" | openssl x509 -out "$PANEL_DATA/tls/panel.crt"
            openssl x509 -in "$PANEL_DATA/tls/panel.crt" -outform DER -out "$PANEL_DATA/tls/panel.cer"
        else
            certificate_names
            make_self_signed "$WORK/panel" "$(secret_file tls-password "$tls_password")"
            install -m 0640 -o root -g "$ACCOUNT" "$WORK/panel.pfx" "$pfx"
            install -m 0644 -o root -g root "$WORK/panel.cer" "$PANEL_DATA/tls/panel.cer"
            install -m 0644 -o root -g root "$WORK/panel.crt" "$PANEL_DATA/tls/panel.crt"
        fi
        make_key_ring_certificate "$WORK/keyring.pfx" "$(secret_file keyring-password "$keyring_password")"
        install -m 0640 -o root -g "$ACCOUNT" "$WORK/keyring.pfx" "$PANEL_DATA/keyring/keyring.pfx"
    fi
    chmod 0644 "$PANEL_DATA/tls/panel.cer" "$PANEL_DATA/tls/panel.crt"
    chown -R "$ACCOUNT:$ACCOUNT" "$PANEL_DATA/keys"
    chmod -R go-rwx "$PANEL_DATA/keys"
    certificate_sha=$(certificate_sha256 "$PANEL_DATA/tls/panel.crt")

    local setup_token=''
    [[ $restoring == false ]] && setup_token=$(new_secret 48)
    local settings
    settings=$(CONNECTION="$connection" TLS_PASSWORD="$tls_password" KEYRING_PASSWORD="$keyring_password" SETUP_TOKEN="$setup_token" \
        jq -n --arg port "$PORT" --arg agents "$AGENTS_PORT" --arg pfx "$pfx" --arg keys "$PANEL_DATA/keys" \
        --arg keyring "$PANEL_DATA/keyring/keyring.pfx" --arg logs "$PANEL_LOGS" '
        {
          ConnectionStrings: { Pulsar: env.CONNECTION },
          Kestrel: { Endpoints: ({ Https: { Url: "https://*:\($port)", Certificate: { Path: $pfx, Password: env.TLS_PASSWORD } } }
            + (if $agents != "0" then { Agents: { Url: "https://*:\($agents)", Certificate: { Path: $pfx, Password: env.TLS_PASSWORD } } } else {} end)) },
          DataProtection: { KeysPath: $keys, CertificatePath: $keyring, CertificatePassword: env.KEYRING_PASSWORD },
          Pulsar: { LogsDirectory: $logs }
        } + (if env.SETUP_TOKEN != "" then { InitialSetup: { Token: env.SETUP_TOKEN } } else {} end)')
    write_settings "$settings"

    if [[ $NO_FIREWALL != true ]]; then
        open_port "$PORT" HTTPS
        [[ $AGENTS_PORT != 0 ]] && open_port "$AGENTS_PORT" agents
        open_port "$SFTP_PORT" SFTP
        [[ -n $PUBLIC_IP ]] && open_port 80 "Let's Encrypt check"
    fi

    local hosts_json firewall_json
    hosts_json=$(printf '%s\n' "${HOST_NAMES[@]}" | jq -R . | jq -sc 'map(select(. != ""))')
    firewall_json=$(printf '%s\n' "${FIREWALL_RULES[@]}" | jq -R . | jq -sc 'map(select(. != ""))')
    write_state "$(jq -n --arg version "$(release_version "$SCRIPT_DIR/release.json")" --arg database "$database" \
        --argjson port "$PORT" --argjson agents "$AGENTS_PORT" --argjson sftp "$SFTP_PORT" --argjson self "$self_signed" \
        --argjson hosts "$hosts_json" --argjson firewall "$firewall_json" --arg ip "$PUBLIC_IP" \
        '{format: 1, version: $version, database: $database, port: $port, agentsPort: $agents, sftpPort: $sftp,
          selfSigned: $self, hostNames: $hosts, firewall: $firewall, publicIp: $ip}')"

    write_unit
    migrate_database || fail "$EXIT_FAILED" 'The panel could not prepare its database (see above).'
    start_panel || fail "$EXIT_FAILED" 'The panel service did not start (see above).'
    panel_client "$(certificate_pin "$PANEL_DATA/tls/panel.crt")"
    step 'Waiting for the panel'
    wait_panel_ready || fail "$EXIT_FAILED" 'The panel did not start (see above).'

    if [[ $restoring == false ]]; then
        step "Creating the administrator '$ADMIN_USER'"
        panel_call GET /api/v1/auth/session >/dev/null
        TOKEN="$setup_token" USER_NAME="$ADMIN_USER" PASSWORD="$ADMIN_PASSWORD" \
            jq -n '{token: env.TOKEN, userName: env.USER_NAME, password: env.PASSWORD, displayName: null, email: null}' |
            panel_call POST /api/v1/auth/setup body >/dev/null || fail "$EXIT_FAILED" 'The panel refused to create the administrator (see above).'
        # The setup token is spent: no secret the panel no longer needs stays in its settings.
        write_settings "$(jq 'del(.InitialSetup)' "$SETTINGS")"
        if [[ $SFTP_PORT != 2022 ]]; then
            step "Setting the SFTP port to $SFTP_PORT"
            panel_call GET /api/v1/auth/session >/dev/null
            jq -n --argjson port "$SFTP_PORT" '{enabled: true, port: $port, publicHost: null}' |
                panel_call PUT /api/v1/sftp/settings body >/dev/null || fail "$EXIT_FAILED" 'The panel refused the SFTP port (see above).'
        fi
        if [[ -n $PUBLIC_IP ]]; then
            step "Turning on the public IP certificate for $PUBLIC_IP (Let's Encrypt)"
            panel_call GET /api/v1/auth/session >/dev/null
            jq -n --arg ip "$PUBLIC_IP" '{enabled: true, publicIp: $ip, email: null, directory: "production"}' |
                panel_call PUT /api/v1/public-ip-certificate body >/dev/null || fail "$EXIT_FAILED" 'The panel refused the public IP certificate (see above).'
        fi
        panel_call POST /api/v1/auth/logout >/dev/null 2>&1 || true
    fi
    trap cleanup EXIT

    local address lan=()
    mapfile -t lan < <(lan_addresses)
    address=${lan[0]:-}
    address=${address%% *}
    echo
    echo "Pulsar's panel is installed and running."
    echo "  Open: https://${address:-localhost}:$PORT (from this machine: https://localhost:$PORT)"
    echo "  Certificate SHA-256: $(format_fingerprint "$certificate_sha")"
    if [[ $self_signed == true ]]; then
        echo "  The certificate is self-signed: browsers warn until it is trusted ($PANEL_DATA/tls/panel.cer; docs/guides/install.md, \"Trusting the panel's certificate\")."
    fi
    echo "  SFTP: port $SFTP_PORT. Settings: $SETTINGS. Logs: journalctl -u $SERVICE, and $PANEL_LOGS."
    echo "  Add Windows machines as nodes from the panel: Nodes > Add node."
    if [[ $restoring == true ]]; then echo '  Restored from the backup: sign in with its users. Nodes reconnect when the panel'"'"'s address leads here.'; fi
    echo "  Back up the panel: sudo $INSTALL_ROOT/backup-pulsar.sh --destination <folder> (docs/guides/backup.md)."
}

# --- Update ----------------------------------------------------------------------------------------------------------
require_installed() {
    if [[ ! -f $UNIT || ! -f $SETTINGS || ! -f $STATE ]] || [[ ! -d $PANEL_DIR && ${1:-} != interrupted-ok ]]; then
        fail "$EXIT_USAGE" "The panel is not installed here by this script ($UNIT, $PANEL_DIR, $SETTINGS and $STATE must exist)."
    fi
    PORT=$(jq -r '.port' "$STATE")
}

panel_pin_from_settings() {
    certificate_pin "$PANEL_DATA/tls/panel.crt"
}

JOURNAL=$UPDATE_ROOT/journal.json

write_journal() {
    local staged
    staged=$(mktemp "$UPDATE_ROOT/.journal.XXXXXX")
    printf '%s\n' "$1" >"$staged"
    mv -f -- "$staged" "$JOURNAL"
    sync -f "$JOURNAL" 2>/dev/null || sync
}

# Backs up what an update could break into $UPDATE_ROOT/backups/<from>-<time> (as backup-pulsar.sh does): prints the folder.
update_backup() {
    local from=$1 folder
    folder=$UPDATE_ROOT/backups/$from-$(date -u +%Y%m%d-%H%M%S)
    mkdir -m 0700 -- "$folder" || return 1
    pg_panel pg_dump -Fc >"$folder/pulsar.dump" || return 1
    install -m 0600 "$SETTINGS" "$folder/appsettings.Production.json" || return 1
    mkdir -m 0700 "$folder/panel-data" || return 1
    copy_plain_files "$PANEL_DATA/keys" "$folder/panel-data/keys" || return 1
    cp -R --no-preserve=ownership -- "$PANEL_DATA/keyring" "$PANEL_DATA/tls" "$folder/panel-data/" || return 1
    applied_migrations >"$folder/migrations.txt" || return 1
    printf '%s' "$folder"
}

# Copies the plain files of a folder the panel's user can write (its keys) into a new folder: never a link, a folder
# or a name a restore would refuse, which are left out with a warning.
copy_plain_files() {
    local from=$1 to=$2 entry name
    mkdir -m 0700 -- "$to" || return 1
    while IFS= read -r -d '' entry; do
        name=${entry##*/}
        if [[ -L $entry || ! -f $entry || ! $name =~ ^[A-Za-z0-9._-]+$ ]]; then
            warn "$entry is not a plain key file: it is left out of the backup."
            continue
        fi
        # -P: should the file become a link meanwhile, the link is copied (and refused at a restore), never followed.
        cp -P --no-preserve=ownership -- "$entry" "$to/$name" || return 1
    done < <(find "$from" -mindepth 1 -maxdepth 1 -print0)
}

prune_update_backups() {
    local old
    while read -r old; do
        [[ -n $old ]] && rm -rf -- "$old"
    done < <(find "$UPDATE_ROOT/backups" -mindepth 1 -maxdepth 1 -type d -printf '%T@ %p\n' | sort -rn | tail -n +$((UPDATE_BACKUPS_KEPT + 1)) | cut -d' ' -f2-)
}

restore_database_dump() {
    local dump=$1
    if [[ $(jq -r '.database' "$STATE") == local ]]; then
        prepare_local_postgres >/dev/null
        pg_super -c "DROP DATABASE IF EXISTS $DATABASE WITH (FORCE)" &&
            pg_super -c "CREATE DATABASE $DATABASE OWNER $ACCOUNT ENCODING 'UTF8' TEMPLATE template0" &&
            pg_panel pg_restore --no-owner --no-privileges --exit-on-error -d <"$dump"
    else
        pg_panel pg_restore --clean --if-exists --no-owner --no-privileges --exit-on-error -d <"$dump"
    fi
}

# Puts the previous programs back (and the database, when the update changed it) and starts them.
roll_back_update() {
    local backup=$1 before_file=$2
    set +e
    step 'Putting the previous version back'
    systemctl stop "$SERVICE" >/dev/null 2>&1
    if [[ -d $UPDATE_ROOT/previous/panel ]]; then
        rm -rf -- "$INSTALL_ROOT/.panel-failed"
        [[ -d $PANEL_DIR ]] && mv -- "$PANEL_DIR" "$INSTALL_ROOT/.panel-failed"
        mv -- "$UPDATE_ROOT/previous/panel" "$PANEL_DIR"
        rm -rf -- "$INSTALL_ROOT/.panel-failed"
    fi
    rm -rf -- "$INSTALL_ROOT"/.panel-new.*
    # The database goes back too when the update changed its migrations, or when that cannot be told.
    local after
    after=$(applied_migrations 2>/dev/null) || after=unknown
    if [[ -n $backup && -f $backup/pulsar.dump && $after != "$(cat "$before_file" 2>/dev/null)" ]]; then
        step 'Putting the database back as it was before the update'
        restore_database_dump "$backup/pulsar.dump" || warn "The database could not be put back: restore it from $backup/pulsar.dump (docs/guides/backup.md)."
    fi
    if [[ -f $backup/appsettings.Production.json ]]; then install -m 0640 -o root -g "$ACCOUNT" "$backup/appsettings.Production.json" "$SETTINGS"; fi
    write_unit
    systemctl start "$SERVICE"
    panel_client "$(panel_pin_from_settings)"
    local ok=true
    wait_panel_ready || ok=false
    rm -f -- "$JOURNAL"
    set -e
    [[ $ok == true ]]
}

write_update_result() {
    local status=$1 from=$2 to=$3 failure=${4:-}
    jq -n --arg status "$status" --arg from "$from" --arg to "$to" --arg failure "$failure" --arg at "$(date -u +%FT%TZ)" \
        '{status: $status, from: $from, to: $to, at: $at} + (if $failure != "" then {error: $failure} else {} end)' >"$UPDATE_ROOT/last-update.json"
}

resume_interrupted_update() {
    [[ -f $JOURNAL ]] || return 0
    local stage backup
    stage=$(jq -r '.stage // ""' "$JOURNAL" 2>/dev/null || echo unreadable)
    backup=$(jq -r '.backup // ""' "$JOURNAL" 2>/dev/null || echo)
    if [[ ($stage == backed-up || $stage == '') && -d $PANEL_DIR ]]; then
        rm -f -- "$JOURNAL"
        return 0
    fi
    if [[ $DISCARD_INTERRUPTED_UPDATE == true ]]; then
        warn "An earlier update was cut off ($stage); going on without rolling it back (--discard-interrupted-update)."
        mv -- "$JOURNAL" "$UPDATE_ROOT/journal-discarded-$(date -u +%Y%m%d-%H%M%S).json"
        return 0
    fi
    warn "An earlier update was cut off while it was $stage: putting the version before it back."
    if roll_back_update "$backup" "$backup/migrations.txt"; then
        step 'The version before the cut-off update is back and runs.'
    else
        fail "$EXIT_FAILED" "The cut-off update could not be rolled back (see above). Its backup is in $backup. To go on with Pulsar as it is, run --update again with --discard-interrupted-update."
    fi
}

update_panel() {
    # An update cut off between moving the programs away and putting the new ones in place left no programs' folder:
    # its journal is resumed (rolled back) first.
    require_installed interrupted-ok
    exec 9>"$LOCK_FILE"
    flock -n 9 || fail "$EXIT_USAGE" 'Another run of this script is under way.'
    resume_interrupted_update
    require_installed
    local from to
    from=$(release_version "$PANEL_DIR/release.json" 2>/dev/null || jq -r '.version' "$STATE")
    to=$(release_version "$SCRIPT_DIR/release.json")
    if [[ $(compare_versions "$to" "$from") == -1 ]]; then
        fail "$EXIT_USAGE" "This release ($to) is older than the installed panel ($from): a panel does not run on the data of a newer one. Nothing was changed."
    fi
    if [[ $(jq -r '.database' "$STATE") == local ]]; then
        prepare_local_postgres >/dev/null
    else
        command -v pg_dump >/dev/null 2>&1 || apt_install postgresql-client ||
            fail "$EXIT_UNSUPPORTED" 'pg_dump is needed to back up the database before the update: install the PostgreSQL client programs.'
    fi
    step "Updating the panel from $from to $to"
    step 'Backing up the database, keys and settings'
    local backup
    backup=$(update_backup "$from") || fail "$EXIT_FAILED" 'The backup before the update failed (see above). Nothing was changed.'
    step "Backup: $backup"
    write_journal "$(jq -n --arg from "$from" --arg to "$to" --arg backup "$backup" '{from: $from, to: $to, backup: $backup, stage: "backed-up"}')"

    local staged
    staged=$(stage_programs)
    systemctl stop "$SERVICE"
    rm -rf -- "$UPDATE_ROOT/previous"
    mkdir -m 0700 "$UPDATE_ROOT/previous"
    write_journal "$(jq '.stage = "swapping"' "$JOURNAL")"
    mv -- "$PANEL_DIR" "$UPDATE_ROOT/previous/panel"
    mv -- "$staged" "$PANEL_DIR"
    write_journal "$(jq '.stage = "swapped"' "$JOURNAL")"
    install_scripts
    write_unit

    local failure=''
    if ! migrate_database; then
        failure='the new version could not prepare the database'
    elif ! start_panel; then
        failure='the new version did not start'
    else
        panel_client "$(panel_pin_from_settings)"
        wait_panel_ready || failure='the new version did not become ready'
    fi
    if [[ -n $failure ]]; then
        warn "The update failed: $failure."
        if roll_back_update "$backup" "$backup/migrations.txt"; then
            write_update_result rolled_back "$from" "$to" "$failure"
            fail "$EXIT_ROLLED_BACK" "The update to $to failed ($failure): Pulsar $from is back and runs. The update's log: $LOG_FILE."
        fi
        write_update_result failed "$from" "$to" "$failure"
        fail "$EXIT_FAILED" "The update to $to failed ($failure), and putting $from back failed too (see above). The backup is in $backup (docs/guides/backup.md)."
    fi
    rm -f -- "$JOURNAL"
    jq --arg version "$to" '.version = $version' "$STATE" >"$WORK/state.json" && write_state "$(cat "$WORK/state.json")"
    write_update_result succeeded "$from" "$to"
    prune_update_backups
    echo
    echo "The panel is updated to $to and runs. The previous version's programs are in $UPDATE_ROOT/previous, its backup in $backup."
}

# --- Renew the certificate -------------------------------------------------------------------------------------------
renew_certificate() {
    require_installed
    [[ $(jq -r '.selfSigned' "$STATE") == true ]] ||
        fail "$EXIT_CERTIFICATE" "The panel's certificate is not one this script made: replace it yourself ($PANEL_DATA/tls/panel.pfx and its password in $SETTINGS). Nothing was changed."
    exec 9>"$LOCK_FILE"
    flock -n 9 || fail "$EXIT_USAGE" 'Another run of this script is under way.'
    if [[ $HOST_NAMES_GIVEN == false ]]; then mapfile -t HOST_NAMES < <(jq -r '.hostNames[]?' "$STATE"); fi
    local name
    for name in "${HOST_NAMES[@]}"; do
        valid_host_name "$name" || fail "$EXIT_USAGE" "--host-name $name is not a name or an IP address."
    done
    local old_sha new_password
    old_sha=$(certificate_sha256 "$PANEL_DATA/tls/panel.crt")
    certificate_names
    new_password=$(new_secret 24)
    make_self_signed "$WORK/panel" "$(secret_file new-tls-password "$new_password")"

    step 'Replacing the certificate (the old one is kept until the new one works)'
    local saved=$WORK/saved
    mkdir -m 0700 "$saved"
    cp -p -- "$PANEL_DATA/tls/panel.pfx" "$PANEL_DATA/tls/panel.cer" "$PANEL_DATA/tls/panel.crt" "$SETTINGS" "$saved/"
    install -m 0640 -o root -g "$ACCOUNT" "$WORK/panel.pfx" "$PANEL_DATA/tls/panel.pfx"
    install -m 0644 -o root -g root "$WORK/panel.cer" "$PANEL_DATA/tls/panel.cer"
    install -m 0644 -o root -g root "$WORK/panel.crt" "$PANEL_DATA/tls/panel.crt"
    write_settings "$(PASSWORD="$new_password" jq '.Kestrel.Endpoints |= with_entries(.value.Certificate.Password = env.PASSWORD)' "$saved/panel.json")"
    local ok=true
    if systemctl restart "$SERVICE"; then
        panel_client "$(certificate_pin "$PANEL_DATA/tls/panel.crt")"
        wait_panel_ready || ok=false
    else
        ok=false
    fi
    if [[ $ok != true ]]; then
        warn 'The panel did not work with the new certificate: putting the old one back.'
        install -m 0640 -o root -g "$ACCOUNT" "$saved/panel.pfx" "$PANEL_DATA/tls/panel.pfx"
        install -m 0644 -o root -g root "$saved/panel.cer" "$PANEL_DATA/tls/panel.cer"
        install -m 0644 -o root -g root "$saved/panel.crt" "$PANEL_DATA/tls/panel.crt"
        install -m 0640 -o root -g "$ACCOUNT" "$saved/panel.json" "$SETTINGS"
        systemctl restart "$SERVICE" || true
        fail "$EXIT_CERTIFICATE" 'The certificate was not renewed: the old one is back.'
    fi
    local hosts_json
    hosts_json=$(printf '%s\n' "${HOST_NAMES[@]}" | jq -R . | jq -sc 'map(select(. != ""))')
    write_state "$(jq --argjson hosts "$hosts_json" '.hostNames = $hosts' "$STATE")"
    echo
    echo "The panel's certificate is renewed."
    echo "  Old SHA-256: $(format_fingerprint "$old_sha")"
    echo "  New SHA-256: $(format_fingerprint "$(certificate_sha256 "$PANEL_DATA/tls/panel.crt")")"
    echo "  Browsers that trusted the old one must trust $PANEL_DATA/tls/panel.cer. Nodes added with a node code pinned the old"
    echo "  certificate: on each, run its installer again with a new node code from the panel (Nodes > the node > Pin again)."
}

# --- Uninstall -------------------------------------------------------------------------------------------------------
uninstall_panel() {
    [[ -e $UNIT || -e $PANEL_DIR || -e $SETTINGS ]] || fail "$EXIT_USAGE" 'The panel is not installed here.'
    exec 9>"$LOCK_FILE"
    flock -n 9 || fail "$EXIT_USAGE" 'Another run of this script is under way.'
    step 'Removing the panel'
    local rules=()
    [[ -f $STATE ]] && mapfile -t rules < <(jq -r '.firewall[]?' "$STATE")
    systemctl disable --now "$SERVICE" >/dev/null 2>&1 || true
    rm -f -- "$UNIT"
    systemctl daemon-reload
    close_ports "${rules[@]}"
    # The settings first: they hold the passwords.
    rm -f -- "$SETTINGS" "$STATE"
    rm -rf -- "$PANEL_DIR" "$INSTALL_ROOT"/.panel-new.* "$INSTALL_ROOT/release.json"
    rm -f -- "$INSTALL_ROOT/install-pulsar.sh" "$INSTALL_ROOT/backup-pulsar.sh"
    rmdir -- "$INSTALL_ROOT" 2>/dev/null || true
    echo
    echo 'The panel is removed: its service, programs, settings and firewall rules.'
    echo "Kept: the '$DATABASE' database in PostgreSQL, $PANEL_DATA (keys, certificates), $UPDATE_ROOT, $LOG_ROOT and the"
    echo "system user '$ACCOUNT'. To remove them too: docs/guides/install.md, \"Starting over\"."
}

# --- Main ------------------------------------------------------------------------------------------------------------
parse_options() {
    while (($# > 0)); do
        case $1 in
        --port) PORT=${2:?--port needs a value}; shift ;;
        --agents-port) AGENTS_PORT=${2:?--agents-port needs a value}; shift ;;
        --sftp-port) SFTP_PORT=${2:?--sftp-port needs a value}; shift ;;
        --host-name) HOST_NAMES+=("${2:?--host-name needs a value}"); HOST_NAMES_GIVEN=true; shift ;;
        --certificate) CERTIFICATE_PATH=${2:?--certificate needs a value}; shift ;;
        --admin-user) ADMIN_USER=${2:?--admin-user needs a value}; shift ;;
        --public-ip) PUBLIC_IP=${2:?--public-ip needs a value}; shift ;;
        --no-firewall) NO_FIREWALL=true ;;
        --restore-from) RESTORE_FROM=${2:?--restore-from needs a value}; shift ;;
        --trust-restore-source) TRUST_RESTORE_SOURCE=true ;;
        --secrets-file) SECRETS_FILE=${2:?--secrets-file needs a value}; shift ;;
        --unattended) UNATTENDED=true ;;
        --update) MODE=update ;;
        --discard-interrupted-update) DISCARD_INTERRUPTED_UPDATE=true ;;
        --uninstall) MODE=uninstall ;;
        --renew-certificate) MODE=renew ;;
        -h | --help) usage; exit 0 ;;
        *) fail "$EXIT_USAGE" "Unknown option $1 (--help lists them)." ;;
        esac
        shift
    done
    if [[ $MODE != install ]]; then
        local given=()
        [[ $PORT != 8443 || $AGENTS_PORT != 0 || $SFTP_PORT != 2022 ]] && given+=(ports)
        [[ -n $CERTIFICATE_PATH || -n $PUBLIC_IP || -n $RESTORE_FROM || $ADMIN_USER != admin ]] && given+=(install options)
        [[ $MODE != renew && $HOST_NAMES_GIVEN == true ]] && given+=(--host-name)
        ((${#given[@]} == 0)) || fail "$EXIT_USAGE" "${given[*]} only go with an install."
    fi
}

main() {
    parse_options "$@"
    require_root
    require_system
    start_log
    make_work
    case $MODE in
    install | update)
        require_trusted_location
        require_release
        ;;
    esac
    require_tools
    read_secrets_file
    case $MODE in
    install) install_panel ;;
    update) update_panel ;;
    renew) renew_certificate ;;
    uninstall) uninstall_panel ;;
    esac
}

# Run when executed, not when a test reads its functions (scripts/tests/test-install-scripts.sh).
if [[ ${BASH_SOURCE[0]} == "$0" ]]; then
    main "$@"
fi
