#!/usr/bin/env pwsh
# Nova Globen Stomme interactive installer (Windows) — D66.
#
# Configures a published Stomme.Server bundle for a deploy: PROMPTS for the database, the OpenIddict IdP (and
# GENERATES its persistent signing/encryption certificate for you, via .NET X509 APIs), the HTTPS strategy, the
# host/port and the host account, then writes them into the app's appsettings.Production.json so the app boots
# straight to the site instead of the first-run web wizard. Run from inside a bundle produced by
# build-bundle.ps1 (the folder holding app\).
#
# This is the OFFLINE/local installer; the network-manifest installer is setup\install.ps1.
#
# Every value can be supplied as a parameter for a -NonInteractive / automated install; -DryRun prints exactly
# what WOULD be written and writes nothing.
param(
    [string]$AppDir = "$PSScriptRoot/app",
    [ValidateSet("", "sqlserver", "localdb", "azuresql", "sqlite", "postgres", "ravendb")]
    [string]$Db = "",
    [string]$Conn = "",
    [string]$Alias = "",
    [string]$HostEmail = "",
    [string]$HostPassword = "",
    [ValidateSet("", "yes", "no")]
    [string]$Idp = "",
    [int]$Port = 5000,
    [ValidateSet("proxy", "kestrel")]
    [string]$Tls = "proxy",
    [string]$TlsCert = "",
    [string]$TlsKey = "",
    [string]$InstallId = "",
    [switch]$NonInteractive,
    [switch]$DryRun
)
$ErrorActionPreference = "Stop"

function Die([string]$m) { Write-Error $m; exit 1 }

function Ask([string]$prompt, [string]$default) {
    if ($NonInteractive) { return $default }
    $suffix = if ($default) { " [$default]" } else { "" }
    $answer = Read-Host "$prompt$suffix"
    if ([string]::IsNullOrEmpty($answer)) { return $default } else { return $answer }
}
function AskSecret([string]$prompt) {
    if ($NonInteractive) { return "" }
    $secure = Read-Host $prompt -AsSecureString
    return [System.Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
}

function DbTypeFor([string]$db) {
    switch ($db) {
        { $_ -in @("sqlserver", "localdb", "azuresql") } { return "Stomme.Database.SqlServer.SqlServerDatabase, Stomme.Server" }
        "sqlite"   { return "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server" }
        "postgres" { return "Stomme.Database.PostgreSQL.PostgreSQLDatabase, Stomme.Server" }
        "ravendb"  { return "Stomme.Database.RavenDb.RavenDatabase, Stomme.Server" }
        default    { Die "Unknown database provider '$db'." }
    }
}

if (-not (Test-Path $AppDir)) { Die "App directory '$AppDir' not found. Point -AppDir at the published Stomme.Server (the Stomme host)." }

Write-Host "== Nova Globen Stomme install =="
Write-Host "Databases: sqlserver, localdb, azuresql, sqlite, postgres, ravendb"
if (-not $Db) { $Db = Ask "Database provider" "postgres" }
$dbType = DbTypeFor $Db

if (-not $Conn) {
    $defaultConn = switch ($Db) {
        "sqlite"   { if (Test-Path (Join-Path $AppDir "stomme.db")) { "Data Source=$AppDir/stomme.db;" } else { "Data Source=$AppDir/stomme.db;" } }   # an existing install keeps its database
        "postgres" { "Server=127.0.0.1;Port=5432;Database=stomme;User Id=stomme;Password=CHANGE_ME;" }
        "ravendb"  { "Url=http://127.0.0.1:8080;Database=Stomme" }
        default    { "Server=(localdb)\MSSQLLocalDB;Database=Stomme;Trusted_Connection=True;TrustServerCertificate=True;" }
    }
    $Conn = Ask "Connection string" $defaultConn
}

if (-not $Alias) { $Alias = Ask "Default site alias / hostname" "localhost" }
if (-not $HostEmail) { $HostEmail = Ask "Host account email" "admin@$Alias" }
if (-not $HostPassword) { $HostPassword = AskSecret "Host account password" }
if (-not $HostPassword) { Die "A host account password is required." }

if (-not $Idp) { $Idp = Ask "Enable the OpenIddict IdP (persistent signing cert)? yes/no" "no" }
if ($PSBoundParameters.ContainsKey('Port') -eq $false) { $Port = [int](Ask "Kestrel HTTP port" "5000") }
if (-not $InstallId) { $InstallId = [guid]::NewGuid().ToString() }

# --- generate the OpenIddict signing/encryption certificate (.NET X509, cross-platform) -----------------
$oidcEnabled = ($Idp -in @("yes", "y", "true"))
$signPfx = ""; $signPwd = ""
if ($oidcEnabled) {
    $certDir = Join-Path $AppDir "certs"
    $signPfx = Join-Path $certDir "openiddict-signing.pfx"
    $signPwd = [guid]::NewGuid().ToString("N")
    if ($DryRun) {
        Write-Host "[dry-run] would generate an OpenIddict signing cert (RSA 2048, digitalSignature+keyEncipherment) at $signPfx"
    } else {
        New-Item -ItemType Directory -Force -Path $certDir | Out-Null
        # One self-signed cert carrying BOTH usages (digitalSignature for signing, keyEncipherment for
        # encryption), so it satisfies OpenIddict's AddSigningCertificate AND AddEncryptionCertificate.
        $rsa = [System.Security.Cryptography.RSA]::Create(2048)
        try {
            $req = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
                "CN=Stomme OpenIddict $Alias", $rsa,
                [System.Security.Cryptography.HashAlgorithmName]::SHA256,
                [System.Security.Cryptography.X509Certificates.RSASignaturePadding]::Pkcs1)
            $usage = [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]"DigitalSignature, KeyEncipherment"
            $req.CertificateExtensions.Add(
                [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new($usage, $true))
            $cert = $req.CreateSelfSigned([System.DateTimeOffset]::UtcNow.AddDays(-1), [System.DateTimeOffset]::UtcNow.AddYears(10))
            $bytes = $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pkcs12, $signPwd)
            [System.IO.File]::WriteAllBytes($signPfx, $bytes)
        } finally { $rsa.Dispose() }
        Write-Host "Generated OpenIddict signing certificate (persistent across restarts): $signPfx"
    }
}

# --- compose appsettings.Production.json ----------------------------------------------------------------
$openIddict = [ordered]@{ Enabled = $false }
if ($oidcEnabled) {
    $openIddict = [ordered]@{
        Enabled = $true
        SigningCertificatePath = $signPfx
        SigningCertificatePassword = $signPwd
        EncryptionCertificatePath = $signPfx
        EncryptionCertificatePassword = $signPwd
    }
}

$config = [ordered]@{
    Database          = [ordered]@{ DefaultDBType = $dbType }
    ConnectionStrings = [ordered]@{ DefaultConnection = $Conn }
    Installation      = [ordered]@{
        DefaultAlias     = $Alias
        HostEmail        = $HostEmail
        HostPassword     = $HostPassword
        SiteTemplate     = ""
        DefaultTheme     = ""
        DefaultContainer = ""
    }
    OpenIddict        = $openIddict
    InstallationId    = $InstallId
}

if ($Tls -eq "kestrel") {
    if (-not $TlsCert) { Die "-Tls kestrel needs -TlsCert (and -TlsKey for a PEM cert)." }
    $certObj = [ordered]@{ Path = $TlsCert }
    if ($TlsKey) { $certObj.KeyPath = $TlsKey }
    $config.Kestrel = [ordered]@{
        Endpoints = [ordered]@{ Https = [ordered]@{ Url = "https://0.0.0.0:$Port"; Certificate = $certObj } }
    }
}

$json = $config | ConvertTo-Json -Depth 8
$prod = Join-Path $AppDir "appsettings.Production.json"

if ($DryRun) {
    Write-Host ""; Write-Host "----- appsettings.Production.json (dry-run, NOT written) -----"
    Write-Host $json
    Write-Host ""; Write-Host "[dry-run] nothing was written."
    exit 0
}

Set-Content -Path $prod -Value $json -Encoding utf8
Write-Host "Wrote $prod"

$urls = if ($Tls -eq "kestrel") { "https://0.0.0.0:$Port" } else { "http://127.0.0.1:$Port" }
$exe = Join-Path $AppDir "Stomme.Server.exe"
$runCmd = if (Test-Path $exe) { "`"$exe`"" } else { "dotnet `"$(Join-Path $AppDir 'Stomme.Server.dll')`"" }

Write-Host ""
Write-Host "Installed. Stomme is configured for $Db on alias '$Alias' (IdP enabled=$oidcEnabled)."
Write-Host "The DB + host account are prefilled, so the first-run web wizard is skipped."
Write-Host "Run it with:"
Write-Host "  `$env:ASPNETCORE_ENVIRONMENT='Production'; `$env:ASPNETCORE_URLS='$urls'; $runCmd"
Write-Host "Or install a Windows Service:"
# The service is named after Stomme; an install made before the rename keeps its Stomme service.
$serviceName = if (Get-Service -Name Stomme -ErrorAction SilentlyContinue) { "Stomme" } else { "Stomme" }
Write-Host "  New-Service -Name $serviceName -BinaryPathName $runCmd -StartupType Automatic"
if ($Tls -eq "proxy") {
    Write-Host "TLS terminates at your reverse proxy — see deploy/nginx.conf.example (proxy_pass http://127.0.0.1:$Port)."
}
