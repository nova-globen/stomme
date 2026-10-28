#!/usr/bin/env pwsh
# D87. Packs the two independent, packages-only external THEMES (Aurora = public, Slate = admin) to Stomme
# theme .nupkgs, the same way examples/Modules/Blogs/pack-blogs.ps1 packs the Blogs module.
#
# Prerequisite: examples/pack-sdk.ps1 has produced examples/local-feed (the Stomme.Sdk packages the themes
# restore against — packages only, ZERO source references). For each theme this script:
#   1. builds the theme in Release against that feed (assembly <Theme>.Stomme.dll + wwwroot/Theme.css),
#   2. packs it to <id>.1.0.0.nupkg in the InstallationManager layout (lib/net10.0 + staticwebassets + icon),
#   3. (optionally) drops the nupkg into a Stomme.Server Packages folder for the real host to install.
#
# No dotnet clean / no obj deletion (BLAZOR106). Run from the repo root: pwsh examples/Themes/pack-themes.ps1
# Pass -PackagesFolder <path> to also copy the nupkgs into a host's {contentRoot}/Packages folder.
[CmdletBinding()]
param(
    [string]$PackagesFolder
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$nuget = Join-Path $here '../../src/Stomme.Package/nuget.exe'

$themes = @(
    @{ Name = 'Stomme.Theme.Aurora' },
    @{ Name = 'Stomme.Theme.Slate' }
)

foreach ($theme in $themes)
{
    $name = $theme.Name
    $project = Join-Path $here "$name/$name/$name.csproj"
    $nuspec = Join-Path $here "$name/Package/$name.nuspec"
    $outDir = Join-Path $here "$name/Package"

    Write-Host "`n=== $name ===" -ForegroundColor Yellow

    # 1. Build against the local SDK feed (packages only).
    dotnet build $project -c Release
    if ($LASTEXITCODE -ne 0) { throw "$name build failed" }

    # 2. Pack the nuspec into a Stomme theme nupkg (the layout InstallationManager expects).
    Remove-Item (Join-Path $outDir '*.nupkg') -ErrorAction SilentlyContinue
    & $nuget pack $nuspec -OutputDirectory $outDir -NoDefaultExcludes
    if ($LASTEXITCODE -ne 0) { throw "$name nuget pack failed" }

    $nupkg = Get-ChildItem (Join-Path $outDir '*.nupkg') | Select-Object -First 1
    Write-Host "Packed: $($nupkg.FullName)" -ForegroundColor Green

    # 3. Optionally stage into a host Packages folder for the real InstallationManager to unpack on boot.
    if ($PackagesFolder) {
        New-Item -ItemType Directory -Force -Path $PackagesFolder | Out-Null
        Copy-Item $nupkg.FullName -Destination $PackagesFolder -Force
        Write-Host "Staged into: $PackagesFolder" -ForegroundColor Cyan
    }
}
