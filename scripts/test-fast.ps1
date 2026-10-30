# The FAST TIER (BL-164): every test that does not reach PostgreSQL or RavenDB, for iterating in a few minutes.
#
# Since 2026-10-03 this IS the per-commit gate's test step (`pwsh scripts/gate.ps1`, the FAST mode), and it is still
# NOT enough for a tag: a tier without the two real providers cannot see a provider divergence, the D29 renderer
# deadlock or a parity break, so `pwsh scripts/gate.ps1 -Full` runs before every nuget-v*, v* or prod-v* tag and for
# a change to persistence, a store provider, the installer or the render path (docs/releasing.md "Which gate when").
#
# Both external endpoints are pointed at a CLOSED local port on purpose. The trait is kept true by a source scan
# (FastTierTraitTests), and a scan cannot see a class that reaches a store through a helper in another file - so
# such a class goes RED here, quickly, instead of quietly using the shared servers and making the tier slow.
#
#   pwsh scripts/test-fast.ps1                 # the whole solution's fast tier
#   pwsh scripts/test-fast.ps1 -NoBuild        # after a build (of the same -Configuration, Debug by default; CI builds Release)
#   pwsh scripts/test-fast.ps1 -Log fast.log   # keep the full log (the default is under %TEMP%)
param(
    [switch]$NoBuild,
    [string]$Log = (Join-Path ([IO.Path]::GetTempPath()) 'stomme-fast-tier.log'),
    [int]$MaxCpu = 8,
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$env:RAVENDB_TEST_URL = 'http://127.0.0.1:9'
$env:POSTGRES_TEST_HOST = '127.0.0.1'
$env:POSTGRES_TEST_PORT = '9'
$env:POSTGRES_TEST_PASSWORD = 'closed-port' # the local servers' passwords are files a CI runner has not got
$env:SQLSERVER_TEST_HOST = '127.0.0.1'
$env:SQLSERVER_TEST_PORT = '9'
$env:SQLSERVER_TEST_PASSWORD = 'closed-port'
$env:MYSQL_TEST_HOST = '127.0.0.1'
$env:MYSQL_TEST_PORT = '9'
$env:MYSQL_TEST_PASSWORD = 'closed-port' # the endpoint is closed; a password only so the fixture does not stop first
$env:REDIS_TEST_HOST = '127.0.0.1'
$env:REDIS_TEST_PORT = '9'
$env:REDIS_TEST_PASSWORD = 'closed-port'
$env:MONGODB_TEST_HOST = '127.0.0.1'
$env:MONGODB_TEST_PORT = '9'
$env:MONGODB_TEST_PASSWORD = 'closed-port'
$env:FILESERVER_TEST_HOST = '127.0.0.1' # the FTP and SFTP servers' passwords are files; their ports closed
$env:FTP_TEST_PORT = '9'
$env:SFTP_TEST_PORT = '9'
$env:PGWIRE_TEST_HOST = '127.0.0.1' # QuestDB's and CrateDB's
$env:QUESTDB_TEST_PORT = '9'
$env:QUESTDB_TEST_PASSWORD = 'closed-port'
$env:CRATEDB_TEST_PORT = '9'
$env:CRATEDB_TEST_PASSWORD = 'closed-port'
$env:RABBITMQ_TEST_HOST = '127.0.0.1'
$env:RABBITMQ_TEST_PORT = '9'
$env:RABBITMQ_TEST_PASSWORD = 'closed-port'
$env:MQTT_TEST_HOST = '127.0.0.1'
$env:MQTT_TEST_PORT = '9'
$env:MQTT_TEST_PASSWORD = 'closed-port'
$env:NEXTCLOUD_TEST_HOST = '127.0.0.1'
$env:NEXTCLOUD_TEST_PORT = '9'
$env:NEXTCLOUD_TEST_PASSWORD = 'closed-port'
$env:AZURITE_TEST_HOST = '127.0.0.1'
$env:AZURITE_TEST_PORT = '9'
$env:AZURITE_TEST_KEY = 'Y2xvc2VkLXBvcnQ='
$env:GCS_TEST_HOST = '127.0.0.1'
$env:GCS_TEST_PORT = '9'
$env:LDAP_TEST_HOST = '127.0.0.1'
$env:LDAP_TEST_PORT = '9'
$env:LDAP_TEST_PASSWORD = 'closed-port'
$env:HOMEASSISTANT_TEST_HOST = '127.0.0.1'
$env:HOMEASSISTANT_TEST_PORT = '9'
$env:HOMEASSISTANT_TEST_TOKEN = 'closed-port'
$env:COSMOSDB_TEST_HOST = '127.0.0.1'
$env:COSMOSDB_TEST_PORT = '9'
$env:FIRESTORE_TEST_HOST = '127.0.0.1'
$env:FIRESTORE_TEST_PORT = '9'
$env:MOTO_TEST_HOST = '127.0.0.1'
$env:MOTO_TEST_PORT = '9'
$env:MOTO_TEST_ACCESS_KEY = 'closed-port'
$env:MOTO_TEST_SECRET_KEY = 'closed-port'
$env:METABASE_TEST_HOST = '127.0.0.1'
$env:METABASE_TEST_PORT = '9'
$env:METABASE_TEST_PASSWORD = 'closed-port'
$env:METABASE_TEST_API_KEY = 'closed-port'

# Capped, and no reusable MSBuild nodes left behind (see scripts/gate.ps1).
$arguments = @('test', (Join-Path $root 'Stomme.slnx'), '--nologo', '--configuration', $Configuration, '--filter', 'Category!=ExternalDatabase', "-m:$MaxCpu", '-nodeReuse:false')
if ($NoBuild) { $arguments += '--no-build' }

$timer = [Diagnostics.Stopwatch]::StartNew()
& dotnet @arguments *> $Log
$exit = $LASTEXITCODE

Select-String -Path $Log -Pattern '^\s*(Passed!|Failed!)' | ForEach-Object { $_.Line.Trim() }
Select-String -Path $Log -Pattern '^\s+Failed ' | ForEach-Object { $_.Line.Trim() }
# A run that died before any test (no build of this configuration, a restore failure) prints no summary: show why, so
# a CI log says it rather than only "exit 1".
if ($exit -ne 0 -and -not (Select-String -Path $Log -Pattern '^\s*(Passed!|Failed!)' -Quiet)) { Get-Content $Log -Tail 40 }
"fast tier: exit $exit in $([int]$timer.Elapsed.TotalSeconds) s - full log: $Log"
"Not enough for a tag: run 'pwsh scripts/gate.ps1 -Full' before one."
exit $exit
