#!/usr/bin/env bash
# Checks the functions of the Linux install scripts (scripts/install/install-pulsar.sh, scripts/release/install.sh) that
# decide what to trust: the signed manifest, the tarball, addresses for the certificate, folders only root can change,
# versions. Nothing is installed. Run it on Linux with bash, openssl and jq (as root for the folder checks):
#   bash scripts/tests/test-install-scripts.sh
# Every check prints PASS or FAIL; the exit code is 1 on any FAIL. The scripts' main() runs only when they are executed,
# so their functions are read with "source", each script in a subshell of its own (they share function names).
set -uo pipefail
export LC_ALL=C

repo=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)
installer=$repo/scripts/install/install-pulsar.sh
one_command=$repo/scripts/release/install.sh
scratch=$(mktemp -d)
trap 'rm -rf -- "$scratch"' EXIT
failures=0

check() {
    if [[ $2 == 0 ]]; then
        printf 'PASS %s\n' "$1"
    else
        printf 'FAIL %s\n' "$1"
        failures=$((failures + 1))
    fi
}

# Whether a command (a subshell, since the scripts' fail() exits) succeeds.
passes() { ("$@") >/dev/null 2>&1; }
refuses() { ! ("$@") >/dev/null 2>&1; }

# --- install.sh: the signed manifest -----------------------------------------------------------------------------------
results=$(
    # shellcheck source=/dev/null
    source "$one_command"
    set +e
    work=$scratch/manifest
    mkdir -p "$work" "$scratch/keys"
    # Not in $work: verify_manifest writes its own key.pem there.
    key=$scratch/keys/signer.pem
    openssl ecparam -name prime256v1 -genkey -noout -out "$key"
    spki=$(openssl pkey -in "$key" -pubout -outform DER | base64 -w0)
    other=$(openssl ecparam -name prime256v1 -genkey -noout | openssl pkey -pubout -outform DER | base64 -w0)

    # Signs a manifest as tools/Pulsar.UpdateSigner does: the purpose and the manifest, each after its length, ECDSA
    # P-256 with SHA-256, r and s as 32 bytes each, in {"keyId", "signature"}.
    sign() {
        local manifest=$1 output=$2 id=${3:-}
        {
            be32 "${#SIGNATURE_PURPOSE}"
            printf '%s' "$SIGNATURE_PURPOSE"
            be32 "$(stat -c %s -- "$manifest")"
            cat -- "$manifest"
        } >"$work/signed"
        openssl dgst -sha256 -sign "$key" -out "$work/sig.der" "$work/signed"
        local r s
        r=$(openssl asn1parse -inform DER -in "$work/sig.der" | awk -F: '/INTEGER/ {print $NF}' | sed -n 1p)
        s=$(openssl asn1parse -inform DER -in "$work/sig.der" | awk -F: '/INTEGER/ {print $NF}' | sed -n 2p)
        r=$(printf '%064s' "${r: -64}" | tr ' ' 0)
        s=$(printf '%064s' "${s: -64}" | tr ' ' 0)
        jq -n --arg id "${id:-$(key_id "$spki")}" --arg signature "$(printf '%s%s' "$r" "$s" | bytes_of_hex | base64 -w0)" \
            '{keyId: $id, signature: $signature}' >"$output"
    }

    printf '{"product":"pulsar","version":"1.2.3","files":[{"name":"pulsar-1.2.3-linux-x64.tar.gz","size":1234,"sha256":"%s"}]}' \
        "$(printf 'a%.0s' {1..64})" >"$work/m.json"
    sign "$work/m.json" "$work/m.sig"
    passes verify_manifest "$work/m.json" "$work/m.sig" "$work" "$other" "$spki"
    echo "signed by a trusted key, among others: verified|$?"
    cp "$work/m.json" "$work/changed.json"
    sed -i 's/1.2.3/1.2.4/g' "$work/changed.json"
    refuses verify_manifest "$work/changed.json" "$work/m.sig" "$work" "$spki"
    echo "a manifest changed after it was signed: refused|$?"
    refuses verify_manifest "$work/m.json" "$work/m.sig" "$work" "$other"
    echo "signed by a key that is not trusted: refused|$?"
    sign "$work/m.json" "$work/lying.sig" "$(key_id "$other")"
    refuses verify_manifest "$work/m.json" "$work/lying.sig" "$work" "$other" "$spki"
    echo "a signature naming a trusted key it is not by: refused|$?"
    jq '.signature = "AAAA"' "$work/m.sig" >"$work/short.sig"
    refuses verify_manifest "$work/m.json" "$work/short.sig" "$work" "$spki"
    echo "a signature that is not 64 bytes: refused|$?"
    printf 'not json' >"$work/bad.sig"
    refuses verify_manifest "$work/m.json" "$work/bad.sig" "$work" "$spki"
    echo "a signature file that is not one: refused|$?"
    refuses verify_manifest "$work/m.json" "$work/m.sig" "$work" "$(openssl genrsa 2048 2>/dev/null | openssl pkey -pubout -outform DER | base64 -w0)"
    echo "a trusted key that is not P-256: refused|$?"
    [[ $(key_id "${TRUSTED_KEYS[0]}") == sha256:mIudURK-7CYCrht6ZxuX5B_v2ToaYyPGC5VEAWw4ibA ]]
    echo "the trusted key's id is its fingerprint, as the builds compute it|$?"
    built=$(grep -oE '"MF[A-Za-z0-9+/=]+"' "$repo/src/Pulsar.Contracts/Updates.cs" | tr -d '"' | paste -sd'|')
    [[ -n $built && $(printf '%s\n' "${TRUSTED_KEYS[@]}" | paste -sd'|') == "$built" ]]
    echo "it trusts exactly the update keys every Pulsar build trusts (UpdateKeys.Trusted)|$?"

    entry=$(tarball_entry "$work/m.json")
    [[ $entry == "1.2.3 pulsar-1.2.3-linux-x64.tar.gz 1234 $(printf 'a%.0s' {1..64})" ]]
    echo "the manifest names the tarball, its size and SHA-256|$?"
    jq '.product = "other"' "$work/m.json" >"$work/p.json"
    refuses tarball_entry "$work/p.json"
    echo "a manifest of another product: refused|$?"
    jq '.version = "1.2.3-beta"' "$work/m.json" >"$work/v.json"
    refuses tarball_entry "$work/v.json"
    echo "a version that is not major.minor.patch: refused|$?"
    jq '.files[0].size = 2000000000' "$work/m.json" >"$work/s.json"
    refuses tarball_entry "$work/s.json"
    echo "a tarball bigger than 1 GiB: refused|$?"
    jq '.files[0].sha256 = "../x"' "$work/m.json" >"$work/h.json"
    refuses tarball_entry "$work/h.json"
    echo "an entry without a SHA-256: refused|$?"

    # Tarballs: one top folder, plain folders and files only.
    top=pulsar-1.2.3-linux-x64
    mkdir -p "$work/t/$top/panel"
    echo x >"$work/t/$top/panel/a"
    tar -czf "$work/good.tgz" -C "$work/t" "$top"
    passes check_tarball "$work/good.tgz" "$top"
    echo "a tarball of one folder: taken|$?"
    ln -s /etc/shadow "$work/t/$top/panel/link"
    tar -czf "$work/link.tgz" -C "$work/t" "$top"
    refuses check_tarball "$work/link.tgz" "$top"
    echo "a tarball with a link: refused|$?"
    rm "$work/t/$top/panel/link"
    echo y >"$work/t/other"
    tar -czf "$work/outside.tgz" -C "$work/t" "$top" other
    refuses check_tarball "$work/outside.tgz" "$top"
    echo "a tarball with a file outside its folder: refused|$?"
    # GNU tar strips '..' from the names it packs: Python's tarfile writes them as given.
    for evil in "$top/../evil" "$top/panel/../../evil" "/$top/panel/a"; do
        python3 -I -c 'import io, sys, tarfile
with tarfile.open(sys.argv[1], "w:gz") as t:
    data = b"x"
    info = tarfile.TarInfo(sys.argv[2]); info.size = len(data)
    t.addfile(info, io.BytesIO(data))' "$work/evil.tgz" "$evil"
        refuses check_tarball "$work/evil.tgz" "$top"
        echo "a tarball with $evil: refused|$?"
    done
)
while IFS='|' read -r name status; do [[ -n $name ]] && check "one command: $name" "$status"; done <<<"$results"

# --- install-pulsar.sh: names, addresses, versions, folders ------------------------------------------------------------
results=$(
    # shellcheck source=/dev/null
    source "$installer"
    set +e
    trap - EXIT
    for address in 2001:db8::20 ::1 fe80::1 2001:0db8:0000:0000:0000:0000:0000:0001 ::ffff:192.168.1.2 1::; do
        valid_ipv6 "$address"
        echo "$address is an IPv6 address|$?"
    done
    for text in 2001:db8:::1 1:2:3:4:5:6:7:8:9 :1:2 1:2:3:4:5:6:7: g::1 1::2::3 ::1.2.3.256; do
        ! valid_ipv6 "$text"
        echo "$text is not an IPv6 address|$?"
    done
    [[ $(certificate_address 192.168.1.20) == 192.168.1.20 && $(certificate_address 2001:DB8::20) == 2001:db8::20 && -z $(certificate_address panel.example.com) ]]
    echo "an address is listed as an address and a name as a name|$?"
    for text in 10.1 192.168.1.300 0x7f 0.0.0.0 224.0.0.1 :: ff02::1 ::ffff:10.0.0.1; do
        ! certificate_address "$text" >/dev/null 2>&1
        echo "--host-name $text is refused|$?"
    done
    for name in 'panel.example.com' '*.example.com' 'a_b-c' '203.0.113.7' '2001:db8::7'; do
        valid_host_name "$name"
        echo "--host-name $name is taken|$?"
    done
    # shellcheck disable=SC2016 # a command substitution, as text, is what is refused
    for name in 'a b' 'x;rm' '$(id)' 'a..b' '*.*.example.com'; do
        ! valid_host_name "$name"
        echo "--host-name $name is refused|$?"
    done
    [[ $(compare_versions 1.0.34 1.0.33) == 1 && $(compare_versions 1.0.9 1.0.10) == -1 && $(compare_versions 2.0.0 2.0.0) == 0 ]]
    echo "versions compare by number|$?"
    private_ipv4 192.168.0.4 && private_ipv4 10.1.2.3 && private_ipv4 172.31.0.1 && ! private_ipv4 172.32.0.1 && ! private_ipv4 100.64.0.1
    echo "private addresses are RFC 1918's only|$?"
    pg_target 'Host=db.example.com;Port=6543;Database=pulsar;Username=panel;Password=p@ss;word;SSL Mode=VerifyFull'
    [[ $PG_TARGET == "host='db.example.com' port='6543' dbname='pulsar' user='panel'" && $PG_SSLMODE == verify-full && $PG_PASSWORD == 'p@ss' ]]
    echo "a connection string becomes libpq's, without its password|$?"
    pg_target 'Host=db.example.com;Database=pulsar;Username=panel;Password=dummy-only;SSL Mode=VerifyCA;Root Certificate=/fixture/custom ca.pem'
    [[ $PG_SSLMODE == verify-ca && $PG_SSLROOTCERT == '/fixture/custom ca.pem' ]]
    echo "VerifyCA and its configured root certificate are preserved|$?"
    (
        export PGSSLROOTCERT=/fixture/inherited.pem
        capture_policy() { [[ $PGSSLMODE == verify-ca && $PGSSLROOTCERT == '/fixture/custom ca.pem' && $PGPASSWORD == dummy-only ]]; }
        pg_external capture_policy && [[ $PGSSLROOTCERT == /fixture/inherited.pem ]]
    )
    echo "the external client receives the configured CA without changing its caller's environment|$?"
    pg_target 'Host=db.example.com;Database=pulsar;Username=panel;Password=dummy-only;SSL Mode=VerifyFull'
    [[ $PG_SSLMODE == verify-full && -z $PG_SSLROOTCERT ]]
    echo "a later connection without a custom CA does not retain the preceding setting|$?"
    # A value with a space or a quote stays one value (libpq's quoting), and a '*' or '?' is never a file name.
    globs=$scratch/globs
    mkdir -p "$globs"
    touch "$globs/Database=dx" "$globs/Password=pxy"
    (
        cd "$globs" || exit 1
        pg_target "Host=h;Database=d*;Username=o'x\\y z;Password=p*?[a]"
        [[ $PG_TARGET == "host='h' port='5432' dbname='d*' user='o\\'x\\\\y z'" && $PG_PASSWORD == 'p*?[a]' ]]
    )
    echo "a connection string with *, ?, a space and a quote is read as written and quoted|$?"
    if [[ $(id -u) == 0 ]]; then
        root_folder=$(mktemp -d /root/pulsar-test.XXXXXX)
        path_owned_by_root "$root_folder"
        echo "a folder of root's under /root: trusted|$?"
        chmod 0777 "$root_folder"
        ! path_owned_by_root "$root_folder"
        echo "a folder others may write: not trusted|$?"
        rm -rf "$root_folder"
        sticky=$(mktemp -d /tmp/pulsar-test.XXXXXX)
        path_owned_by_root "$sticky"
        echo "a folder of root's in /tmp (sticky): trusted|$?"
        chown nobody "$sticky"
        ! path_owned_by_root "$sticky"
        echo "a folder another user owns: not trusted|$?"
        rm -rf "$sticky"
        logs=$(mktemp -d /root/pulsar-test.XXXXXX)
        chmod 0775 "$logs" && chgrp adm "$logs"
        system_group_folder "$logs"
        echo "a log folder a system group may write (as Ubuntu's /var/log): trusted|$?"
        chgrp nogroup "$logs" && chmod 0775 "$logs"
        ! system_group_folder "$logs"
        echo "a folder a group of users (gid 1000 or more) may write: not trusted|$?"
        chgrp adm "$logs" && chmod 0777 "$logs"
        ! system_group_folder "$logs"
        echo "a folder everyone may write: not trusted|$?"
        rm -rf "$logs"
    else
        echo "the folder checks need root (skipped)|0"
    fi
)
while IFS='|' read -r name status; do [[ -n $name ]] && check "installer: $name" "$status"; done <<<"$results"

# --- install-pulsar.sh: update backups and roll back -------------------------------------------------------------------
# The script's folders are readonly: a copy of it points them into the scratch folder, and what a roll back runs on the
# machine (systemd, PostgreSQL, the panel) is replaced by stubs that note their calls.
# shellcheck disable=SC2034,SC2317 # the stubs and variables are the sourced installer's, called and read by it
results=$(
    root=$scratch/machine
    mkdir -p "$root"
    sed -E "s#^(readonly (INSTALL_ROOT|CONFIG_DIR|DATA_ROOT|LOG_ROOT))=/#\\1=$root/#" "$installer" >"$scratch/installer-copy.sh"
    # shellcheck source=/dev/null
    source "$scratch/installer-copy.sh"
    set +e
    trap - EXIT
    mkdir -p "$UPDATE_ROOT/backups" "$UPDATE_ROOT/previous/panel" "$PANEL_DIR" "$CONFIG_DIR"
    calls=$scratch/calls
    : >"$calls"
    LOG_FILE=$scratch/setup.log
    systemctl() { echo "systemctl $*" >>"$calls"; }
    write_unit() { :; }
    panel_client() { :; }
    panel_pin_from_settings() { :; }
    wait_panel_ready() { :; }
    applied_migrations() { echo 002; }
    restore_database_dump() { echo "restore $1" >>"$calls"; [[ $RESTORE_WORKS == true ]]; }

    backup=$UPDATE_ROOT/backups/1.0.0-20260101-000000
    mkdir -p "$backup"
    echo dump >"$backup/pulsar.dump"
    echo 001 >"$backup/migrations.txt"

    # roll_back_update turns errexit back on when it ends: a failing status must not end this subshell.
    rolls_back() {
        local status=0
        roll_back_update "$backup" "$backup/migrations.txt" >/dev/null 2>&1 || status=$?
        set +e
        return "$status"
    }
    RESTORE_WORKS=false
    ! rolls_back
    echo "a roll back whose database could not be put back fails|$?"
    [[ -f $backup/.keep ]]
    echo "and keeps that backup from pruning|$?"
    ! grep -q 'systemctl start' "$calls"
    echo "and leaves the panel stopped, not on an empty database|$?"

    rm -f "$backup/.keep"
    mkdir -p "$UPDATE_ROOT/previous/panel"
    : >"$calls"
    RESTORE_WORKS=true
    rolls_back
    [[ $? == 0 && ! -e $backup/.keep ]] && grep -q 'systemctl start' "$calls"
    echo "a roll back that puts the database back succeeds and starts the panel|$?"

    # Five backups, newest last; the oldest one is marked.
    rm -rf "$UPDATE_ROOT/backups"
    for n in 1 2 3 4 5; do
        mkdir -p "$UPDATE_ROOT/backups/b$n"
        touch -d "2026-01-0$n" "$UPDATE_ROOT/backups/b$n"
    done
    : >"$UPDATE_ROOT/backups/b1/.keep"
    touch -d 2026-01-01 "$UPDATE_ROOT/backups/b1"
    prune_update_backups
    [[ -d $UPDATE_ROOT/backups/b1 && ! -d $UPDATE_ROOT/backups/b2 && -d $UPDATE_ROOT/backups/b3 && -d $UPDATE_ROOT/backups/b5 ]]
    echo "pruning keeps the three newest backups and any marked .keep|$?"

    # The EXIT trap of an update cut off after the panel was stopped.
    roll_back_update() { echo "roll_back $1" >>"$calls"; [[ $ROLL_BACK_WORKS == true ]]; }
    guard_run() (
        : >"$calls"
        rm -f "$JOURNAL" "$UPDATE_ROOT/last-update.json"
        [[ $1 == journal ]] && echo '{}' >"$JOURNAL"
        UPDATE_GUARD=("$backup" 1.0.0 1.0.1)
        trap roll_back_cut_off_update EXIT
        exit 1
    )
    ROLL_BACK_WORKS=true
    guard_run journal >/dev/null 2>&1
    status=$?
    [[ $status == "$EXIT_ROLLED_BACK" && $(jq -r .status "$UPDATE_ROOT/last-update.json") == rolled_back ]] && grep -q "roll_back $backup" "$calls"
    echo "an update cut off with its journal left is rolled back, exit 7|$?"
    ROLL_BACK_WORKS=false
    guard_run journal >/dev/null 2>&1
    status=$?
    [[ $status == "$EXIT_FAILED" && $(jq -r .status "$UPDATE_ROOT/last-update.json") == failed ]]
    echo "and when that fails it is reported as failed, exit 1|$?"
    guard_run none >/dev/null 2>&1
    status=$?
    [[ $status == 1 && ! -s $calls ]]
    echo "with no journal left (already rolled back or done) nothing is rolled back|$?"
)
while IFS='|' read -r name status; do [[ -n $name ]] && check "update: $name" "$status"; done <<<"$results"

if command -v shellcheck >/dev/null 2>&1; then
    (cd "$repo" && shellcheck -x scripts/install/install-pulsar.sh scripts/install/backup-pulsar.sh scripts/release/install.sh scripts/tests/test-install-scripts.sh)
    check 'shellcheck finds nothing in the scripts' "$?"
fi

if ((failures > 0)); then
    printf '%d check(s) failed\n' "$failures"
    exit 1
fi
echo 'All checks passed'
