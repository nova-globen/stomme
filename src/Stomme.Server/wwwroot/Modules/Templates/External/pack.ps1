# Builds and packs the module: Package/[Owner].Module.[Module].1.0.0.nupkg. With -PackagesFolder, also copies the package
# into a host's Packages folder, where its InstallationManager installs it on the next start.
[CmdletBinding()]
param(
    [string]$PackagesFolder
)

$ErrorActionPreference = 'Stop'
$package = Join-Path $PSScriptRoot 'Package/[Owner].Module.[Module].Package.csproj'
Remove-Item (Join-Path $PSScriptRoot 'Package/*.nupkg') -ErrorAction SilentlyContinue
dotnet pack $package -c Release
if ($LASTEXITCODE -ne 0) { throw 'dotnet pack failed' }

$nupkg = Get-ChildItem (Join-Path $PSScriptRoot 'Package/*.nupkg') | Select-Object -First 1
Write-Host "Packed: $($nupkg.FullName)"
if ($PackagesFolder) {
    New-Item -ItemType Directory -Force -Path $PackagesFolder | Out-Null
    Copy-Item $nupkg.FullName -Destination $PackagesFolder -Force
    Write-Host "Staged into: $PackagesFolder"
}
