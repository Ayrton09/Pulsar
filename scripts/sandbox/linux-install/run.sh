#!/usr/bin/env bash
# Runs the Linux panel's install checks on a disposable machine (the CI runner, or a throwaway VM/WSL distro), as root:
# the release's install-pulsar.sh installs the panel as docs/guides/install.md ("Linux") describes, then it is backed up
# with backup-pulsar.sh, updated (a release that works, then one that breaks after changing the database, which must
# be rolled back), refused an older release, its certificate renewed, removed, restored from the backup, and installed
# with ufw active. It changes the machine (PostgreSQL's 'pulsar' database, the 'pulsar' user, ufw): never run it on a
# machine you keep.
#   sudo bash scripts/sandbox/linux-install/run.sh <the extracted release folder> <report folder>
# The release folder is pulsar-<version>-linux-x64 as the tarball extracts (root's, unchangeable by others). Results
# go to <report folder>/summary.txt (PASS/FAIL lines); the exit code is 1 on any FAIL.
set -uo pipefail
export LC_ALL=C

release=$(cd -- "${1:?the extracted release folder}" && pwd -P)
report=${2:?the report folder}
[[ $(id -u) == 0 ]] || { echo 'Run it as root.' >&2; exit 3; }
mkdir -p "$report"
summary=$report/summary.txt
: >"$summary"
failures=0

check() {
    local name=$1 ok=$2 detail=${3:-}
    if [[ $ok == 0 ]]; then
        printf 'PASS %s\n' "$name" | tee -a "$summary"
    else
        printf 'FAIL %s%s\n' "$name" "${detail:+ - $detail}" | tee -a "$summary"
        failures=$((failures + 1))
    fi
}

# A copy of the release as another version (its release.json), in a folder of root's; "broken" makes its panel one
# whose migration changes the database and which never starts.
stage() {
    local version=$1 kind=${2:-} folder=/root/pulsar-check-$1
    rm -rf "$folder"
    mkdir -m 0700 "$folder"
    cp -a "$release/." "$folder/"
    jq --arg version "$version" '.version = $version' "$release/release.json" >"$folder/release.json"
    if [[ $kind == broken ]]; then
        cat >"$folder/panel/Pulsar.Api" <<'EOF'
#!/bin/sh
if [ "$1" = migrate ]; then
  exec psql -X -q "host=/var/run/postgresql dbname=pulsar user=pulsar" -c "INSERT INTO \"__EFMigrationsHistory\" VALUES ('29991231000000_Broken', '10.0.0')"
fi
exit 1
EOF
    fi
    chown -R root:root "$folder"
    chmod -R go-w "$folder"
    printf '%s' "$folder"
}

secrets=/root/pulsar-check-secrets.json
(umask 077 && jq -n --arg password "$(head -c 24 /dev/urandom | base64 | tr -d '/+=' | head -c 24)" '{adminPassword: $password}' >"$secrets")

pin() {
    openssl x509 -in /var/lib/pulsar/panel/tls/panel.crt -noout -pubkey | openssl pkey -pubin -outform DER |
        openssl dgst -sha256 -binary | base64 -w0
}

# Signs in as the first administrator: the HTTP status of the sign-in.
sign_in() {
    local jar headers token status
    jar=$(mktemp)
    headers=$(mktemp)
    curl -sS -k --pinnedpubkey "sha256//$(pin)" -c "$jar" -o /dev/null https://localhost:8443/api/v1/auth/session
    token=$(awk '$6 == "__Host-pulsar-xsrf" {print $7}' "$jar")
    printf 'X-XSRF-TOKEN: %b\n' "${token//%/\\x}" >"$headers"
    status=$(PASSWORD=$(jq -r .adminPassword "$secrets") jq -n '{userName: "admin", password: env.PASSWORD, rememberMe: false}' |
        curl -sS -k --pinnedpubkey "sha256//$(pin)" -b "$jar" -c "$jar" -H @"$headers" -H 'Content-Type: application/json' \
            --data-binary @- -o /dev/null -w '%{http_code}' https://localhost:8443/api/v1/auth/login)
    rm -f "$jar" "$headers"
    printf '%s' "$status"
}

sftp_key() { journalctl -u pulsar-panel --no-pager -o cat | grep -oE 'SFTP host key SHA256:[A-Za-z0-9+/]+' | tail -1; }
pulsar_db() { runuser -u pulsar -- psql -X -qtA -c "$1" 'host=/var/run/postgresql dbname=pulsar user=pulsar'; }

echo "== Install (release $(jq -r .version "$release/release.json"))"
first=$(stage 9.0.0)
# A machine whose /opt others may change (GitHub's runners have one) gets no install: whoever could replace /opt/pulsar
# would have their programs run as root by the backups. Then /opt is made root's, as on an ordinary machine.
if [[ $(stat -c %u /opt) != 0 || $((8#$(stat -c %a /opt) & 8#022)) != 0 ]]; then
    "$first/install-pulsar.sh" --unattended --secrets-file "$secrets" >"$report/install-open-opt.log" 2>&1
    code=$?
    [[ $code == 3 && ! -e /opt/pulsar/panel ]] && grep -q 'can be changed by another user than root' "$report/install-open-opt.log"
    check "install where /opt is open to others ($(stat -c '%U %a' /opt)): refused, nothing changed" "$?" "exit code $code"
    chown root:root /opt
    chmod go-w /opt
fi
"$first/install-pulsar.sh" --unattended --secrets-file "$secrets" >"$report/install.log" 2>&1
check 'install: exit code 0' "$?" "$(tail -3 "$report/install.log")"
systemctl is-active --quiet pulsar-panel
check 'install: the service runs' "$?"
[[ $(stat -c '%a %U %G' /etc/pulsar/panel.json) == '640 root pulsar' ]]
check 'install: the settings are root'"'"'s, readable by the panel only' "$?" "$(stat -c '%a %U %G' /etc/pulsar/panel.json)"
! jq -e 'has("InitialSetup")' /etc/pulsar/panel.json >/dev/null
check 'install: the setup token is gone from the settings' "$?"
[[ $(stat -c '%a %U' /var/lib/pulsar/panel/keys) == '700 pulsar' && $(stat -c '%a %U %G' /var/lib/pulsar/panel/keyring/keyring.pfx) == '640 root pulsar' ]]
check 'install: the keys are the panel'"'"'s alone' "$?"
[[ $(ps -o user= -C Pulsar.Api | head -1) == pulsar ]]
check 'install: the panel runs as pulsar' "$?"
grep -q 'CapEff:\s*0000000000000400' "/proc/$(pgrep -f /opt/pulsar/panel/Pulsar.Api | head -1)/status"
check 'install: the panel holds CAP_NET_BIND_SERVICE and nothing more' "$?"
exposure=$(systemd-analyze security pulsar-panel --no-pager 2>/dev/null | sed -nE 's/.*Overall exposure level for pulsar-panel.service: ([0-9.]+).*/\1/p')
awk -v e="${exposure:-10}" 'BEGIN { exit !(e <= 3.0) }'
check "install: systemd rates the service's exposure low (${exposure:-?})" "$?"
[[ $(sign_in) == 200 ]]
check 'install: the administrator signs in' "$?"
! journalctl -u pulsar-panel --no-pager -o cat | grep -q 'No XML encryptor configured'
check 'install: the Data Protection keys are protected by the key ring certificate' "$?"
key_before=$(sftp_key)

echo '== Backup'
/opt/pulsar/backup-pulsar.sh --destination /root/pulsar-check-backups >"$report/backup.log" 2>&1
check 'backup: exit code 0' "$?" "$(tail -3 "$report/backup.log")"
backup=$(find /root/pulsar-check-backups -mindepth 1 -maxdepth 1 -type d -name 'pulsar-backup-*' | sort | tail -1)
(cd "$backup" && jq -r '.files | to_entries[] | "\(.value)  \(.key)"' backup.json | sha256sum --quiet -c -) >/dev/null 2>&1
check 'backup: every file is what backup.json says' "$?"
[[ $(jq -r '.pulsarVersion' "$backup/backup.json") == 9.0.0 && $(jq '.migrations | length' "$backup/backup.json") -gt 10 ]]
check 'backup: backup.json names the version and the migrations' "$?"
[[ $(stat -c '%a %U' "$backup") == '700 root' ]]
check 'backup: the folder is root'"'"'s alone' "$?"

echo '== Updates'
"$(stage 9.0.1)/install-pulsar.sh" --update >"$report/update.log" 2>&1
check 'update: exit code 0' "$?" "$(tail -3 "$report/update.log")"
[[ $(jq -r .status /var/lib/pulsar/updates/last-update.json) == succeeded && $(jq -r .version /opt/pulsar/panel/release.json) == 9.0.1 ]]
check 'update: 9.0.1 is installed and recorded' "$?"
"$(stage 9.0.2 broken)/install-pulsar.sh" --update >"$report/update-broken.log" 2>&1
code=$?
[[ $code == 7 ]]
check 'update to a release that does not start: exit code 7' "$?" "exit code $code"
[[ $(jq -r .version /opt/pulsar/panel/release.json) == 9.0.1 && $(jq -r .status /var/lib/pulsar/updates/last-update.json) == rolled_back ]]
check 'update to a release that does not start: the previous version is back' "$?"
[[ $(pulsar_db "SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE migration_id LIKE '2999%'") == 0 ]]
check 'update to a release that does not start: the database is as it was' "$?"
systemctl is-active --quiet pulsar-panel && [[ $(sign_in) == 200 ]]
check 'update to a release that does not start: the panel runs and signs in' "$?"
"$(stage 8.0.0)/install-pulsar.sh" --update >/dev/null 2>&1
code=$?
[[ $code == 2 && $(jq -r .version /opt/pulsar/panel/release.json) == 9.0.1 ]]
check 'update to an older release: refused, nothing changed' "$?" "exit code $code"
# An update cut off right after the programs moved away (the machine lost power): the next run puts them back first.
systemctl stop pulsar-panel
latest=$(find /var/lib/pulsar/updates/backups -mindepth 1 -maxdepth 1 -type d | sort | tail -1)
rm -rf /var/lib/pulsar/updates/previous && mkdir -m 0700 /var/lib/pulsar/updates/previous
mv /opt/pulsar/panel /var/lib/pulsar/updates/previous/panel
jq -n --arg backup "$latest" '{from: "9.0.1", to: "9.0.3", backup: $backup, stage: "swapping"}' >/var/lib/pulsar/updates/journal.json
"$(stage 9.0.3)/install-pulsar.sh" --update >"$report/update-resumed.log" 2>&1
check 'update after one cut off mid-swap: exit code 0' "$?" "$(tail -3 "$report/update-resumed.log")"
grep -q 'cut off while it was swapping' "$report/update-resumed.log" && [[ $(jq -r .version /opt/pulsar/panel/release.json) == 9.0.3 && $(sign_in) == 200 ]]
check 'update after one cut off mid-swap: rolled back first, then updated, and the panel signs in' "$?"

echo '== Certificate'
old=$(openssl x509 -in /var/lib/pulsar/panel/tls/panel.crt -noout -fingerprint -sha256)
/opt/pulsar/install-pulsar.sh --renew-certificate --host-name panel.check.test >"$report/renew.log" 2>&1
check 'renew: exit code 0' "$?" "$(tail -3 "$report/renew.log")"
[[ $(openssl x509 -in /var/lib/pulsar/panel/tls/panel.crt -noout -fingerprint -sha256) != "$old" ]] &&
    openssl x509 -in /var/lib/pulsar/panel/tls/panel.crt -noout -ext subjectAltName | grep -q 'DNS:panel.check.test'
check 'renew: a new certificate with the new name' "$?"
[[ $(sign_in) == 200 ]]
check 'renew: the panel answers with it' "$?"

echo '== Uninstall and restore'
/opt/pulsar/install-pulsar.sh --uninstall >"$report/uninstall.log" 2>&1
check 'uninstall: exit code 0' "$?"
[[ ! -e /etc/systemd/system/pulsar-panel.service && ! -e /opt/pulsar && ! -e /etc/pulsar/panel.json ]]
check 'uninstall: the service, programs and settings are gone' "$?"
[[ $(runuser -u postgres -- psql -X -qtA -c "SELECT count(*) FROM pg_database WHERE datname = 'pulsar'") == 1 ]]
check 'uninstall: the database is kept' "$?"
"$first/install-pulsar.sh" --unattended --secrets-file "$secrets" >/dev/null 2>&1
code=$?
[[ $code == 2 ]]
check 'install over the kept database: refused' "$?" "exit code $code"
runuser -u postgres -- psql -Xq -c 'DROP DATABASE pulsar WITH (FORCE)' -c 'DROP ROLE pulsar'
rm -rf /var/lib/pulsar/panel
"$(stage 9.0.1)/install-pulsar.sh" --unattended --restore-from "$backup" >"$report/restore.log" 2>&1
check 'restore: exit code 0' "$?" "$(tail -3 "$report/restore.log")"
[[ $(sign_in) == 200 ]]
check 'restore: the backup'"'"'s administrator signs in' "$?"
[[ -n $key_before && $(sftp_key) == "$key_before" ]]
check 'restore: the panel reads its secrets again (the same SFTP host key)' "$?"

echo '== What another user could do'
/opt/pulsar/install-pulsar.sh --uninstall >/dev/null 2>&1
runuser -u postgres -- psql -Xq -c 'DROP DATABASE pulsar WITH (FORCE)' -c 'DROP ROLE pulsar'
hba=$(runuser -u postgres -- psql -X -qtA -c 'SHOW hba_file')
cp -p "$hba" "$report/pg_hba.conf.saved"
printf 'host all all 127.0.0.1/32 trust
' >>"$hba"
runuser -u postgres -- psql -Xq -c 'SELECT pg_reload_conf()' >/dev/null
"$first/install-pulsar.sh" --unattended --secrets-file "$secrets" >/dev/null 2>&1
code=$?
cp -p "$report/pg_hba.conf.saved" "$hba"
runuser -u postgres -- psql -Xq -c 'SELECT pg_reload_conf()' >/dev/null
[[ $code == 3 && ! -e /opt/pulsar/panel ]]
check 'a pg_hba.conf rule trusting local connections stops the install, nothing changed' "$?" "exit code $code"
runuser -u pulsar -- ln -s /etc/pulsar-canary /var/lib/pulsar/panel/keys/key-planted.xml 2>/dev/null
"$first/install-pulsar.sh" --unattended --secrets-file "$secrets" >"$report/install-again.log" 2>&1
check 'install over the keys an earlier panel left: exit code 0' "$?" "$(tail -3 "$report/install-again.log")"
[[ ! -e /etc/pulsar-canary && -z $(find /var/lib/pulsar/panel/keys -type l) ]] &&
    [[ -n $(find /var/lib/pulsar/panel -maxdepth 1 -name 'keys.before-*' -user root) ]]
check 'the earlier keys (and a link the panel'"'"'s user put there) are moved aside, never written through' "$?"
runuser -u pulsar -- ln -s /etc/shadow /var/lib/pulsar/panel/keys/key-link.xml 2>/dev/null
/opt/pulsar/backup-pulsar.sh --destination /root/pulsar-check-backups2 >"$report/backup-link.log" 2>&1
check 'backup with a link in the keys: exit code 0' "$?" "$(tail -3 "$report/backup-link.log")"
linked=$(find /root/pulsar-check-backups2 -mindepth 1 -maxdepth 1 -type d | sort | tail -1)
[[ ! -e $linked/panel-data/keys/key-link.xml ]] && grep -q 'not a plain key file' "$report/backup-link.log"
check 'backup with a link in the keys: the link is left out, with a warning' "$?"
rm -f /var/lib/pulsar/panel/keys/key-link.xml

echo '== Firewall'
if command -v ufw >/dev/null 2>&1; then
    /opt/pulsar/install-pulsar.sh --uninstall >/dev/null 2>&1
    runuser -u postgres -- psql -Xq -c 'DROP DATABASE pulsar WITH (FORCE)' -c 'DROP ROLE pulsar'
    ufw --force enable >/dev/null
    # A second network with a router (a worse route than the real one): the certificate lists both private addresses.
    ip link add pulsar-check0 type dummy && ip addr add 192.168.77.5/24 dev pulsar-check0 && ip link set pulsar-check0 up &&
        ip route add default via 192.168.77.1 dev pulsar-check0 metric 4000
    "$first/install-pulsar.sh" --unattended --secrets-file "$secrets" --sftp-port 2222 >"$report/install-ufw.log" 2>&1
    check 'ufw and two private addresses: install exit code 0, with its summary' "$?" "$(tail -3 "$report/install-ufw.log")"
    grep -q "installed and running" "$report/install-ufw.log" &&
        openssl x509 -in /var/lib/pulsar/panel/tls/panel.crt -noout -ext subjectAltName | grep -q 'IP Address:192.168.77.5'
    check 'two private addresses: both in the certificate, and the summary printed' "$?"
    ip link del pulsar-check0 2>/dev/null
    ufw status | grep -qE '^8443/tcp +ALLOW' && ufw status | grep -qE '^2222/tcp +ALLOW'
    check 'ufw: the panel'"'"'s ports are allowed' "$?"
    /opt/pulsar/install-pulsar.sh --uninstall >/dev/null 2>&1
    ! ufw status | grep -q 'Pulsar panel'
    check 'ufw: uninstall removes the rules it made' "$?"
    ufw --force disable >/dev/null
else
    echo 'SKIP ufw is not installed' | tee -a "$summary"
fi

echo
cat "$summary"
if ((failures > 0)); then
    echo "$failures check(s) failed"
    exit 1
fi
echo 'All checks passed'
