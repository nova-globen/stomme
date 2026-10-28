#!/bin/sh
# Nova Globen Stomme container entrypoint (optional container deploy path).
#
# The image runs non-root. Stomme reads its DB, host account, OpenIddict cert paths and InstallationId from
# appsettings.Production.json — mount it (and any generated cert) at /app, or bake it in at build time. This
# entrypoint only ensures the writable data dir exists, then execs the host.
set -e

mkdir -p "${STOMME_DATA:-${STOMME_DATA:-/app/data}}" 2>/dev/null || true

exec dotnet Stomme.Server.dll "$@"
