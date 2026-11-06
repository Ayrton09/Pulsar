#!/usr/bin/env bash
# Installs Pulsar's panel on this Linux machine from the newest release on GitHub (docs/guides/install.md, "Linux"):
#
#   curl -fsSL https://github.com/Ayrton09/Pulsar/releases/latest/download/install.sh | sudo bash
#
# Options after "bash -s --" go to install-pulsar.sh, for example:
#   curl -fsSL .../install.sh | sudo bash -s -- --host-name panel.example.com
#   curl -fsSL .../install.sh | sudo bash -s -- --update
#
# Before it runs anything, it checks the release as installed Pulsars check an update: the release's manifest
# (pulsar-update.json) must be signed by the project's update key, which this script carries (the keys of
# UpdateKeys.Trusted in the programs), and the Linux tarball must have the size and SHA-256 that signed manifest gives.
# The tarball is downloaded into a new folder only root can use, checked (one folder, no links, nothing outside it),
# extracted there, and its install-pulsar.sh run; the folder is removed afterwards. A release that anyone without the
# maintainer's key published or changed on GitHub is refused.
#
# This script is what GitHub serves for the newest release, over HTTPS, so that line trusts GitHub for it as a download
# from the Releases page does. To read it first: download it (the signed manifest names it, with its SHA-256, and it has
# its own .sha256 file), check it, and run it with sudo bash ./install.sh.
set -Eeuo pipefail
# A failed command inside $(...) ends it too (bash clears -e there otherwise); a pipe whose reader stops early (head,
# grep -q) is never used on a long output, since pipefail would count the writer's SIGPIPE as a failure.
shopt -s inherit_errexit
umask 077
export LC_ALL=C
export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin

readonly REPOSITORY=Ayrton09/Pulsar
readonly RELEASE_URL=https://github.com/$REPOSITORY/releases/latest/download
# The update keys' public keys (ECDSA P-256, SubjectPublicKeyInfo in base64): UpdateKeys.Trusted, the same list.
readonly TRUSTED_KEYS=(
    'MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEDoveNpq4aiT1Pmog9P4f2egClTyuGYeYtyaG9S6MVS15A31pequgC+TyKe0E7zDKqY7HXWwZWiY65JZ4vpafXA=='
)
readonly SIGNATURE_PURPOSE=pulsar-update-manifest-v1
readonly MANIFEST_NAME=pulsar-update.json
readonly SIGNATURE_NAME=pulsar-update.json.sig
readonly MAX_MANIFEST_BYTES=65536
readonly MAX_SIGNATURE_BYTES=4096
readonly MAX_FILE_BYTES=1073741824
readonly DOWNLOAD_HOSTS=(github.com objects.githubusercontent.com release-assets.githubusercontent.com)
readonly VERSION_PATTERN='^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})$'
# A P-256 key's SubjectPublicKeyInfo up to its point (hex): id-ecPublicKey, prime256v1, a 66-byte BIT STRING, uncompressed.
readonly P256_KEY_PREFIX=3059301306072a8648ce3d020106082a8648ce3d03010703420004

fail() {
    printf 'ERROR: %s\n' "$1" >&2
    exit "${2:-1}"
}

hex_of() { od -An -v -tx1 | tr -d ' \n'; }

# Hexadecimal text on stdin as the bytes it spells.
bytes_of_hex() {
    local hex escaped='' i
    hex=$(cat)
    for ((i = 0; i < ${#hex}; i += 2)); do escaped+="\\x${hex:i:2}"; done
    printf '%b' "$escaped"
}

# n as 4 bytes, big-endian.
be32() {
    printf '%08x' "$1" | bytes_of_hex
}

# A trusted key's id: "sha256:" and the base64url SHA-256 of its SubjectPublicKeyInfo (ProtocolCrypto.Fingerprint).
key_id() {
    printf 'sha256:%s' "$(printf '%s' "$1" | base64 -d | openssl dgst -sha256 -binary | base64 -w0 | tr '+/' '-_' | tr -d '=')"
}

# One DER INTEGER from an unsigned big-endian number in hex: leading zero bytes dropped, one added when the top bit is set.
der_integer() {
    local value=$1
    while [[ ${#value} -gt 2 && ${value:0:2} == 00 ]]; do value=${value:2}; done
    if ((16#${value:0:1} >= 8)); then value=00$value; fi
    printf '02%02x%s' $((${#value} / 2)) "$value"
}

# Exits unless signature_file (the .sig: {"keyId", "signature"}) is a trusted key's signature of manifest_file: the key
# its keyId names among the given keys, a 64-byte P-256 signature (r and s) of the signed data, which is the purpose and
# then the manifest, each after its length in 4 bytes (ProtocolCrypto.SignedData).
verify_manifest() {
    local manifest=$1 signature_file=$2 work=$3
    shift 3
    local key_id_named signature key='' candidate
    key_id_named=$(jq -r 'if type == "object" and (.keyId | type) == "string" and (.signature | type) == "string" then .keyId else empty end' "$signature_file" 2>/dev/null) ||
        key_id_named=''
    [[ -n $key_id_named ]] || fail "The release's signature file is not one: it is not installed."
    signature=$(jq -r '.signature' "$signature_file")
    for candidate in "$@"; do
        local spki
        spki=$(printf '%s' "$candidate" | base64 -d 2>/dev/null | hex_of) || spki=''
        [[ ${#spki} -eq 182 && ${spki:0:54} == "$P256_KEY_PREFIX" ]] || fail 'A trusted key is not a P-256 public key.'
        if [[ $(key_id "$candidate") == "$key_id_named" ]]; then
            key=$candidate
            break
        fi
    done
    [[ -n $key ]] || fail "The release is signed by $(printf '%s' "$key_id_named" | tr -c 'A-Za-z0-9:_-' '?'), a key this script does not trust: it is not installed."
    local raw
    raw=$(printf '%s' "$signature" | base64 -d 2>/dev/null | hex_of) || raw=''
    [[ ${#raw} -eq 128 ]] || fail "The release's signature does not match its manifest: it is not installed."
    local body
    body=$(der_integer "${raw:0:64}")$(der_integer "${raw:64:64}")
    printf '30%02x%s' $((${#body} / 2)) "$body" | bytes_of_hex >"$work/signature.der"
    {
        printf '%s\n' '-----BEGIN PUBLIC KEY-----'
        fold -w 64 <<<"$key"
        printf '%s\n' '-----END PUBLIC KEY-----'
    } >"$work/key.pem"
    {
        be32 "${#SIGNATURE_PURPOSE}"
        printf '%s' "$SIGNATURE_PURPOSE"
        be32 "$(stat -c %s -- "$manifest")"
        cat -- "$manifest"
    } >"$work/signed.bin"
    openssl dgst -sha256 -verify "$work/key.pem" -signature "$work/signature.der" "$work/signed.bin" >/dev/null 2>&1 ||
        fail "The release's signature does not match its manifest: it is not installed."
}

# The tarball the signed manifest names for this machine: prints "version name size sha256", or exits.
tarball_entry() {
    local manifest=$1 version name entry
    version=$(jq -r 'if .product == "pulsar" and (.version | type) == "string" then .version else empty end' "$manifest" 2>/dev/null) || version=''
    [[ $version =~ $VERSION_PATTERN ]] || fail "The release's manifest is not one of Pulsar's: it is not installed."
    name="pulsar-$version-linux-x64.tar.gz"
    entry=$(jq -r --arg name "$name" '[.files[]? | select(.name == $name)] | first // empty | "\(.size) \(.sha256)"' "$manifest")
    [[ -n $entry ]] || fail "Release $version has no $name."
    local size=${entry%% *} sha=${entry#* }
    if [[ ! $size =~ ^[1-9][0-9]{0,9}$ || ! $sha =~ ^[0-9a-f]{64}$ ]] || ((size > MAX_FILE_BYTES)); then
        fail "The manifest's entry for $name is not one this script can check."
    fi
    printf '%s %s %s %s' "$version" "$name" "$size" "$sha"
}

# Whether a URL is HTTPS on one of GitHub's hosts.
github_url() {
    local host=${1#https://} allowed
    [[ $1 == https://* ]] || return 1
    host=${host%%/*}
    host=${host%%:*}
    for allowed in "${DOWNLOAD_HOSTS[@]}"; do [[ $host == "$allowed" ]] && return 0; done
    return 1
}

# Downloads url into path over HTTPS only, at most max bytes, and only from GitHub's hosts: each redirect is followed
# by hand once its target is checked, so nothing is ever asked of another host.
download() {
    local url=$1 path=$2 max=$3 answer status
    for _ in 1 2 3 4 5; do
        github_url "$url" || fail "$1 led to $url, which is not one of GitHub's hosts: it is not installed."
        answer=$(curl -sS --proto '=https' --tlsv1.2 --max-filesize "$max" --retry 3 -o "$path" -w '%{http_code} %{redirect_url}' "$url") ||
            fail "Could not download $url."
        status=${answer%% *}
        case $status in
        200) return 0 ;;
        301 | 302 | 303 | 307 | 308) url=${answer#* } ;;
        *) fail "Could not download $url (HTTP $status)." ;;
        esac
    done
    fail "$1 redirects too many times: it is not installed."
}

# Exits unless every entry of the tarball is a folder or a file under its one top folder (no link, no '..', no absolute path).
check_tarball() {
    local tarball=$1 top=$2 line
    while IFS= read -r line; do
        [[ $line == "$top/" || $line == "$top/"* ]] || fail "The tarball holds $line, outside $top/: it is not installed."
        [[ /$line/ != */../* && /$line/ != */./* ]] || fail "The tarball holds a path with '.' or '..': it is not installed."
    done < <(tar -tzf "$tarball")
    local listing
    listing=$(tar -tvzf "$tarball") || fail 'The tarball cannot be read: it is not installed.'
    if grep -qv '^[d-]' <<<"$listing"; then
        fail 'The tarball holds a link or a special file: it is not installed.'
    fi
}

ensure_tools() {
    local missing=() tool
    for tool in curl jq openssl tar gzip sha256sum od base64 fold; do
        command -v "$tool" >/dev/null 2>&1 || missing+=("$tool")
    done
    ((${#missing[@]} == 0)) && return 0
    if command -v apt-get >/dev/null 2>&1; then
        local packages=()
        for tool in "${missing[@]}"; do
            case $tool in
            jq | openssl | tar | gzip) packages+=("$tool") ;;
            curl) packages+=(curl ca-certificates) ;;
            *) packages+=(coreutils) ;;
            esac
        done
        DEBIAN_FRONTEND=noninteractive apt-get update -qq
        DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends "${packages[@]}" >/dev/null
    else
        fail "Install these first: ${missing[*]}." 3
    fi
}

main() {
    [[ $(id -u) -eq 0 ]] || fail 'Run it as root: curl -fsSL https://github.com/Ayrton09/Pulsar/releases/latest/download/install.sh | sudo bash' 3
    [[ $(uname -m) == x86_64 ]] || fail "Pulsar's Linux release is for x86_64 machines, and this one is $(uname -m)." 3
    ensure_tools
    local work
    work=$(mktemp -d /var/tmp/pulsar-install.XXXXXXXX)
    # shellcheck disable=SC2064 # the folder is known now
    trap "rm -rf -- '$work'" EXIT
    chmod 0700 "$work"

    printf '==> Checking the newest release of %s\n' "$REPOSITORY"
    download "$RELEASE_URL/$MANIFEST_NAME" "$work/$MANIFEST_NAME" "$MAX_MANIFEST_BYTES"
    download "$RELEASE_URL/$SIGNATURE_NAME" "$work/$SIGNATURE_NAME" "$MAX_SIGNATURE_BYTES"
    verify_manifest "$work/$MANIFEST_NAME" "$work/$SIGNATURE_NAME" "$work" "${TRUSTED_KEYS[@]}"
    local entry version name size sha
    entry=$(tarball_entry "$work/$MANIFEST_NAME") || exit 1
    read -r version name size sha <<<"$entry"

    printf '==> Downloading Pulsar %s (%s MB)\n' "$version" "$((size / 1048576))"
    download "https://github.com/$REPOSITORY/releases/download/v$version/$name" "$work/$name" "$size"
    [[ $(stat -c %s -- "$work/$name") == "$size" && $(sha256sum -- "$work/$name" | awk '{print $1}') == "$sha" ]] ||
        fail "$name is not the file the signed manifest names (its size or SHA-256 differs): it is not installed."
    local top=${name%.tar.gz}
    check_tarball "$work/$name" "$top"
    tar -xzf "$work/$name" -C "$work" --no-same-owner
    rm -f -- "$work/$name"

    printf '==> Running install-pulsar.sh of Pulsar %s\n' "$version"
    local status=0
    # Its questions go to the terminal (/dev/tty); stdin may be this script, piped from curl.
    bash "$work/$top/install-pulsar.sh" "$@" </dev/null || status=$?
    exit "$status"
}

# Run when executed or piped into bash, not when a test reads its functions. Everything is in functions, so a download
# cut short runs nothing.
if [[ -z ${BASH_SOURCE[0]:-} || ${BASH_SOURCE[0]} == "$0" ]]; then
    main "$@"
fi
