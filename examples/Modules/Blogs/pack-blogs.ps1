#!/usr/bin/env pwsh
# Packs the independent, storage-blind, WASM-download-safe SPLIT Blogs module
# (Client/Server/Shared) to a single Stomme module .nupkg.
#
# Prerequisite: examples/pack-sdk.ps1 has produced examples/local-feed (the Stomme.Sdk packages the
# module restores against — packages only, ZERO source references). This script then:
#   1. builds the three projects in Release against that feed (assemblies
#      NovaGloben.Module.Blogs.{Client,Server,Shared}.Stomme.dll — every name contains "Stomme" so the
#      convention loader loads them),
#   2. packs them to NovaGloben.Module.Blogs.2.0.0.nupkg in the InstallationManager layout (lib/net10.0 + icon),
#   3. (optionally) drops the nupkg into a Stomme.Server Packages folder for the real host to install.
#
# No dotnet clean / no obj deletion (BLAZOR106). Run from the repo root: pwsh examples/Modules/Blogs/pack-blogs.ps1
# Pass -PackagesFolder <path> to also copy the nupkg into a host's {contentRoot}/Packages folder.
[CmdletBinding()]
param(
    [string]$PackagesFolder
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$projects = @(
    'NovaGloben.Module.Blogs.Shared/NovaGloben.Module.Blogs.Shared.csproj',
    'NovaGloben.Module.Blogs.Client/NovaGloben.Module.Blogs.Client.csproj',
    'NovaGloben.Module.Blogs.Server/NovaGloben.Module.Blogs.Server.csproj'
)
$nuspec = Join-Path $here 'Package/NovaGloben.Module.Blogs.nuspec'
$outDir = Join-Path $here 'Package'
$nuget = Join-Path $here '../../../src/Stomme.Package/nuget.exe'

# 1. Build all three layers against the local SDK feed (packages only).
foreach ($project in $projects) {
    dotnet build (Join-Path $here $project) -c Release
    if ($LASTEXITCODE -ne 0) { throw "Blogs build failed: $project" }
}

# 2. Pack the nuspec into ONE Stomme module nupkg carrying all three DLLs (the layout
#    InstallationManager expects — it extracts lib/net*/ into the host /bin).
Remove-Item (Join-Path $outDir '*.nupkg') -ErrorAction SilentlyContinue
& $nuget pack $nuspec -OutputDirectory $outDir -NoDefaultExcludes
if ($LASTEXITCODE -ne 0) { throw 'nuget pack failed' }

$nupkg = Get-ChildItem (Join-Path $outDir '*.nupkg') | Select-Object -First 1
Write-Host "`nPacked: $($nupkg.FullName)" -ForegroundColor Green

# 3. Optionally stage into a host Packages folder for the real InstallationManager to unpack on boot.
if ($PackagesFolder) {
    New-Item -ItemType Directory -Force -Path $PackagesFolder | Out-Null
    Copy-Item $nupkg.FullName -Destination $PackagesFolder -Force
    Write-Host "Staged into: $PackagesFolder" -ForegroundColor Cyan
}
