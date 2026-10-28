#!/usr/bin/env pwsh
# D81 — reliable-modularity Phase 4: INDEPENDENCE packaging.
#
# Packs the Stomme module-facing SDK to a LOCAL NuGet feed (examples/local-feed), then restores +
# builds the storage-blind smoke module (examples/_sdk-smoke) against PACKAGES ONLY — proving a module
# can compile with ZERO source/project references to Stomme (the owner's bar #1).
#
# All packages are versioned off Directory.Build.props (<Version>). No dotnet clean / no obj deletion
# (BLAZOR106). Run from the repo root: pwsh examples/pack-sdk.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$feed = Join-Path $PSScriptRoot 'local-feed'
New-Item -ItemType Directory -Force -Path $feed | Out-Null

# Stomme.Server and its WHOLE project closure, computed from the project references (BL-186): the hand-kept list that
# stood here held 17 of 28 projects and missed the engine and the cryptography library, so the packed Stomme.Server
# could not be restored from this feed (measured 2026-09-30).
& pwsh -NoProfile -File (Join-Path $root 'scripts/pack-framework.ps1') -Output $feed
if ($LASTEXITCODE -ne 0) { throw 'pack failed: the framework' }

# NuGet resolves an id and version from the global packages folder before any source, and a repack keeps its version,
# so what this run packed is purged there - or the meta and the smoke module below restore a stale copy (the slim
# script does the same, pack-sdk-slim.ps1 step 0).
[xml]$props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props')
$version = @($props.Project.PropertyGroup.Version | Where-Object { $_ })[0]
$gpf = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
Get-ChildItem $feed -Filter "Stomme.*.$version.nupkg" | ForEach-Object {
    $id = $_.Name.Substring(0, $_.Name.Length - ".$version.nupkg".Length).ToLowerInvariant()
    $cached = Join-Path $gpf (Join-Path $id $version)
    if (Test-Path -LiteralPath $cached) { Remove-Item -LiteralPath $cached -Recurse -Force }
}

# The Stomme.Sdk meta-package (one reference -> the whole module surface).
dotnet pack (Join-Path $PSScriptRoot 'Stomme.Sdk/Stomme.Sdk.csproj') -c Release --output $feed
if ($LASTEXITCODE -ne 0) { throw 'pack failed: Stomme.Sdk' }

Write-Host "`nFeed contents:" -ForegroundColor Cyan
Get-ChildItem $feed -Filter *.nupkg | Select-Object -ExpandProperty Name | Sort-Object

# The proof: the smoke module restores + builds against the feed with ZERO source/project references.
$smoke = Join-Path $PSScriptRoot '_sdk-smoke/Stomme.Sdk.Smoke/Stomme.Sdk.Smoke.csproj'
dotnet restore $smoke
if ($LASTEXITCODE -ne 0) { throw 'smoke restore failed' }
dotnet build $smoke -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'smoke build failed' }

Write-Host "`nD81 PROOF: storage-blind smoke module built against packages only, 0 errors." -ForegroundColor Green
