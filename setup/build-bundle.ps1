#!/usr/bin/env pwsh
# Builds a deployable Nova Globen Stomme bundle: publishes the Stomme.Server host for one RID and lays the matching
# interactive installer + deploy templates beside it, under setup/dist/stomme-<rid>/. Requires the .NET 10
# SDK on the build machine. Windows-host variant; see build-bundle.sh for Linux/macOS hosts.
#
# Unlike Fravashi (formerly Fravashi; it ships a container image + a launcher), Stomme is an ASP.NET Core web app you run
# behind a reverse proxy on a VM — so a "bundle" is the published app plus the installer that configures it.
#
# BLAZOR106: this NEVER runs `dotnet clean` and NEVER deletes obj/ or .dswa.cache.json — a clean rebuild
# mangles the FluentUI static-web-asset paths on this toolchain. `dotnet publish` is incremental and safe.
param(
    [string]$Rid = "win-x64",
    [string]$OutputRoot = "$PSScriptRoot/dist",
    # Version + variant stamped into the bundle name / release manifest. A plain local build uses the defaults.
    [string]$Version = "0.0.0",
    [string]$Variant = "full",
    # Self-contained (default, mirrors Fravashi) carries the .NET runtime so the VM needs nothing installed;
    # -SelfContained:$false produces a smaller framework-dependent build that needs the ASP.NET 10 runtime on
    # the target.
    [bool]$SelfContained = $true,
    # Also produce a <bundle>.zip for uploading to a release (the install.ps1/.sh network installer fetches it).
    [switch]$Zip
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path "$PSScriptRoot/..").Path
$server = Join-Path $repoRoot "src/Stomme.Server/Stomme.Server.csproj"
$bundle = Join-Path $OutputRoot "stomme-$Rid"
$appDir = Join-Path $bundle "app"

New-Item -ItemType Directory -Force -Path $appDir | Out-Null

Write-Host "1/3  Publishing Stomme.Server for $Rid (self-contained=$SelfContained, version $Version, variant $Variant) ..."
dotnet publish $server `
    -c Release -r $Rid --self-contained $SelfContained `
    -p:Version=$Version `
    -o $appDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (Stomme.Server) failed." }

Write-Host "2/3  Copying the interactive installer + deploy templates into the bundle ..."
if ($Rid -like "win-*") {
    Copy-Item "$PSScriptRoot/install/install-windows.ps1" $bundle -Force
} else {
    Copy-Item "$PSScriptRoot/install/install-unix.sh" $bundle -Force
}
Copy-Item "$PSScriptRoot/deploy" $bundle -Recurse -Force
Copy-Item "$PSScriptRoot/README.md" (Join-Path $bundle "README.md") -Force

# Stamp the bundle so the installer + host agree on what was shipped.
[ordered]@{
    product = "stomme"; version = $Version; variant = $Variant; rid = $Rid
    selfContained = $SelfContained
    builtAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
} | ConvertTo-Json | Set-Content -Path (Join-Path $bundle "bundle.json") -Encoding utf8

Write-Host "3/3  Bundle ready: $bundle"

if ($Zip) {
    # no version in the file name: a release keeps it in its folder (GitHub Releases keeps it in the tag)
    $zipPath = Join-Path $OutputRoot "stomme-$Variant-$Rid.zip"
    if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
    Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $zipPath
    Write-Host "     Zipped: $zipPath"
}
