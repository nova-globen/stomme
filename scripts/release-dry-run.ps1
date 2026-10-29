#!/usr/bin/env pwsh
# Packs every package the way .github/workflows/release.yml packs it and runs ALL of release.yml's guards on them -
# locally, before a tag, so a release guard never meets a package first in CI (infra ruling C31, 2026-10-29: 10.74.0's
# tag failed release.yml's licence guard on the two theme packages, which nothing local had ever checked).
#
#   pwsh scripts/release-dry-run.ps1                      # packs into a temp folder, runs the guards, exits 1 on any failure
#   pwsh scripts/release-dry-run.ps1 -Out D:\feed         # and keeps the packages there (a local feed)
#   pwsh scripts/release-dry-run.ps1 -Check D:\feed       # runs the guards on packages already packed, packs nothing
#
# The first three guards mirror release.yml's three "Guard -" steps one for one; a change to one changes the other. The
# rest are this script's own: no package names an old product, a forge or an infrastructure host; the shipped agent assets
# changed with the code (or the release notes say why not); and the consumer audit, a plain-SDK project over every package,
# failing on a high or critical NuGet advisory a consumer would inherit. Every guard runs even when an earlier one fails,
# so one run reports everything.
# Nothing is pushed, nothing is signed in.
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
            Version = [regex]::Match($nuspec, '<version>([^<]*)</version>').Groups[1].Value
            IsTemplate = $nuspec -match '<packageType name="Template"'
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
    # BL-259: a stock theme carries the release's version, so changed content never ships under a number already out.
    if ($p.Version -ne $version) { $failures.Add("theme version: $($p.Name) is $($p.Version), not the release's $version") }
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

# ---- Guard - no package names an old product or the infrastructure (2026-10-29, the owner) ----------------------------
# Every file of every package, the app template's agent assets (AGENTS.md, CLAUDE.md, docs/, .claude/skills/) included,
# read as bytes - as Latin-1 and as UTF-16, so a string in a DLL is found too. A package carries none of the old product
# names, no forge or registry host, no host of the infrastructure and no broker command.
$forbidden = @(
    @{ Name = 'an old product name'; Pattern = 'd[i]da[ ._-]?(core|bit|deck|bin)' }
    @{ Name = 'a forge or registry host'; Pattern = 'forgejo|(git|store)\.d[i]da|dl-internal' }
    @{ Name = 'an infrastructure host'; Pattern = '(runner|docker|store)0\d|ros[t]am|192\.168\.0\.11' }
    @{ Name = 'a broker command'; Pattern = '(^|[^a-z0-9])ng (exec|api|run|ops|actions|put-profile) ' }
)
foreach ($file in Get-ChildItem $push -Filter '*.nupkg' | Sort-Object Name) {
    $zip = [IO.Compression.ZipFile]::OpenRead($file.FullName)
    try {
        foreach ($entry in $zip.Entries | Where-Object { $_.Length -gt 0 }) {
            $stream = $entry.Open()
            $buffer = [IO.MemoryStream]::new()
            try { $stream.CopyTo($buffer) } finally { $stream.Dispose() }
            $bytes = $buffer.ToArray()
            $text = ([Text.Encoding]::Latin1.GetString($bytes) + "`n" + [Text.Encoding]::Unicode.GetString($bytes)).ToLowerInvariant()
            foreach ($rule in $forbidden) {
                $hit = [regex]::Match($text, $rule.Pattern)
                if ($hit.Success) { $failures.Add("names: $($file.Name)/$($entry.FullName) carries $($rule.Name) ('$($hit.Value.Trim())')") }
            }
        }
    }
    finally { $zip.Dispose() }
}
'guard: names - checked'

# ---- Guard - the shipped agent assets move with the code (2026-10-29, the owner) --------------------------------------
# What an agent building on Stomme knows is the app template's AGENTS.md, CLAUDE.md, README.md, docs/ and .claude/skills/.
# A release whose product code changed since the previous release changes them too, or its release notes say why not
# with a line starting "No agent-asset change needed:".
$releases = @(git log --format='%H %s' --grep='^release: ' -n 20 HEAD | Where-Object { $_ -notmatch "^\S+ release: $([regex]::Escape($version))( |$)" })
if (-not $releases) {
    $failures.Add('agent assets: no previous release commit found to compare with')
}
else {
    $base = $releases[0].Split(' ')[0]
    $changed = @(git diff --name-only $base HEAD)
    $product = @($changed | Where-Object { $_ -like 'src/Stomme.*' -and $_ -notlike 'src/Stomme.Application/*' -and $_ -notlike 'src/Stomme.Package/*' -and $_ -match '\.(cs|razor|resx|json|css|js|csproj|props|targets|nuspec)$' })
    $assets = @($changed | Where-Object { $_ -like 'src/Stomme.Application/docs/*' -or $_ -like 'src/Stomme.Application/.claude/*' -or $_ -in 'src/Stomme.Application/AGENTS.md', 'src/Stomme.Application/CLAUDE.md', 'src/Stomme.Application/README.md' })
    "  since $($releases[0].Substring(0, 9)): $($product.Count) product file(s) changed, $($assets.Count) agent-asset file(s) changed"
    if ($product.Count -and -not $assets.Count) {
        $notes = Get-Content -Raw -LiteralPath 'docs/releasing.md'
        $section = [regex]::Match($notes, "(?ms)^## Release notes for $([regex]::Escape($version))\b.*?(?=^## )").Value
        if ($section -notmatch '(?m)^\s*-?\s*No agent-asset change needed:') {
            $failures.Add("agent assets: $($product.Count) product file(s) changed since the previous release and no shipped agent asset did; update docs/ or the skills, or say 'No agent-asset change needed: <why>' in the $version release notes")
        }
    }
}
'guard: agent assets - checked'

# ---- Guard - no consumer inherits a high or critical advisory (2026-10-29) --------------------------------------------
# A PLAIN SDK project referencing every package, restored with NuGet's audit over the whole graph: what a class library
# or test project built on Stomme sees. The framework's own restore cannot stand in for it - a Web SDK project prunes what
# the ASP.NET Core shared framework carries, and so missed GHSA-rxg9-xrhp-64gj (System.Drawing.Common 4.7.0, NU1904),
# which reached every plain-SDK consumer of Stomme.Server 10.74.1. The Stomme.* folders of the audit's package folder are
# emptied first, so a cached package of the same version never stands in for the one packed here.
$audit = Join-Path ([IO.Path]::GetTempPath()) 'stomme-release-audit'
$auditPackages = Join-Path $audit 'packages'
New-Item -ItemType Directory -Force $auditPackages | Out-Null
Get-ChildItem $auditPackages -Directory -Filter 'stomme.*' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
Remove-Item -Recurse -Force (Join-Path $audit 'obj') -ErrorAction SilentlyContinue
$references = $packages | Where-Object { -not $_.IsTemplate } | ForEach-Object { "    <PackageReference Include=""$($_.Id)"" Version=""$($_.Version)"" />" }
@(
    '<Project Sdk="Microsoft.NET.Sdk">',
    '  <PropertyGroup>',
    '    <TargetFramework>net10.0</TargetFramework>',
    '    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>',
    '    <NuGetAudit>true</NuGetAudit>',
    '    <NuGetAuditMode>all</NuGetAuditMode>',
    '    <NuGetAuditLevel>low</NuGetAuditLevel>',
    '  </PropertyGroup>',
    '  <ItemGroup>'
) + $references + @('  </ItemGroup>', '</Project>') | Set-Content -Encoding utf8 (Join-Path $audit 'Consumer.csproj')
@(
    '<?xml version="1.0" encoding="utf-8"?>',
    '<configuration>',
    '  <packageSources>',
    '    <clear />',
    "    <add key=""packed"" value=""$push"" />",
    '    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />',
    '  </packageSources>',
    '  <packageSourceMapping>',
    '    <packageSource key="packed"><package pattern="Stomme.*" /></packageSource>',
    '    <packageSource key="nuget.org"><package pattern="*" /></packageSource>',
    '  </packageSourceMapping>',
    '</configuration>'
) | Set-Content -Encoding utf8 (Join-Path $audit 'NuGet.config')
$auditLog = Join-Path $logs 'consumer-audit.log'
& dotnet restore (Join-Path $audit 'Consumer.csproj') --packages $auditPackages --configfile (Join-Path $audit 'NuGet.config') --force-evaluate -nologo *> $auditLog
if ($LASTEXITCODE) { $failures.Add("consumer audit: the consumer's restore failed (exit $LASTEXITCODE): see $auditLog") }
$advisories = Select-String -LiteralPath $auditLog -Pattern 'NU190\d' | ForEach-Object {
    [regex]::Match($_.Line, "NU190\d: Package '[^']+' [^ ]+ has a known [a-z]+ severity vulnerability, \S+").Value
} | Where-Object { $_ } | Sort-Object -Unique
foreach ($advisory in $advisories) {
    "  $advisory"
    if ($advisory -match '^NU190[34]') { $failures.Add("consumer audit: $advisory") }
}
"guard: consumer audit - checked ($(@($advisories).Count) advisories; high and critical fail)"

''
$packages | ForEach-Object { "  $($_.Name)" }
if ($failures.Count) {
    ''
    $failures | ForEach-Object { "FAILED $_" }
    exit 1
}
"release dry run: PASSED ($($packages.Count) packages, every guard) - packages in $push"
