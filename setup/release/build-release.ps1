# Builds a Nova Globen Stomme self-host release for a downloads host. For each variant x rid in variants.json it runs
# build-bundle.ps1 and lays the release out as the network installers read it (<base>/<version|latest>/):
#
#   setup/dist/release/<version>/
#     stomme-<variant>-<rid>.zip     one per variant x rid - no version in a file name; the folder carries it
#     install.sh, install.ps1        the network installers, so <base>/latest/install.sh works
#     release-manifest.json          what install.sh / install.ps1 read: variants, rids and asset names
#     SHA256SUMS                     every file above; the installers refuse a bundle it does not list
#
# It builds and writes only; publishing is a release act done after it: copy the folder to <base>/<version>/, then make
# <base>/latest/ an exact copy of it. (Nova Globen's own publishing commands are in its internal release notes.)
#
#   pwsh setup/release/build-release.ps1 -Version 10.77.0
#   pwsh setup/release/build-release.ps1 -Version 10.77.0 -Rids linux-x64 -Variants full   # a quick partial build
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string[]]$Rids,
    [string[]]$Variants
)
$ErrorActionPreference = "Stop"
$setup = (Resolve-Path "$PSScriptRoot/..").Path
$config = Get-Content -Raw (Join-Path $PSScriptRoot "variants.json") | ConvertFrom-Json
$ridList = if ($Rids) { $Rids } else { $config.rids }
# (PowerShell names ignore case: these locals must not be called $rids/$variants, or they overwrite the typed params.)
$variantList = @($config.variants | Where-Object { -not $Variants -or $Variants -contains $_.name })
if (-not $variantList) { throw "No variant matches '$($Variants -join ', ')'." }

$release = Join-Path $setup "dist/release/$Version"
if (Test-Path $release) { throw "$release already exists - a version is built once; remove it on purpose or use a new version." }
New-Item -ItemType Directory -Force -Path $release | Out-Null

$manifest = [ordered]@{ product = "stomme"; version = $Version; variants = @() }
foreach ($variant in $variantList) {
    $bundles = @()
    foreach ($rid in $ridList) {
        # Each variant x rid builds in a folder of its own: build-bundle reuses stomme-<rid>, and a framework-dependent
        # publish into a folder a self-contained one used would carry its runtime along.
        $work = Join-Path $setup "dist/build/$Version/$($variant.name)"
        & pwsh -NoProfile -File (Join-Path $setup "build-bundle.ps1") -Rid $rid -OutputRoot $work -Version $Version `
            -Variant $variant.name -SelfContained:([bool]$variant.selfContained) -Zip
        if ($LASTEXITCODE -ne 0) { throw "build-bundle failed for $($variant.name) / $rid." }
        $asset = "stomme-$($variant.name)-$rid.zip"
        Move-Item (Join-Path $work $asset) (Join-Path $release $asset)
        $bundles += [ordered]@{ rid = $rid; asset = $asset }
    }
    $manifest.variants += [ordered]@{ name = $variant.name; displayName = $variant.displayName; bundles = $bundles }
}

Copy-Item (Join-Path $setup "install.sh") $release
Copy-Item (Join-Path $setup "install.ps1") $release
$manifest | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $release "release-manifest.json") -Encoding utf8NoBOM

# sha256sum's own format (hash, two spaces, name), LF endings, so `sha256sum -c SHA256SUMS` verifies it as is.
$lines = Get-ChildItem $release -File | Where-Object Name -ne "SHA256SUMS" | Sort-Object Name |
    ForEach-Object { "{0}  {1}" -f (Get-FileHash -Algorithm SHA256 $_.FullName).Hash.ToLowerInvariant(), $_.Name }
[IO.File]::WriteAllText((Join-Path $release "SHA256SUMS"), (($lines -join "`n") + "`n"), (New-Object Text.UTF8Encoding($false)))

Write-Host ""
Write-Host "Release $Version built: $release"
Get-ChildItem $release -File | ForEach-Object { Write-Host ("  {0,12:N0}  {1}" -f $_.Length, $_.Name) }
Write-Host ""
Write-Host "Publish it: copy this folder to <base>/$Version/ on the downloads host, then make <base>/latest/ an exact copy of it."
