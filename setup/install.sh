#!/usr/bin/env bash
# Nova Globen Stomme network installer (Linux/macOS).
#
# Fetches a release from the project's GitHub Releases (<base>/latest/download/ or <base>/download/v<version>/), verifies
# the RID bundle against the release's SHA256SUMS BEFORE unpacking anything, extracts the bundle, then hands off to the bundle's
# INTERACTIVE installer (install-unix.sh), which prompts for the database, the OpenIddict IdP + its generated signing
# cert, TLS, host/port and the host account. This mirrors the Fravashi two-tier layout (network installer ->
# local-from-bundle installer).
#
# --base-url points at another copy of the releases laid out the same way (for example a mirror inside your network).
#
# Usage:
#   curl -fsSL https://github.com/nova-globen/stomme/releases/latest/download/install.sh | bash
# Options (after `bash -s --`) - anything after `--` beyond these is forwarded to install-unix.sh:
#   --version X.Y.Z   Install a specific version (default: latest).
#   --variant NAME    Variant to install (default: full).
#   --base-url URL    Where the releases are (default: https://github.com/nova-globen/stomme/releases).
#   --dest DIR        Where to unpack the bundle (default: ./stomme).
set -euo pipefail

BASE_URL="https://github.com/nova-globen/stomme/releases"
VERSION="latest"
VARIANT="full"
DEST="./stomme"
FORWARD=()

while [ "$#" -gt 0 ]; do
    case "$1" in
        --version)  VERSION="$2"; shift 2 ;;
        --variant)  VARIANT="$2"; shift 2 ;;
        --base-url) BASE_URL="${2%/}"; shift 2 ;;
        --dest)     DEST="$2"; shift 2 ;;
        --)         shift; FORWARD=("$@"); break ;;
        *) FORWARD+=("$1"); shift ;;   # unknown flags flow through to install-unix.sh
    esac
done

say()  { printf '%s\n' "$*"; }
die()  { printf 'error: %s\n' "$*" >&2; exit 1; }

host_os=""; case "$(uname -s)" in Linux) host_os=linux ;; Darwin) host_os=osx ;; *) die "Unsupported OS $(uname -s) - use install.ps1 on Windows." ;; esac
cpu=""; case "$(uname -m)" in x86_64|amd64) cpu=x64 ;; aarch64|arm64) cpu=arm64 ;; *) die "Unsupported CPU $(uname -m)." ;; esac
rid="${host_os}-${cpu}"

if [ "$VERSION" = "latest" ]; then folder="${BASE_URL}/latest/download"; else folder="${BASE_URL}/download/v${VERSION#v}"; fi

JSON=""
if command -v jq >/dev/null 2>&1; then JSON=jq
elif command -v python3 >/dev/null 2>&1; then JSON=python3
else die "Need jq or python3 to parse the release manifest."; fi

manifest_field() { # <file> <what>   what: version|asset
    local file="$1" what="$2"
    if [ "$JSON" = jq ]; then
        case "$what" in
            version) jq -r '.version' "$file" ;;
            asset)   jq -r --arg v "$VARIANT" --arg r "$rid" '.variants[]|select(.name==$v)|.bundles[]|select(.rid==$r)|.asset' "$file" ;;
        esac
    else
        python3 - "$file" "$VARIANT" "$rid" "$what" <<'PY'
import json, sys
file, variant, rid, what = sys.argv[1:5]
m = json.load(open(file, encoding="utf-8"))
if what == "version": print(m.get("version","")); sys.exit()
v = next((v for v in m.get("variants",[]) if v.get("name","").lower()==variant.lower()), None) or {}
b = next((b for b in v.get("bundles",[]) if b.get("rid","").lower()==rid.lower()), None) or {}
print(b.get("asset", ""))
PY
    fi
}

sha256_of() { if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | awk '{print $1}'; else shasum -a 256 "$1" | awk '{print $1}'; fi; }

work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
say "Fetching the release manifest from ${folder} ..."
curl -fsSL "${folder}/release-manifest.json" -o "$work/release-manifest.json" \
    || die "Could not fetch ${folder}/release-manifest.json. Check the version exists, or see --base-url."
grep -q '"version"' "$work/release-manifest.json" \
    || die "The answer from ${folder} is not a release manifest (a sign-in or proxy page?). Check --base-url."

resolved_version="$(manifest_field "$work/release-manifest.json" version)"
[ -n "$resolved_version" ] || die "The manifest has no version."
say "Release: ${resolved_version} (variant: ${VARIANT}, rid: ${rid})"

asset="$(manifest_field "$work/release-manifest.json" asset)"
[ -n "$asset" ] || die "No bundle for rid '${rid}' in variant '${VARIANT}'."

curl -fsSL "${folder}/SHA256SUMS" -o "$work/SHA256SUMS" || die "The release has no SHA256SUMS - refusing to install an unverified bundle."
expected="$(awk -v f="$asset" '$2==f || $2=="*"f {print $1}' "$work/SHA256SUMS" | head -n 1)"
[ -n "$expected" ] || die "SHA256SUMS lists no checksum for ${asset} - refusing to install it."

say "Downloading ${asset} ..."
curl -fL --progress-bar "${folder}/${asset}" -o "$work/bundle.zip" || die "Bundle download failed."
actual="$(sha256_of "$work/bundle.zip")"
[ "$actual" = "$expected" ] || die "sha256 mismatch (expected $expected, got $actual). Aborting."
say "  verified against SHA256SUMS"

command -v unzip >/dev/null 2>&1 || die "unzip is required to unpack the bundle."
mkdir -p "$DEST"; unzip -oq "$work/bundle.zip" -d "$DEST"
chmod +x "$DEST/install-unix.sh" 2>/dev/null || true

say ""
say "Bundle unpacked to $DEST. Starting the interactive installer ..."
say ""
exec bash "$DEST/install-unix.sh" "${FORWARD[@]:-}"
