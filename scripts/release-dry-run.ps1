#!/usr/bin/env pwsh
# Packs every package the way .github/workflows/release.yml packs it and runs ALL of release.yml's guards on them -
# locally, before a tag, so a release guard never meets a package first in CI (infra ruling C31, 2026-10-29: 10.74.0's
# tag failed release.yml's licence guard on the two theme packages, which nothing local had ever checked).
#
#   pwsh scripts/release-dry-run.ps1                      # packs into a temp folder, runs the guards, exits 1 on any failure
#   pwsh scripts/release-dry-run.ps1 -Out D:\feed         # and keeps the packages there (a local feed)
#   pwsh scripts/release-dry-run.ps1 -Check D:\feed       # runs the guards on packages already packed, packs nothing
#
# The guards mirror release.yml's three "Guard -" steps one for one; a change to one changes the other. Every guard
# runs even when an earlier one fails, so one run reports everything. Nothing is pushed, nothing is signed in.
param(
    [string]$Out = '',
    [string]$Check = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
[Environment]::CurrentDirectory = $root
Add-Type -AssemblyName System.IO.Compression.FileSystem

[xml]$rootProps = Get-Content -LiteralPath 'Directory.Build.props'
[xml]$examplesProps = Get-Content -LiteralPath 'examples/Directory.Build.props'
$version = @($rootProps.Project.PropertyGroup.Version | Where-Object { $_ })[0]
$examplesVersion = @($examplesProps.Project.PropertyGroup.Version | Where-Object { $_ })[0]
if ($version -ne $examplesVersion) { throw "Directory.Build.props $version != examples/Directory.Build.props $examplesVersion (release.yml refuses this)" }
"release dry run for $version"

# ---- pack, in release.yml's order -----------------------------------------------------------------------------------
$logs = Join-Path ([IO.Path]::GetTempPath()) 'stomme-release-dry-run'
New-Item -ItemType Directory -Force $logs | Out-Null
function Step([string]$name, [scriptblock]$run) {
    $log = Join-Path $logs "$name.log"
    & $run *> $log
    if ($LASTEXITCODE) { throw "$name failed (exit $LASTEXITCODE): see $log" }
    "  packed: $name"
}

$push = if ($Check) { $Check } elseif ($Out) { $Out } else { Join-Path $logs 'push' }
if (-not $Check) {
try {
    Remove-Item -Recurse -Force examples/local-feed, pkg-out -ErrorAction SilentlyContinue
    New-Item -ItemType Directory pkg-out | Out-Null
    Step 'framework' { pwsh -NoProfile -File scripts/pack-framework.ps1 -Output examples/local-feed }
    Step 'sdk' { dotnet pack examples/Stomme.Sdk/Stomme.Sdk.csproj -c Release --output examples/local-feed -nologo }
    Step 'template' { pwsh -NoProfile -File scripts/pack-template.ps1 -Output pkg-out }
    foreach ($slim in 'Stomme.Server.Sdk', 'Stomme.Client.Sdk', 'Stomme.Sdk.Slim') {
        Step $slim { dotnet pack "examples/Stomme.Sdk.Slim/$slim/$slim.csproj" -c Release --output examples/local-feed -nologo }
    }

    if (Test-Path $push) { Remove-Item -Recurse -Force $push }
    New-Item -ItemType Directory -Force $push | Out-Null
    Copy-Item examples/local-feed/Stomme.*.nupkg, pkg-out/Stomme.*.nupkg $push
}
finally {
    Remove-Item -Recurse -Force examples/local-feed, pkg-out -ErrorAction SilentlyContinue
}
}

# ---- read every package once ----------------------------------------------------------------------------------------
$packages = foreach ($file in Get-ChildItem $push -Filter '*.nupkg' | Sort-Object Name) {
    $zip = [IO.Compression.ZipFile]::OpenRead($file.FullName)
    try {
        $entries = @($zip.Entries | ForEach-Object FullName)
        $read = { param($pattern) $e = $zip.Entries | Where-Object { $_.FullName -like $pattern } | Select-Object -First 1; if ($e) { [IO.StreamReader]::new($e.Open()).ReadToEnd() } }
        $nuspec = & $read '*.nuspec'
        [pscustomobject]@{
            Name = $file.Name
            Id = [regex]::Match($nuspec, '<id>([^<]*)</id>').Groups[1].Value
            Nuspec = $nuspec
            Entries = $entries
            AssetsProps = & $read 'build/Microsoft.AspNetCore.StaticWebAssets.props'
        }
    }
    finally { $zip.Dispose() }
}
"  $($packages.Count) packages"
$failures = [Collections.Generic.List[string]]::new()

# ---- Guard - every package carries the LICENSE file (D115) -----------------------------------------------------------
foreach ($p in $packages) {
    if ($p.Nuspec -notmatch '<license type="file">LICENSE</license>') { $failures.Add("licence: NOT THE LICENSE FILE: $($p.Name)") }
    if ($p.Entries -notcontains 'LICENSE') { $failures.Add("licence: NO LICENSE AT THE PACKAGE ROOT: $($p.Name)") }
}
'guard: licence - checked'

# ---- Guard - the theme packs declare exactly the assets they package (BL-70) -----------------------------------------
foreach ($p in $packages | Where-Object { $_.Name -like 'Stomme.Theme.*.nupkg' }) {
    $packaged = @($p.Entries | Where-Object { $_ -like 'staticwebassets/*' -and -not $_.EndsWith('/') }).Count
    $declared = if ($p.AssetsProps) { ([regex]::Matches($p.AssetsProps, '<StaticWebAsset ')).Count } else { 0 }
    "  $($p.Name): packaged=$packaged declared=$declared"
    if ($packaged -ne $declared) { $failures.Add("theme assets: POISONED PACK: $($p.Name) (packaged $packaged, declared $declared)") }
}
'guard: theme assets - checked'

# ---- Guard - no pushed package depends on an id this run does not push ------------------------------------------------
$ids = @($packages | ForEach-Object Id | Sort-Object -Unique)
foreach ($p in $packages) {
    foreach ($dep in [regex]::Matches($p.Nuspec, '<dependency id="([^"]*)"') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique) {
        if (($dep -like 'Stomme.*' -or $dep -like 'NovaGloben.*') -and $ids -notcontains $dep) {
            $failures.Add("dependencies: $($p.Name) depends on $dep, which this run does not push")
        }
    }
}
'guard: dependencies - checked'

''
$packages | ForEach-Object { "  $($_.Name)" }
if ($failures.Count) {
    ''
    $failures | ForEach-Object { "FAILED $_" }
    exit 1
}
"release dry run: PASSED ($($packages.Count) packages, every guard) - packages in $push"
