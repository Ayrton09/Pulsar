#!/usr/bin/env bash
# Installs, updates or removes the Pulsar agent on this Linux machine, which makes it a node of a Pulsar panel: the
# machine that runs its game servers (docs/guides/install.md, "Linux nodes"). Run it as root from the folder of the
# release's Linux tarball (pulsar-<version>-linux-x64.tar.gz), which must be a folder only root can change: the script
# refuses any other. The one-line install does all of it:
#
#   curl -fsSL https://github.com/Ayrton09/Pulsar/releases/latest/download/install.sh | sudo bash -s -- --agent
#
#   sudo ./install-pulsar-agent.sh                 install the agent and enroll it with a panel (asks for the node code)
#   sudo ./install-pulsar-agent.sh --update        replace the agent's programs (its game servers keep running)
#   sudo /opt/pulsar/install-pulsar-agent.sh --uninstall   remove the agent from Pulsar (keeps the servers' folders)
#
# --wine (with an install or an update) installs Wine too: games whose dedicated server is only for Windows (V Rising,
# Enshrouded...) run with it on this node.
#
# The node code comes from the panel (Nodes > Add node). It carries a single-use enrollment token, so it never goes on
# the command line, which other users can read: it is pasted on the terminal when asked for, or comes from the
# environment (PULSAR_NODE_CODE) or from --secrets-file (a JSON file only root can read: {"nodeCode": ...}).
#
# The agent runs as root (pulsar-agent.service): it makes a system user for each game server and runs the server's game
# as it. Its programs go in /opt/pulsar/agent, its state in /var/lib/pulsar/agent, the servers in /var/lib/pulsar/servers
# unless --servers-root says otherwise. Every run writes a log, without secrets, to /var/log/pulsar/setup.
#
# Exit codes, as install-pulsar.sh's: 0 done; 1 failed (the message says what was left); 2 wrong or missing options,
# nothing changed; 3 this machine or folder cannot run the agent as it is, nothing changed; 7 --update put the previous
# version back because the new one did not work.
set -Eeuo pipefail
# A failed command inside $(...) ends it too (bash clears -e there otherwise); a pipe whose reader stops early (head,
# grep -q) is never used on a long output, since pipefail would count the writer's SIGPIPE as a failure.
shopt -s inherit_errexit
umask 077
export LC_ALL=C
export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin

readonly EXIT_FAILED=1 EXIT_USAGE=2 EXIT_UNSUPPORTED=3 EXIT_ROLLED_BACK=7

readonly INSTALL_ROOT=/opt/pulsar
readonly AGENT_DIR=$INSTALL_ROOT/agent
readonly CONFIG_DIR=/etc/pulsar
readonly STATE=$CONFIG_DIR/agent-install.json
readonly PANEL_STATE=$CONFIG_DIR/install.json
readonly DATA_ROOT=/var/lib/pulsar
readonly UPDATE_ROOT=$DATA_ROOT/updates
# The agent's programs before its last update: not the panel's $UPDATE_ROOT/previous, which an update of this machine's
# panel (run by the agent's) empties.
readonly PREVIOUS_AGENT=$UPDATE_ROOT/previous-agent
# Written before an update stops the agent, removed once its result is written: a run that finds it knows the update
# before it was cut off (the machine lost power) and rolls it back first.
readonly AGENT_JOURNAL=$UPDATE_ROOT/agent-update.json
readonly LOG_ROOT=/var/log/pulsar
readonly SETUP_LOGS=$LOG_ROOT/setup
readonly SERVICE=pulsar-agent
readonly UNIT_DIR=/etc/systemd/system
# One run at a time, in /run (root's alone; /run/lock is open to every user, who could take it first). Not the panel's
# lock: an update of both takes it, then runs install-pulsar.sh, which takes its own.
readonly LOCK_FILE=/run/pulsar-agent-install.lock
readonly START_SECONDS=90

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
readonly SCRIPT_DIR

# --- Options ---------------------------------------------------------------------------------------------------------
MODE=install
SERVERS_ROOT=
BACKUPS_ROOT=
SECRETS_FILE=${PULSAR_SECRETS_FILE:-}
UNATTENDED=false
WINE=false
UPDATE_ATTEMPT=
NODE_CODE=${PULSAR_NODE_CODE:-}
unset PULSAR_NODE_CODE

usage() {
    sed -n '2,/^set -Eeuo/p' "${BASH_SOURCE[0]}" | sed '$d; s/^# \{0,1\}//'
    cat <<'EOF'
Options:
  --servers-root DIR      Where the game servers' folders go (default /var/lib/pulsar/servers).
  --backups-root DIR      Where their backups go (default the agent's state folder).
  --secrets-file FILE     Secrets as JSON, readable by root only: {"nodeCode": ...}.
  --unattended            Never prompt: what is missing is an error.
  --wine                  Install Wine too (Debian's and Ubuntu's wine64): games only for Windows run with it.
  --update                Update the installed agent to this release (and the panel, when this machine has it too).
  --update-attempt ID     With --update: the panel's id for the request, written in the result (the agent passes it).
  --uninstall             Remove the agent from Pulsar.
  --help                  This text.
EOF
}

# --- Output, log and errors ------------------------------------------------------------------------------------------
LOG_FILE=

step() { printf '==> %s\n' "$*"; }
warn() { printf 'WARNING: %s\n' "$*" >&2; }
fail() {
    local code=$1
    shift
    printf 'ERROR: %s\n' "$*" >&2
    exit "$code"
}

start_log() {
    install -d -m 0755 -o root -g root "$LOG_ROOT"
    install -d -m 0700 -o root -g root "$SETUP_LOGS"
    LOG_FILE=$SETUP_LOGS/agent-$MODE-$(date -u +%Y%m%d-%H%M%S)-$$.log
    : >"$LOG_FILE"
    chmod 0600 "$LOG_FILE"
    # What is printed goes to the log too; secrets are never printed.
    exec > >(tee -a "$LOG_FILE") 2>&1
    step "Pulsar agent install script ($MODE), log: $LOG_FILE"
}

has_control_characters() { [[ $1 == *[[:cntrl:]]* ]]; }

# --- Checks before anything changes ----------------------------------------------------------------------------------
require_root() {
    [[ $(id -u) -eq 0 ]] || fail "$EXIT_UNSUPPORTED" 'Run this script as root (sudo).'
}

require_system() {
    [[ -d /run/systemd/system ]] || fail "$EXIT_UNSUPPORTED" 'The Pulsar agent runs as a systemd service, and this machine does not run systemd.'
    [[ $(uname -m) == x86_64 ]] || fail "$EXIT_UNSUPPORTED" "This release is for x86_64 machines, and this one is $(uname -m)."
    # Games' CPU and memory caps are their cgroups (v2, the unified hierarchy Debian 11+ and Ubuntu 21.10+ mount).
    [[ -f /sys/fs/cgroup/cgroup.controllers ]] ||
        fail "$EXIT_UNSUPPORTED" 'The Pulsar agent needs cgroups v2 (the unified hierarchy at /sys/fs/cgroup), and this machine mounts the older ones.'
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

# Whether a folder is root's and, besides root, writable only by a system group (gid below 1000), with the folders
# above it root's alone.
system_group_folder() {
    local folder=$1 mode
    [[ ! -L $folder && $(stat -c %u -- "$folder") == 0 ]] || return 1
    mode=$(stat -c %a -- "$folder")
    (((8#$mode & 8#002) == 0)) || return 1
    (((8#$mode & 8#020) == 0 || $(stat -c %g -- "$folder") < 1000)) || return 1
    path_owned_by_root "$(dirname -- "$folder")"
}

# The folders the agent's programs, settings, state, logs and service go into must be ones only root can change, up
# to /: whoever could rename or replace /opt/pulsar would have their programs run as root by the agent's service.
require_trusted_parents() {
    local folder trusted
    for folder in "$(dirname -- "$INSTALL_ROOT")" "$(dirname -- "$CONFIG_DIR")" "$(dirname -- "$DATA_ROOT")" \
        "$(dirname -- "$LOG_ROOT")" "$UNIT_DIR"; do
        trusted=false
        if path_owned_by_root "$folder"; then
            trusted=true
        elif [[ $folder == "$(dirname -- "$LOG_ROOT")" ]] && system_group_folder "$folder"; then
            # Debian's and Ubuntu's /var/log is root's and its system group's (syslog): no user's.
            trusted=true
        fi
        [[ $trusted == true ]] || fail "$EXIT_UNSUPPORTED" \
            "$folder (owner $(stat -c %U -- "$folder"), mode $(stat -c %a -- "$folder")), or a folder above it, can be changed by another user than root, and the agent would put its programs, settings or service there. Make it root's and closed to others (sudo chown root:root $folder && sudo chmod go-w $folder) and run the script again. Nothing was changed."
    done
}

# This script runs as root and installs what is next to it: nobody else may have been able to change it.
require_trusted_location() {
    path_owned_by_root "$SCRIPT_DIR" || fail "$EXIT_UNSUPPORTED" \
        "$SCRIPT_DIR (or a folder above it) can be changed by another user than root. Extract the release as root into a folder only root can change, for example: sudo mkdir -m 700 /root/pulsar-release && sudo tar -xzf pulsar-<version>-linux-x64.tar.gz -C /root/pulsar-release --no-same-owner"
    local found
    found=$(find "$SCRIPT_DIR" \( ! -uid 0 -o -perm /022 -o -type l \) -print -quit)
    [[ -z $found ]] || fail "$EXIT_UNSUPPORTED" \
        "$found can be changed by another user than root (or is a link). Extract the release as root (tar --no-same-owner) into a folder only root can change."
}

require_release() {
    [[ -f $SCRIPT_DIR/agent/PulsarAgent ]] ||
        fail "$EXIT_UNSUPPORTED" "$SCRIPT_DIR/agent is not the agent of a Pulsar release (PulsarAgent is missing): run the script from the extracted release."
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

# What the script and the agent need from the system: the tools, the libraries .NET uses (ICU, OpenSSL 3), and the
# 32-bit ones SteamCMD and the older Steam servers use.
require_tools() {
    local missing=() tool
    for tool in jq flock useradd userdel getent systemd-run tar find; do
        command -v "$tool" >/dev/null 2>&1 || missing+=("$tool")
    done
    local packages=()
    for tool in "${missing[@]}"; do
        case $tool in
        jq) packages+=(jq) ;;
        flock) packages+=(util-linux) ;;
        useradd | userdel) packages+=(passwd) ;;
        getent) packages+=(libc-bin) ;;
        systemd-run) packages+=(systemd) ;;
        find) packages+=(findutils) ;;
        *) packages+=("$tool") ;;
        esac
    done
    if ! has_library 'libicuuc\.so\.'; then
        local icu=''
        if has_apt; then icu=$(apt-cache search --names-only '^libicu[0-9]+$' 2>/dev/null | awk '{print $1}' | sort -V | tail -1); fi
        packages+=("${icu:-libicu}")
    fi
    has_library 'libssl\.so\.3' || packages+=(libssl3)
    # SteamCMD and the 32-bit Source servers (srcds_linux) are 32-bit programs: the 32-bit GCC and C++ libraries (which
    # bring the 32-bit C library).
    has_library 'libgcc_s\.so\.1 (libc6) ' || packages+=(lib32gcc-s1)
    has_library 'libstdc++\.so\.6 (libc6) ' || packages+=(lib32stdc++6)
    ((${#packages[@]} == 0)) && return 0
    mapfile -t packages < <(printf '%s\n' "${packages[@]}" | sort -u)
    if has_apt; then
        apt_install "${packages[@]}" || fail "$EXIT_UNSUPPORTED" "Could not install ${packages[*]} with apt-get."
    else
        fail "$EXIT_UNSUPPORTED" "Install these first with your distribution's package manager: ${packages[*]} (and ICU and OpenSSL 3, which .NET needs, and the 32-bit libgcc and libstdc++, which SteamCMD needs)."
    fi
}

# Wine's loader where the agent looks for it (LinuxWine.Candidates): games only for Windows run with it.
has_wine() {
    local program
    for program in /usr/lib/wine/wine64 /usr/bin/wine64 /usr/bin/wine /opt/wine-stable/bin/wine64 /opt/wine-stable/bin/wine; do
        [[ -f $program && ! -L $program ]] && return 0
    done
    return 1
}

require_wine() {
    [[ $WINE == true ]] || return 0
    if has_wine; then
        step 'Wine is installed'
        return 0
    fi
    if has_apt; then
        apt_install wine64 || fail "$EXIT_UNSUPPORTED" 'Could not install wine64 with apt-get.'
    else
        fail "$EXIT_UNSUPPORTED" "Install Wine first with your distribution's package manager (its 64-bit loader: wine64 or wine)."
    fi
    has_wine || fail "$EXIT_UNSUPPORTED" "Wine was installed, but its loader is not where the agent looks for it (/usr/lib/wine/wine64, /usr/bin/wine64, /usr/bin/wine)."
}

take_lock() {
    exec 9>"$LOCK_FILE"
    flock -n 9 || fail "$EXIT_USAGE" 'Another run of this script is under way.'
}

# --- The node code ---------------------------------------------------------------------------------------------------
read_secrets_file() {
    [[ -z $SECRETS_FILE ]] && return 0
    [[ -f $SECRETS_FILE && ! -L $SECRETS_FILE ]] || fail "$EXIT_USAGE" "--secrets-file $SECRETS_FILE is not a file."
    path_owned_by_root "$SECRETS_FILE" || fail "$EXIT_USAGE" "--secrets-file $SECRETS_FILE, or a folder above it, can be changed by another user than root."
    local mode
    mode=$(stat -c %a -- "$SECRETS_FILE")
    (((8#$mode & 8#077) == 0)) || fail "$EXIT_USAGE" "--secrets-file $SECRETS_FILE can be read by other users than root (chmod 600 it)."
    jq -e 'type == "object"' "$SECRETS_FILE" >/dev/null 2>&1 || fail "$EXIT_USAGE" "--secrets-file $SECRETS_FILE is not a JSON object."
    local value
    value=$(jq -r '.nodeCode // empty' "$SECRETS_FILE")
    [[ -n $value ]] && NODE_CODE=$value
    return 0
}

read_node_code() {
    read_secrets_file
    if [[ -z $NODE_CODE ]]; then
        [[ $UNATTENDED == true ]] && fail "$EXIT_USAGE" 'The node code is not given (--unattended asks for nothing; use PULSAR_NODE_CODE or --secrets-file).'
        { : </dev/tty; } 2>/dev/null || fail "$EXIT_USAGE" 'The node code is not given, and there is no terminal to ask on (use PULSAR_NODE_CODE or --secrets-file).'
        printf 'Paste the node code (Nodes > Add node in the panel; it is not shown): ' >/dev/tty
        IFS= read -r -s NODE_CODE </dev/tty
        printf '\n' >/dev/tty
    fi
    NODE_CODE=${NODE_CODE//[[:space:]]/}
    # The agent checks it in full; this only keeps something else from being passed along.
    if [[ $NODE_CODE != pulsar-node-1.* ]] || has_control_characters "$NODE_CODE"; then
        fail "$EXIT_USAGE" 'That is not a node code: copy it again from Nodes > Add node (it starts with pulsar-node-1.). Nothing was changed.'
    fi
}

# --- Programs --------------------------------------------------------------------------------------------------------
# The release's agent copied beside its final place: root's, readable by everyone (a game's start runs it as the
# server's user), writable by root alone. Prints its folder.
stage_agent() {
    local staged=$AGENT_DIR.new-$$
    rm -rf -- "$staged"
    install -d -m 0755 -o root -g root "$INSTALL_ROOT"
    cp -a -- "$SCRIPT_DIR/agent" "$staged"
    cp -- "$SCRIPT_DIR/release.json" "$staged/release.json"
    chown -R root:root -- "$staged"
    chmod -R go-w,go+rX -- "$staged"
    chmod 0755 -- "$staged/PulsarAgent"
    printf '%s' "$staged"
}

install_script() {
    install -m 0755 -o root -g root -- "$SCRIPT_DIR/install-pulsar-agent.sh" "$INSTALL_ROOT/install-pulsar-agent.sh"
}

write_state() {
    install -d -m 0755 -o root -g root "$CONFIG_DIR" &&
        jq -n --arg version "$1" --arg at "$(date -u +%FT%TZ)" '{version: $version, installedAt: $at}' >"$STATE.new" &&
        chmod 0644 "$STATE.new" &&
        mv -- "$STATE.new" "$STATE"
}

agent_version() { release_version "$AGENT_DIR/release.json" 2>/dev/null || jq -r '.version // empty' "$STATE"; }

# Waits for the service to run (it says so to systemd once it does: Type=notify).
service_runs() {
    local _
    for _ in $(seq "$START_SECONDS"); do
        case $(systemctl is-active "$SERVICE" 2>/dev/null || true) in
        active) return 0 ;;
        failed) return 1 ;;
        esac
        sleep 1
    done
    return 1
}

# --- Install ---------------------------------------------------------------------------------------------------------
install_agent() {
    take_lock
    if [[ -e $AGENT_DIR || -f $STATE ]]; then
        fail "$EXIT_USAGE" "The agent is installed already ($AGENT_DIR). Update it with --update, enroll it with another panel with 'sudo $AGENT_DIR/PulsarAgent enroll --code -', or remove it with --uninstall first. Nothing was changed."
    fi
    local version
    version=$(release_version "$SCRIPT_DIR/release.json")
    read_node_code
    local enroll=(enroll --code -)
    [[ -n $SERVERS_ROOT ]] && enroll+=(--servers-root "$SERVERS_ROOT")
    [[ -n $BACKUPS_ROOT ]] && enroll+=(--backups-root "$BACKUPS_ROOT")

    step "Installing the Pulsar agent $version in $AGENT_DIR"
    local staged
    staged=$(stage_agent)
    # The record before the agent starts (it tells the panel it takes one-click updates only once the record is there),
    # the programs last: a step that fails leaves nothing half made, and the install can simply run again.
    if ! { install -d -m 0755 -o root -g root "$DATA_ROOT" && install_script && write_state "$version" && mv -- "$staged" "$AGENT_DIR"; }; then
        rm -rf -- "$staged" "$AGENT_DIR" "$STATE" "$INSTALL_ROOT/install-pulsar-agent.sh"
        fail "$EXIT_FAILED" "The agent's programs could not be put in place (see above: a full disk?). Nothing is installed: run the script again once that is fixed."
    fi

    step 'Enrolling this machine with the panel, and starting the agent'
    local status=0
    # The code goes on the agent's standard input: never on a command line, never in the log.
    printf '%s\n' "$NODE_CODE" | "$AGENT_DIR/PulsarAgent" "${enroll[@]}" || status=$?
    NODE_CODE=
    if ((status != 0)); then
        fail "$EXIT_FAILED" "The enrollment failed (see above): the agent's programs are in $AGENT_DIR. Fix what it says and run 'sudo $AGENT_DIR/PulsarAgent enroll --code -', or remove them with 'sudo $INSTALL_ROOT/install-pulsar-agent.sh --uninstall'."
    fi
    echo
    echo "The Pulsar agent $version runs ($SERVICE.service): this machine is a node of its panel. Its status: sudo $AGENT_DIR/PulsarAgent status"
}

# --- Update ----------------------------------------------------------------------------------------------------------
write_update_result() {
    local status=$1 from=$2 to=$3 parts=$4 failure=${5:-}
    install -d -m 0700 -o root -g root "$UPDATE_ROOT"
    jq -n --arg status "$status" --arg from "$from" --arg to "$to" --arg parts "$parts" --arg failure "$failure" \
        --arg attempt "$UPDATE_ATTEMPT" --arg at "$(date -u +%FT%TZ)" \
        '{status: $status, from: $from, to: $to, finishedAt: $at, parts: ($parts | split(" ") | map(select(. != "")))}
         + (if $failure != "" then {error: $failure} else {} end) + (if $attempt != "" then {attempt: $attempt} else {} end)' \
        >"$UPDATE_ROOT/last-update.json.new" &&
        mv -- "$UPDATE_ROOT/last-update.json.new" "$UPDATE_ROOT/last-update.json" &&
        rm -f -- "$AGENT_JOURNAL"
}

# Puts the previous agent back (when it was moved aside: an update that failed before that left it in place) and starts
# it; true when it runs again. Runs where errexit is off, so each step says whether it worked.
roll_back_agent() {
    systemctl stop "$SERVICE" 2>/dev/null || true
    if [[ -d $PREVIOUS_AGENT ]]; then
        rm -rf -- "$AGENT_DIR.failed" || return 1
        if [[ -e $AGENT_DIR ]]; then mv -- "$AGENT_DIR" "$AGENT_DIR.failed" || return 1; fi
        mv -- "$PREVIOUS_AGENT" "$AGENT_DIR" || return 1
    fi
    [[ -x $AGENT_DIR/PulsarAgent ]] || return 1
    # Its own unit again: the new agent may have written another.
    "$AGENT_DIR/PulsarAgent" service install >/dev/null 2>&1 || true
    systemctl start "$SERVICE" 2>/dev/null || true
    rm -rf -- "$AGENT_DIR.failed"
    service_runs
}

# Ends an update that failed once the agent was stopped: the previous agent back, its result written.
fail_update() {
    local from=$1 to=$2 failure=$3
    warn "The update failed ($failure): putting $from back."
    if roll_back_agent; then
        write_update_result rolled_back "$from" "$to" '' "$failure" || true
        fail "$EXIT_ROLLED_BACK" "The update to $to failed ($failure): the agent $from is back and runs. The log: $LOG_FILE."
    fi
    write_update_result failed "$from" "$to" '' "$failure, and putting the previous agent back failed too" || true
    fail "$EXIT_FAILED" "The update to $to failed ($failure), and putting $from back failed too. The previous agent's programs: $PREVIOUS_AGENT. The log: $LOG_FILE."
}

# An update the machine cut off (its journal is still there): rolled back, and its result written for the request it
# was, before anything else runs.
resume_cut_off_update() {
    [[ -f $AGENT_JOURNAL ]] || return 0
    local from to attempt
    from=$(jq -r '.from // empty' "$AGENT_JOURNAL" 2>/dev/null || true)
    to=$(jq -r '.to // empty' "$AGENT_JOURNAL" 2>/dev/null || true)
    attempt=$(jq -r '.attempt // empty' "$AGENT_JOURNAL" 2>/dev/null || true)
    warn "The update from ${from:-?} to ${to:-?} was cut off (the machine stopped during it): putting ${from:-the previous agent} back first."
    local requested=$UPDATE_ATTEMPT status=rolled_back
    [[ $attempt =~ ^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$ ]] || attempt=''
    roll_back_agent || status=failed
    UPDATE_ATTEMPT=$attempt
    write_update_result "$status" "$from" "$to" '' 'the update was cut off (the machine stopped during it)'
    UPDATE_ATTEMPT=$requested
    [[ $status == rolled_back ]] || fail "$EXIT_FAILED" "The cut-off update could not be rolled back: the previous agent's programs are in $PREVIOUS_AGENT."
    # Its version is the previous one again; a copy staged for it is no longer needed.
    if [[ -n $from ]]; then
        write_state "$from" || fail "$EXIT_FAILED" "The cut-off update is rolled back, but $STATE could not be written (a full disk?): run the update again once that is fixed."
    fi
    rm -rf -- "$AGENT_DIR".new-*
}

update_agent() {
    [[ -f $STATE ]] || fail "$EXIT_USAGE" "The agent is not installed by this script ($STATE is missing): install it first."
    take_lock
    resume_cut_off_update
    [[ -x $AGENT_DIR/PulsarAgent ]] || fail "$EXIT_FAILED" "The agent's programs are missing from $AGENT_DIR: install it again (--uninstall, then install)."
    local from to parts='agent'
    from=$(agent_version)
    to=$(release_version "$SCRIPT_DIR/release.json")
    if [[ -n $from && $(compare_versions "$to" "$from") == -1 ]]; then
        write_update_result failed "$from" "$to" '' "This release ($to) is older than the installed agent ($from)."
        fail "$EXIT_USAGE" "This release ($to) is older than the installed agent ($from). Nothing was changed."
    fi

    step "Updating the agent from $from to $to (its game servers keep running in their hosts)"
    local staged
    staged=$(stage_agent)
    install -d -m 0700 -o root -g root "$UPDATE_ROOT"
    rm -rf -- "$PREVIOUS_AGENT"
    jq -n --arg from "$from" --arg to "$to" --arg attempt "$UPDATE_ATTEMPT" '{from: $from, to: $to, attempt: $attempt}' >"$AGENT_JOURNAL.new"
    mv -- "$AGENT_JOURNAL.new" "$AGENT_JOURNAL"
    if ! systemctl stop "$SERVICE"; then
        rm -rf -- "$staged"
        write_update_result failed "$from" "$to" '' 'the agent could not be stopped'
        fail "$EXIT_FAILED" "The agent could not be stopped (see above): nothing was changed. The log: $LOG_FILE."
    fi
    # From here on the agent is stopped: whatever fails puts the previous one back and writes the result. The new agent
    # writes its own unit (what it needs from systemd may have changed), then runs.
    local failure=''
    if ! mv -- "$AGENT_DIR" "$PREVIOUS_AGENT"; then
        failure='the agent could not be moved aside'
    elif ! mv -- "$staged" "$AGENT_DIR"; then
        failure='the new agent could not be put in place'
    elif ! "$AGENT_DIR/PulsarAgent" service install >/dev/null || ! systemctl start "$SERVICE" || ! service_runs; then
        failure='the new agent did not start'
    elif ! install_script; then
        # Only once the new agent runs: a roll back keeps the script that goes with the previous one.
        failure='the script could not be copied'
    elif ! write_state "$to"; then
        failure='the new version could not be recorded'
    fi
    if [[ -n $failure ]]; then
        rm -rf -- "$staged"
        fail_update "$from" "$to" "$failure"
    fi
    # The agent's part is done: a cut from here on (the panel's update has its own journal) rolls nothing back.
    rm -f -- "$AGENT_JOURNAL"

    # This machine's panel too, when it has one: with its own checks, backup and roll back.
    if [[ -f $PANEL_STATE && -x $SCRIPT_DIR/install-pulsar.sh && -d $SCRIPT_DIR/panel ]]; then
        step "Updating this machine's panel to $to"
        local status=0
        "$SCRIPT_DIR/install-pulsar.sh" --update --unattended </dev/null || status=$?
        if ((status != 0)); then
            local failure="the agent is updated, but the panel's update failed (exit code $status; its log is in $SETUP_LOGS)"
            write_update_result "$([[ $status == 7 ]] && echo rolled_back || echo failed)" "$from" "$to" "$parts" "$failure"
            fail "$EXIT_FAILED" "The agent is updated to $to, but the panel's update failed (see above)."
        fi
        parts='agent panel'
    fi
    write_update_result succeeded "$from" "$to" "$parts"
    echo
    echo "Updated to $to ($parts). The previous agent's programs are in $PREVIOUS_AGENT."
}

# --- Uninstall -------------------------------------------------------------------------------------------------------
uninstall_agent() {
    take_lock
    if [[ -x $AGENT_DIR/PulsarAgent ]]; then
        step 'Removing this machine from Pulsar (its service, game servers, accounts, node key and settings)'
        "$AGENT_DIR/PulsarAgent" unenroll ||
            fail "$EXIT_FAILED" "The agent could not remove everything (see above): run this again once that is fixed. Its programs are still in $AGENT_DIR."
    fi
    step "Removing the agent's programs"
    rm -rf -- "$AGENT_DIR" "$AGENT_DIR".new-* "$STATE" "$INSTALL_ROOT/install-pulsar-agent.sh" "$PREVIOUS_AGENT" "$AGENT_JOURNAL"
    # The last update's result is this machine's panel's too, when it has one.
    [[ -f $PANEL_STATE ]] || rm -f -- "$UPDATE_ROOT/last-update.json"
    rmdir -- "$INSTALL_ROOT" 2>/dev/null || true
    echo
    echo 'The Pulsar agent is removed. The game servers'"'"' folders, backups and logs are kept (unenroll said where); delete the node in the panel too.'
}

# --- Main ------------------------------------------------------------------------------------------------------------
parse_options() {
    while (($# > 0)); do
        case $1 in
        --servers-root | --backups-root | --secrets-file | --update-attempt)
            (($# >= 2)) || fail "$EXIT_USAGE" "$1 needs a value."
            has_control_characters "$2" && fail "$EXIT_USAGE" "$1 holds a control character."
            case $1 in
            --servers-root) SERVERS_ROOT=$2 ;;
            --backups-root) BACKUPS_ROOT=$2 ;;
            --secrets-file) SECRETS_FILE=$2 ;;
            --update-attempt)
                [[ $2 =~ ^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$ ]] || fail "$EXIT_USAGE" '--update-attempt must be a request id.'
                UPDATE_ATTEMPT=$2
                ;;
            esac
            shift 2
            ;;
        --unattended)
            UNATTENDED=true
            shift
            ;;
        --wine)
            WINE=true
            shift
            ;;
        --update)
            MODE=update
            shift
            ;;
        --uninstall)
            MODE=uninstall
            shift
            ;;
        --help | -h)
            usage
            exit 0
            ;;
        *) fail "$EXIT_USAGE" "Unknown option $1 (see --help)." ;;
        esac
    done
    if [[ -n $SERVERS_ROOT && $SERVERS_ROOT != /* ]] || [[ -n $BACKUPS_ROOT && $BACKUPS_ROOT != /* ]]; then
        fail "$EXIT_USAGE" '--servers-root and --backups-root must be absolute paths.'
    fi
    # Each game's host has a /tmp and a /var/tmp of its own (PrivateTmp): a server there would be out of its game's sight.
    local servers
    servers=$(tr -s / <<<"$SERVERS_ROOT")
    case ${servers%/}/ in
    /tmp/* | /var/tmp/*) fail "$EXIT_USAGE" '--servers-root cannot be in /tmp or /var/tmp: each game server has those of its own.' ;;
    esac
}

main() {
    parse_options "$@"
    require_root
    start_log
    if [[ $MODE == uninstall ]]; then
        # It runs the installed agent as root: nobody else may have been able to replace it.
        require_trusted_parents
        if [[ -e $AGENT_DIR/PulsarAgent ]] && ! path_owned_by_root "$AGENT_DIR/PulsarAgent"; then
            fail "$EXIT_UNSUPPORTED" "$AGENT_DIR/PulsarAgent (or a folder above it) can be changed by another user than root: it is not run. Nothing was changed."
        fi
        uninstall_agent
        return
    fi

    require_system
    require_trusted_location
    require_release
    require_trusted_parents
    require_tools
    require_wine
    case $MODE in
    install) install_agent ;;
    update) update_agent ;;
    esac
}

# Run when executed, not when a test reads its functions.
if [[ ${BASH_SOURCE[0]} == "$0" ]]; then
    main "$@"
fi
