#!/usr/bin/env pwsh
# Builds and packs the KnowledgeBase exemplar (Client/Server/Shared) into ONE Stomme module .nupkg, after running
# its unit tests.
#
# Prerequisite: examples/pack-sdk-slim.ps1 has produced examples/local-feed (the slim SDK the module restores against -
# packages only, zero source references). Then:
#   1. builds the three projects in Release against that feed,
#   2. runs the module's unit tests (KnowledgeBase.Tests),
#   3. packs NovaGloben.Module.KnowledgeBase.<version>.nupkg in the InstallationManager layout (lib/net10.0 + icon),
#   4. optionally copies it into a host's Packages folder (-PackagesFolder) for the real host to install on boot.
# The drive (KnowledgeBase.Drive) then installs that package into the production host on three stores.
#
# No dotnet clean / no obj deletion (BLAZOR106). Run from the repo root: pwsh examples/Modules/KnowledgeBase/pack-knowledgebase.ps1
[CmdletBinding()]
param(
    [string]$PackagesFolder
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$projects = @(
    'NovaGloben.Module.KnowledgeBase.Shared/NovaGloben.Module.KnowledgeBase.Shared.csproj',
    'NovaGloben.Module.KnowledgeBase.Client/NovaGloben.Module.KnowledgeBase.Client.csproj',
    'NovaGloben.Module.KnowledgeBase.Server/NovaGloben.Module.KnowledgeBase.Server.csproj'
)
$nuspec = Join-Path $here 'Package/NovaGloben.Module.KnowledgeBase.nuspec'
$outDir = Join-Path $here 'Package'
$nuget = Join-Path $here '../../../src/Stomme.Package/nuget.exe'

# 1. The three layers, against the local SDK feed only. The restore is FORCED: pack-sdk-slim.ps1 re-packs the SDK at the
#    same version, and NuGet's no-op restore does not notice a package whose content changed (measured - the module kept
#    compiling against the previous closure).
foreach ($project in $projects) {
    dotnet restore (Join-Path $here $project) --force
    if ($LASTEXITCODE -ne 0) { throw "KnowledgeBase restore failed: $project" }
    dotnet build (Join-Path $here $project) -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "KnowledgeBase build failed: $project" }
}

# 2. The module's own unit tests (domain and application, no host).
dotnet test (Join-Path $here 'KnowledgeBase.Tests/NovaGloben.Module.KnowledgeBase.Tests.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'KnowledgeBase unit tests failed' }

# 3. One module nupkg carrying the three assemblies.
Remove-Item (Join-Path $outDir '*.nupkg') -ErrorAction SilentlyContinue
& $nuget pack $nuspec -OutputDirectory $outDir -NoDefaultExcludes
if ($LASTEXITCODE -ne 0) { throw 'nuget pack failed' }

$nupkg = Get-ChildItem (Join-Path $outDir '*.nupkg') | Select-Object -First 1
Write-Host "`nPacked: $($nupkg.FullName)" -ForegroundColor Green

# 4. Optionally stage it for a host's InstallationManager.
if ($PackagesFolder) {
    New-Item -ItemType Directory -Force -Path $PackagesFolder | Out-Null
    Copy-Item $nupkg.FullName -Destination $PackagesFolder -Force
    Write-Host "Staged into: $PackagesFolder" -ForegroundColor Cyan
}
