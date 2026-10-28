#!/usr/bin/env pwsh
# Nova Globen Stomme network installer (Windows).
#
# Fetches a release from the project's GitHub Releases (<base>/latest/download/ or <base>/download/v<version>/), verifies
# the RID bundle against the release's SHA256SUMS BEFORE unpacking anything, extracts the bundle, then hands off to the
# bundle's INTERACTIVE installer (install-windows.ps1), which prompts for the database, the OpenIddict IdP + its generated
# signing cert, TLS, host/port and the host account. Mirrors the Fravashi two-tier layout (network installer ->
# local-from-bundle installer).
#
# -BaseUrl points at another copy of the releases laid out the same way (for example a mirror inside your network).
#
# Usage (irm | iex):
#   iwr -useb https://github.com/nova-globen/stomme/releases/latest/download/install.ps1 | iex
# Or with options:
#   ./install.ps1 -Version 1.0.0 -Variant full -Dest C:\Stomme
param(
    [string]$Version = "latest",
    [string]$Variant = "full",
    [string]$BaseUrl = "https://github.com/nova-globen/stomme/releases",
    [string]$Dest = "./stomme",
    # Everything after -- is forwarded to install-windows.ps1.
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Forward = @()
)
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
function Die([string]$m) { Write-Error $m; exit 1 }

$rid = "win-x64"   # the Windows host ships win-x64 (runs under emulation on ARM)
$folder = if ($Version -eq "latest") { "$($BaseUrl.TrimEnd('/'))/latest/download" } else { "$($BaseUrl.TrimEnd('/'))/download/v$($Version.TrimStart('v'))" }

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("stomme-install-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $work | Out-Null
try {
    Write-Host "Fetching the release manifest from $folder ..."
    $manifestPath = Join-Path $work "release-manifest.json"
    try { Invoke-WebRequest -UseBasicParsing "$folder/release-manifest.json" -OutFile $manifestPath }
    catch { Die "Could not fetch $folder/release-manifest.json. Check the version exists, or see -BaseUrl." }
    try { $manifest = Get-Content -Raw $manifestPath | ConvertFrom-Json }
    catch { Die "The answer from $folder is not a release manifest (a sign-in or proxy page?). Check -BaseUrl." }
    $resolvedVersion = $manifest.version
    if (-not $resolvedVersion) { Die "The manifest has no version." }
    Write-Host "Release: $resolvedVersion (variant: $Variant, rid: $rid)"

    $variantObj = $manifest.variants | Where-Object { $_.name -ieq $Variant } | Select-Object -First 1
    if (-not $variantObj) { Die "Variant '$Variant' is not in this release." }
    $bundle = $variantObj.bundles | Where-Object { $_.rid -ieq $rid } | Select-Object -First 1
    if (-not $bundle -or -not $bundle.asset) { Die "No bundle for rid '$rid' in variant '$Variant'." }

    $sums = Join-Path $work "SHA256SUMS"
    try { Invoke-WebRequest -UseBasicParsing "$folder/SHA256SUMS" -OutFile $sums }
    catch { Die "The release has no SHA256SUMS - refusing to install an unverified bundle." }
    $expected = Get-Content $sums | ForEach-Object { $parts = $_ -split '\s+', 2; if ($parts.Count -eq 2 -and $parts[1].TrimStart('*') -eq $bundle.asset) { $parts[0].ToLowerInvariant() } } | Select-Object -First 1
    if (-not $expected) { Die "SHA256SUMS lists no checksum for $($bundle.asset) - refusing to install it." }

    $zip = Join-Path $work "bundle.zip"
    Write-Host "Downloading $($bundle.asset) ..."
    Invoke-WebRequest -UseBasicParsing "$folder/$($bundle.asset)" -OutFile $zip
    $actual = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()
    if ($actual -ne $expected) { Die "sha256 mismatch (expected $expected, got $actual)." }
    Write-Host "  verified against SHA256SUMS"

    New-Item -ItemType Directory -Force -Path $Dest | Out-Null
    Expand-Archive -Path $zip -DestinationPath $Dest -Force

    Write-Host ""
    Write-Host "Bundle unpacked to $Dest. Starting the interactive installer ..."
    Write-Host ""
    & (Join-Path $Dest "install-windows.ps1") @Forward
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
