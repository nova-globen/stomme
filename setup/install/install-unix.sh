#!/usr/bin/env bash
# Nova Globen Stomme interactive installer (Linux/macOS) — D66.
#
# Configures a published Stomme.Server bundle for a VM deploy: PROMPTS for the database, the OpenIddict IdP
# (and GENERATES its persistent signing/encryption certificate for you), the HTTPS strategy, the host/port and
# the host account, then writes them into the app's appsettings.Production.json so the app boots straight to
# the site instead of the first-run web wizard. On Linux it can also install a systemd unit and print an nginx
# reverse-proxy snippet. Run from inside a bundle produced by build-bundle.sh (the folder holding app/).
#
# This is the OFFLINE/local installer; the network-manifest installer is setup/install.sh (which fetches a
# release bundle, verifies it, then calls this).
#
# Every value can be supplied as a flag (see --help) for a --non-interactive / automated install; --dry-run
# prints exactly what WOULD be written and writes nothing.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

APP_DIR="$here/app"
DB=""; CONN=""; ALIAS=""; HOST_EMAIL=""; HOST_PASSWORD=""
IDP=""; PORT="5000"; TLS="proxy"; TLS_CERT=""; TLS_KEY=""
INSTALL_ID=""; SERVICE=""; SERVICE_USER="www-data"
NON_INTERACTIVE=0; DRY_RUN=0

usage() {
    sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'
    cat <<'EOF'

Options:
  --app-dir DIR         Published app directory (default: ./app next to this script).
  --db PROVIDER         sqlserver | localdb | azuresql | sqlite | postgres | ravendb
  --conn "STRING"       Connection string for the chosen provider.
  --alias HOST          Default site alias / hostname (e.g. example.com).
  --host-email EMAIL    Host (super-admin) account email.
  --host-password PASS  Host account password.
  --idp yes|no          Enable the OpenIddict IdP (generates a persistent signing cert).
  --port N              Kestrel HTTP port (default 5000).
  --tls proxy|kestrel   proxy = terminate TLS at a reverse proxy (default); kestrel = Kestrel serves HTTPS.
  --tls-cert PATH       Kestrel HTTPS certificate (.pfx or .pem), when --tls kestrel.
  --tls-key PATH        Kestrel HTTPS key (PEM), when the cert is a PEM.
  --install-id GUID     InstallationId (default: generated).
  --service yes|no      Install a systemd unit (Linux, needs root; default: yes when root).
  --service-user USER   systemd unit User= (default: www-data).
  --non-interactive     Never prompt; require the flags above.
  --dry-run             Print the config + unit that WOULD be written; write nothing.
  -h, --help            This help.
EOF
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --app-dir) APP_DIR="$2"; shift 2 ;;
        --db) DB="$2"; shift 2 ;;
        --conn) CONN="$2"; shift 2 ;;
        --alias) ALIAS="$2"; shift 2 ;;
        --host-email) HOST_EMAIL="$2"; shift 2 ;;
        --host-password) HOST_PASSWORD="$2"; shift 2 ;;
        --idp) IDP="$2"; shift 2 ;;
        --port) PORT="$2"; shift 2 ;;
        --tls) TLS="$2"; shift 2 ;;
        --tls-cert) TLS_CERT="$2"; shift 2 ;;
        --tls-key) TLS_KEY="$2"; shift 2 ;;
        --install-id) INSTALL_ID="$2"; shift 2 ;;
        --service) SERVICE="$2"; shift 2 ;;
        --service-user) SERVICE_USER="$2"; shift 2 ;;
        --non-interactive) NON_INTERACTIVE=1; shift ;;
        --dry-run) DRY_RUN=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage; exit 2 ;;
    esac
done

die()  { printf 'error: %s\n' "$*" >&2; exit 1; }
say()  { printf '%s\n' "$*"; }

# Prompt with a default unless running non-interactively.
ask() { # <varname> <prompt> <default>
    local __var="$1" __prompt="$2" __default="${3:-}" __ans=""
    if [ "$NON_INTERACTIVE" = "1" ]; then
        printf -v "$__var" '%s' "$__default"; return
    fi
    if [ -n "$__default" ]; then read -r -p "$__prompt [$__default]: " __ans; else read -r -p "$__prompt: " __ans; fi
    printf -v "$__var" '%s' "${__ans:-$__default}"
}
ask_secret() { # <varname> <prompt>
    local __var="$1" __prompt="$2" __ans=""
    if [ "$NON_INTERACTIVE" = "1" ]; then return; fi
    read -r -s -p "$__prompt: " __ans; echo; printf -v "$__var" '%s' "$__ans"
}

# JSON-escape a string (backslash + double-quote + control chars are what appear in connection strings).
json_escape() { printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g'; }

db_type_for() {
    case "$1" in
        sqlserver|localdb|azuresql) echo "Stomme.Database.SqlServer.SqlServerDatabase, Stomme.Server" ;;
        sqlite)   echo "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server" ;;
        postgres) echo "Stomme.Database.PostgreSQL.PostgreSQLDatabase, Stomme.Server" ;;
        ravendb)  echo "Stomme.Database.RavenDb.RavenDatabase, Stomme.Server" ;;
        *) return 1 ;;
    esac
}

new_guid() {
    if command -v uuidgen >/dev/null 2>&1; then uuidgen | tr 'A-Z' 'a-z';
    elif [ -r /proc/sys/kernel/random/uuid ]; then cat /proc/sys/kernel/random/uuid;
    elif command -v openssl >/dev/null 2>&1; then
        openssl rand -hex 16 | sed 's/\(........\)\(....\)\(....\)\(....\)\(............\)/\1-\2-\3-\4-\5/';
    else python3 -c 'import uuid;print(uuid.uuid4())'; fi
}

# --- resolve config (prompt for anything not supplied) --------------------------------------------------
[ -d "$APP_DIR" ] || die "App directory '$APP_DIR' not found. Point --app-dir at the published Stomme.Server (the Stomme host)."

say "== Nova Globen Stomme install =="
say "Databases: sqlserver, localdb, azuresql, sqlite, postgres, ravendb"
ask DB "Database provider" "${DB:-postgres}"
db_type="$(db_type_for "$DB")" || die "Unknown database provider '$DB'."

case "$DB" in
    # an existing install's database keeps its name; a new one is named after Stomme
    sqlite)   default_conn="Data Source=$APP_DIR/stomme.db;"; [ -f "$APP_DIR/stomme.db" ] && default_conn="Data Source=$APP_DIR/stomme.db;" ;;
    postgres) default_conn="Server=127.0.0.1;Port=5432;Database=stomme;User Id=stomme;Password=CHANGE_ME;" ;;
    ravendb)  default_conn="Url=http://127.0.0.1:8080;Database=Stomme" ;;
    *)        default_conn="Server=localhost;Database=Stomme;Trusted_Connection=True;TrustServerCertificate=True;" ;;
esac
ask CONN "Connection string" "${CONN:-$default_conn}"

ask ALIAS "Default site alias / hostname" "${ALIAS:-localhost}"
ask HOST_EMAIL "Host account email" "${HOST_EMAIL:-admin@$ALIAS}"
if [ -z "$HOST_PASSWORD" ]; then ask_secret HOST_PASSWORD "Host account password"; fi
[ -n "$HOST_PASSWORD" ] || die "A host account password is required."

ask IDP "Enable the OpenIddict IdP (persistent signing cert)? yes/no" "${IDP:-no}"
ask PORT "Kestrel HTTP port" "${PORT:-5000}"
ask TLS "HTTPS: 'proxy' (reverse proxy terminates TLS) or 'kestrel'" "${TLS:-proxy}"
[ -n "$INSTALL_ID" ] || INSTALL_ID="$(new_guid)"

# --- generate the OpenIddict signing/encryption certificate (openssl) -----------------------------------
oidc_enabled="false"
sign_pem=""; sign_key=""
if [ "$IDP" = "yes" ] || [ "$IDP" = "y" ] || [ "$IDP" = "true" ]; then
    oidc_enabled="true"
    command -v openssl >/dev/null 2>&1 || die "openssl is required to generate the IdP signing certificate. Install it and retry."
    cert_dir="$APP_DIR/certs"
    sign_pem="$cert_dir/openiddict-signing.pem"
    sign_key="$cert_dir/openiddict-signing.key"
    if [ "$DRY_RUN" = "1" ]; then
        say "[dry-run] would generate an OpenIddict signing cert (RSA 2048, digitalSignature+keyEncipherment) at:"
        say "          $sign_pem / $sign_key"
    else
        mkdir -p "$cert_dir"; chmod 700 "$cert_dir"
        # One self-signed cert carrying BOTH usages: signing (digitalSignature) AND encryption (keyEncipherment),
        # so it satisfies OpenIddict's AddSigningCertificate and AddEncryptionCertificate. Stomme's loader reads
        # the PEM pair (OpenIddictCertificateLoader) — no password needed.
        openssl req -x509 -newkey rsa:2048 -sha256 -days 3650 -nodes \
            -keyout "$sign_key" -out "$sign_pem" \
            -subj "/CN=Stomme OpenIddict $ALIAS" \
            -addext "keyUsage=critical,digitalSignature,keyEncipherment" >/dev/null 2>&1
        chmod 600 "$sign_key" "$sign_pem"
        say "Generated OpenIddict signing certificate (persistent across restarts): $sign_pem"
    fi
fi

# --- compose appsettings.Production.json ----------------------------------------------------------------
prod="$APP_DIR/appsettings.Production.json"

oidc_block="\"Enabled\": false"
if [ "$oidc_enabled" = "true" ]; then
    oidc_block=$(cat <<EOF
"Enabled": true,
    "SigningCertificatePemPath": "$(json_escape "$sign_pem")",
    "SigningCertificateKeyPath": "$(json_escape "$sign_key")",
    "EncryptionCertificatePemPath": "$(json_escape "$sign_pem")",
    "EncryptionCertificateKeyPath": "$(json_escape "$sign_key")"
EOF
)
fi

kestrel_block=""
if [ "$TLS" = "kestrel" ]; then
    [ -n "$TLS_CERT" ] || die "--tls kestrel needs --tls-cert (and --tls-key for a PEM cert)."
    if [ -n "$TLS_KEY" ]; then
        kestrel_block=$(cat <<EOF
,
  "Kestrel": {
    "Endpoints": {
      "Https": { "Url": "https://0.0.0.0:$PORT", "Certificate": { "Path": "$(json_escape "$TLS_CERT")", "KeyPath": "$(json_escape "$TLS_KEY")" } }
    }
  }
EOF
)
    else
        kestrel_block=$(cat <<EOF
,
  "Kestrel": {
    "Endpoints": {
      "Https": { "Url": "https://0.0.0.0:$PORT", "Certificate": { "Path": "$(json_escape "$TLS_CERT")" } }
    }
  }
EOF
)
    fi
fi

read -r -d '' PROD_JSON <<EOF || true
{
  "Database": { "DefaultDBType": "$(json_escape "$db_type")" },
  "ConnectionStrings": { "DefaultConnection": "$(json_escape "$CONN")" },
  "Installation": {
    "DefaultAlias": "$(json_escape "$ALIAS")",
    "HostEmail": "$(json_escape "$HOST_EMAIL")",
    "HostPassword": "$(json_escape "$HOST_PASSWORD")",
    "SiteTemplate": "",
    "DefaultTheme": "",
    "DefaultContainer": ""
  },
  "OpenIddict": { $oidc_block },
  "InstallationId": "$(json_escape "$INSTALL_ID")"$kestrel_block
}
EOF

# --- systemd unit ---------------------------------------------------------------------------------------
dll="$APP_DIR/Stomme.Server.dll"
exec_start="/usr/bin/dotnet $dll"
[ -x "$APP_DIR/Stomme.Server" ] && exec_start="$APP_DIR/Stomme.Server"   # self-contained build has a native host
unit_urls="http://127.0.0.1:$PORT"
[ "$TLS" = "kestrel" ] && unit_urls="https://0.0.0.0:$PORT"

unit_content=$(sed \
    -e "s|@APP_DIR@|$APP_DIR|g" \
    -e "s|@EXEC_START@|$exec_start|g" \
    -e "s|@USER@|$SERVICE_USER|g" \
    -e "s|@URLS@|$unit_urls|g" \
    "$here/deploy/stomme.service.template" 2>/dev/null || true)

# The unit is named after Stomme; an install made before the rename keeps its stomme unit, upgraded in place.
unit_name="stomme"
[ -f /etc/systemd/system/stomme.service ] && unit_name="stomme"

if [ -z "$SERVICE" ]; then
    if [ "$(id -u)" = "0" ] && [ "$(uname)" = "Linux" ]; then SERVICE="yes"; else SERVICE="no"; fi
fi

# --- apply (or dry-run) ---------------------------------------------------------------------------------
if [ "$DRY_RUN" = "1" ]; then
    say ""; say "----- appsettings.Production.json (dry-run, NOT written) -----"
    printf '%s\n' "$PROD_JSON"
    if [ "$SERVICE" = "yes" ]; then
        say ""; say "----- /etc/systemd/system/$unit_name.service (dry-run) -----"
        printf '%s\n' "$unit_content"
    fi
    say ""; say "[dry-run] nothing was written."
    exit 0
fi

printf '%s\n' "$PROD_JSON" > "$prod"
chmod 600 "$prod"
say "Wrote $prod"

if [ "$SERVICE" = "yes" ]; then
    [ -n "$unit_content" ] || die "systemd unit template not found at $here/deploy/stomme.service.template."
    printf '%s\n' "$unit_content" > "/etc/systemd/system/$unit_name.service"
    systemctl daemon-reload
    systemctl enable "$unit_name" >/dev/null 2>&1 || true
    say "Installed systemd unit $unit_name.service (systemctl start $unit_name)."
else
    say "Skipped systemd (not root / not Linux). Run the app with:"
    say "  ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS=$unit_urls $exec_start"
fi

say ""
say "Installed. Stomme is configured for $DB on alias '$ALIAS' (IdP enabled=$oidc_enabled)."
if [ "$TLS" = "proxy" ]; then
    say "TLS terminates at your reverse proxy — see deploy/nginx.conf.example (proxy_pass http://127.0.0.1:$PORT)."
fi
say "The DB + host account are prefilled, so the first-run web wizard is skipped."
