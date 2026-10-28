#!/usr/bin/env bash
# Builds a deployable Nova Globen Stomme bundle: publishes the Stomme.Server host for one RID and lays the matching
# interactive installer + deploy templates beside it, under setup/dist/stomme-<rid>/. Requires the .NET 10
# SDK on the build machine. Linux/macOS-host variant; see build-bundle.ps1 for Windows hosts.
#
# Unlike Fravashi (formerly Fravashi; it ships a container image + a launcher), Stomme is an ASP.NET Core web app you run
# behind a reverse proxy on a VM — so a "bundle" is the published app plus the installer that configures it.
#
# BLAZOR106: this NEVER runs `dotnet clean` and NEVER deletes obj/ or .dswa.cache.json — a clean rebuild
# mangles the FluentUI static-web-asset paths on this toolchain. `dotnet publish` is incremental and safe.
#
# Usage: ./build-bundle.sh [rid] [output-root] [version] [variant]
#   SELF_CONTAINED=0  -> framework-dependent build (needs the ASP.NET 10 runtime on the target); default 1.
#   ZIP=1             -> also produce <bundle>.zip for a release upload.
set -euo pipefail

rid="${1:-linux-x64}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$here/.." && pwd)"
output_root="${2:-$here/dist}"
version="${3:-0.0.0}"
variant="${4:-full}"
self_contained="${SELF_CONTAINED:-1}"
do_zip="${ZIP:-0}"

server="$repo_root/src/Stomme.Server/Stomme.Server.csproj"
bundle="$output_root/stomme-$rid"
app_dir="$bundle/app"
mkdir -p "$app_dir"

sc_flag="true"; [ "$self_contained" = "1" ] || sc_flag="false"
sc_json="$sc_flag"

echo "1/3  Publishing Stomme.Server for $rid (self-contained=$sc_flag, version $version, variant $variant) ..."
dotnet publish "$server" \
    -c Release -r "$rid" --self-contained "$sc_flag" \
    -p:Version="$version" \
    -o "$app_dir"

echo "2/3  Copying the interactive installer + deploy templates into the bundle ..."
case "$rid" in
    win-*) cp "$here/install/install-windows.ps1" "$bundle/" ;;
    *)     cp "$here/install/install-unix.sh" "$bundle/"; chmod +x "$bundle/install-unix.sh" ;;
esac
cp -R "$here/deploy" "$bundle/"
cp "$here/README.md" "$bundle/README.md"

# Stamp the bundle so the installer + host agree on what was shipped.
cat > "$bundle/bundle.json" <<EOF
{ "product": "stomme", "version": "${version}", "variant": "${variant}", "rid": "${rid}",
  "selfContained": ${sc_json}, "builtAt": "$(date -u +%Y-%m-%dT%H:%M:%SZ)" }
EOF

echo "3/3  Bundle ready: $bundle"

if [ "$do_zip" = "1" ]; then
    zip_path="$output_root/stomme-${variant}-${rid}.zip"   # no version in the name: the release folder carries it
    rm -f "$zip_path"
    ( cd "$bundle" && zip -qr "$zip_path" . )
    echo "     Zipped: $zip_path"
fi
