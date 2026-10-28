#!/usr/bin/env pwsh
# Permissive-license gate (see docs/decisions.md D2; BL-118).
#
# TWO checks, both offline and deterministic:
#
#  1. DECLARED: no build file references a package on the non-permissive DENYLIST below (unless a documented,
#     time-limited exception names it). This carries the reasons and the alternatives - it is advice as well as a gate.
#
#  2. RESOLVED (BL-118): every package the restore actually resolved - direct AND transitive, read from each
#     project's obj/project.assets.json - carries a licence this gate can PROVE is permissive, from the package's own
#     nuspec in the restore's package folder: an SPDX expression on the ALLOWLIST; or, for a package that ships only a
#     licence FILE or a licence URL, an entry in $VerifiedFiles / $VerifiedUrls recording what that file or URL was READ
#     to be; or a documented $Exceptions entry. Anything else FAILS. The denylist alone passed every package it did not
#     name, and every transitive dependency - the hole BL-118 was.
#
#   pwsh scripts/check-licenses.ps1                     # the repository (every restored project under it)
#   pwsh scripts/check-licenses.ps1 -AssetsRoot <dir>   # only the project.assets.json files under <dir> (the self-test)
param(
    [string]$AssetsRoot
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# ---- 1. DECLARED: known non-permissive package IDs, with the reason and the alternative ---------------------------------

$Deny = [ordered]@{
    'MySql.Data'                = 'GPL-2.0 (Oracle) - use MySqlConnector (MIT)'
    'MySql.EntityFrameworkCore' = 'GPL-2.0 (Oracle) - use Pomelo.EntityFrameworkCore.MySql (MIT)'
    'SixLabors.ImageSharp'      = 'Six Labors Split License - use SkiaSharp (MIT) or Magick.NET (Apache-2.0)'
    'SixLabors.ImageSharp.Web'  = 'Six Labors Split License'
    'SixLabors.Fonts'           = 'Six Labors Split License'
    'iTextSharp'                = 'AGPL / commercial'
    'itext7'                    = 'AGPL / commercial'
    'ShortGuid'                 = 'No license declared in the 2.0.1 nuspec - use Stomme.Engine.Pipelines.ShortGuid (in-house, BL-93)'
    # Export/rendering libraries someone will reach for next to DocumentFormat.OpenXml (BL-85 / D108). Each
    # package's own licence FILE was read from its nupkg on 2026-09-20 - none declares an SPDX expression.
    'QuestPDF'                  = 'Dual Community/Professional licence with a revenue threshold (2026.9.0 LICENSE.md) - PDF stays in-house; PdfSharp/MigraDoc (MIT) is the candidate to verify'
    'EPPlus'                    = 'Polyform Noncommercial 1.0.0 (8.7.0 license.md) - use DocumentFormat.OpenXml (MIT)'
    'Aspose.Cells'              = 'Commercial EULA, licence acceptance required (26.9.0) - use DocumentFormat.OpenXml (MIT)'
    'Aspose.PDF'                = 'Commercial EULA, licence acceptance required (26.9.0)'
    'Syncfusion.XlsIO.Net.Core' = 'Syncfusion Essential Studio commercial agreement (34.2.8 LICENSE.txt) - use DocumentFormat.OpenXml (MIT)'
    'Syncfusion.Pdf.Net.Core'   = 'Syncfusion Essential Studio commercial agreement (34.2.8 LICENSE.txt)'
}

# Documented, time-limited exceptions to the DENYLIST. Burn these down (docs/decisions.md D2) - empty this map.
$Except = [ordered]@{
}

# ---- 2. RESOLVED: what a resolved package's licence must be ---------------------------------------------------------------

# SPDX identifiers that are permissive. An expression passes when one of its OR-alternatives is made only of these.
$Permissive = @(
    'MIT', 'MIT-0', 'Apache-2.0', 'BSD-2-Clause', 'BSD-3-Clause', '0BSD', 'ISC', 'MS-PL', 'Zlib', 'PostgreSQL',
    'BSL-1.0', 'Unlicense', 'CC0-1.0'
)

# Packages that ship only a licence FILE: what the file was READ to be, per id AND version - a new version is read again.
$VerifiedFiles = [ordered]@{
    'Lambda2Js.Signed/3.1.4'          = 'MIT (LICENSE.txt read 2026-09-23: "The MIT License (MIT)")'
    'MQTTnet/5.2.0.1603'               = 'MIT (LICENSE read 2026-10-17: "The MIT License (MIT)", .NET Foundation and Contributors)'
    'Microsoft.Web.WebView2/1.0.3179.45' = 'BSD-3-Clause (LICENSE.txt read 2026-09-23: the three BSD redistribution conditions, Microsoft copyright)'
    'SourceGear.sqlite3/3.50.4.5'     = 'Public domain (LICENSE.txt read 2026-09-23: "SQLite is Public Domain")'
}

# Packages that ship only a licence URL: what the URL is, per URL (a URL names a document, not a version).
$VerifiedUrls = [ordered]@{
    'https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet/blob/master/LICENSE.txt' = 'MIT (the repository licence)'
    'https://github.com/dotnet/corefx/blob/master/LICENSE.TXT'                                                   = 'MIT (the corefx repository licence)'
    'https://raw.githubusercontent.com/xunit/xunit/master/license.txt'                                           = 'Apache-2.0 (the xunit repository licence; test-only)'
}

# Documented exceptions: packages whose licence is NOT permissive but which this product takes on deliberately. Each
# names what it is, why it is here, and where it is used. NOT debt to burn down like $Except: Microsoft platform
# components with no permissive alternative, printed on every run so they stay visible.
$Exceptions = [ordered]@{
    'Microsoft.Data.SqlClient.SNI.runtime' = 'Microsoft Software License Terms (redistributable native SNI library) - the network layer of Microsoft.Data.SqlClient (MIT) on Windows; required by the SQL Server provider'
    'Microsoft.WindowsAppSDK'              = 'Microsoft Software License Terms - the Windows App SDK runtime; the MAUI host''s Windows target only (Stomme.Maui), not the web product'
    'Microsoft.Graphics.Win2D'             = 'Microsoft Win2D EULA - a Windows App SDK dependency; the MAUI host''s Windows target only'
    'Microsoft.Windows.SDK.BuildTools'     = 'Microsoft Windows SDK licence - BUILD-TIME tooling for the MAUI Windows target; nothing of it ships'
    # The Windows App SDK 1.8 restores as its parts (measured 2026-10-23: the MAUI host's assets rewritten mid-gate, each part
    # shipping only its licence FILE). Each file read that day: "MICROSOFT SOFTWARE LICENSE TERMS / MICROSOFT WINDOWS APP SDK",
    # the parent's terms above; the MSIX tooling's "MICROSOFT WINDOWS SOFTWARE DEVELOPMENT KIT (SDK) FOR WINDOWS 10", the
    # build tools' terms above. The MAUI host's Windows target only, as their parent.
    'Microsoft.WindowsAppSDK.AI'                    = 'Microsoft Software License Terms (Windows App SDK) - a part of the Windows App SDK; the MAUI host''s Windows target only'
    'Microsoft.WindowsAppSDK.Base'                  = 'Microsoft Software License Terms (Windows App SDK) - a part of the Windows App SDK; the MAUI host''s Windows target only'
    'Microsoft.WindowsAppSDK.DWrite'                = 'Microsoft Software License Terms (Windows App SDK) - a part of the Windows App SDK; the MAUI host''s Windows target only'
    'Microsoft.WindowsAppSDK.Foundation'            = 'Microsoft Software License Terms (Windows App SDK) - a part of the Windows App SDK; the MAUI host''s Windows target only'
    'Microsoft.WindowsAppSDK.InteractiveExperiences' = 'Microsoft Software License Terms (Windows App SDK) - a part of the Windows App SDK; the MAUI host''s Windows target only'
    'Microsoft.WindowsAppSDK.ML'                    = 'Microsoft Software License Terms (Windows App SDK) - a part of the Windows App SDK; the MAUI host''s Windows target only'
    'Microsoft.WindowsAppSDK.Runtime'               = 'Microsoft Software License Terms (Windows App SDK) - a part of the Windows App SDK; the MAUI host''s Windows target only'
    'Microsoft.WindowsAppSDK.Widgets'               = 'Microsoft Software License Terms (Windows App SDK) - a part of the Windows App SDK; the MAUI host''s Windows target only'
    'Microsoft.WindowsAppSDK.WinUI'                 = 'Microsoft Software License Terms (Windows App SDK) - a part of the Windows App SDK; the MAUI host''s Windows target only'
    'Microsoft.Windows.SDK.BuildTools.MSIX'         = 'Microsoft Windows SDK licence - BUILD-TIME MSIX packaging tooling for the MAUI Windows target; nothing of it ships'
    'NETStandard.Library'                  = 'The legacy "MICROSOFT .NET LIBRARY" licence link (go.microsoft.com LinkId=329770) of the 1.x packages, relicensed MIT from 2.0; pulled transitively by old netstandard1.x dependencies'
    'Microsoft.NETCore.Platforms'          = 'The legacy "MICROSOFT .NET LIBRARY" licence link (LinkId=329770) of the 1.x package, relicensed MIT later; transitive'
}

function Test-Permissive([string]$expression) {
    # SPDX: AND binds tighter than OR; "X WITH exception" is judged by X; parentheses are flattened, which is exact for
    # every expression a restored package here carries (measured: plain ids and one "A OR B").
    $flat = ($expression -replace '[()]', ' ').Trim()
    foreach ($alternative in ($flat -split '\s+OR\s+')) {
        $terms = $alternative -split '\s+AND\s+' | ForEach-Object { ($_ -split '\s+WITH\s+')[0].Trim() }
        if (@($terms | Where-Object { $Permissive -notcontains $_ }).Count -eq 0) {
            return $true
        }
    }

    return $false
}

$violations = @()
$exceptionsHit = @()

# ---- 1. DECLARED -------------------------------------------------------------------------------------------------------------
if (-not $AssetsRoot) {
    # Every build file that can declare a PackageReference - NOT just csproj. A Directory.Build.props applies its
    # references to every project beneath it (measured 2026-09-13: denylisted ids planted in one passed a csproj-only sweep).
    $buildFiles = Get-ChildItem -Path $root -Recurse -File -Include *.csproj, *.props, *.targets |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj|\.claude)[\\/]' }

    foreach ($proj in $buildFiles) {
        [xml]$xml = Get-Content -Raw -LiteralPath $proj.FullName
        $ids = $xml.SelectNodes('//PackageReference') | ForEach-Object { $_.GetAttribute('Include') } | Where-Object { $_ }
        foreach ($id in $ids) {
            if ($Deny.Contains($id)) {
                $rel = $proj.FullName.Substring($root.Length).TrimStart('\', '/')
                if ($Except.Contains($id)) {
                    $exceptionsHit += [pscustomobject]@{ Package = $id; Where = $rel; Reason = $Except[$id] }
                }
                else {
                    $violations += [pscustomobject]@{ Package = $id; Where = $rel; Reason = $Deny[$id] }
                }
            }
        }
    }
}

# ---- 2. RESOLVED -------------------------------------------------------------------------------------------------------------
$scanRoot = if ($AssetsRoot) { $AssetsRoot } else { $root }
$assets = Get-ChildItem -Path $scanRoot -Recurse -File -Filter project.assets.json |
    Where-Object { $_.FullName -notmatch '[\\/](bin|\.claude)[\\/]' }

if (-not $AssetsRoot -and $assets.Count -eq 0) {
    Write-Host 'License gate: FAILED - no obj/project.assets.json found; restore first (the resolved check reads what the restore resolved).' -ForegroundColor Red
    exit 1
}

$resolved = @{}
foreach ($file in $assets) {
    $json = Get-Content -Raw -LiteralPath $file.FullName | ConvertFrom-Json -AsHashtable
    $folders = @($json.packageFolders.Keys)
    foreach ($key in $json.libraries.Keys) {
        $library = $json.libraries[$key]
        if ($library.type -eq 'package' -and -not $resolved.ContainsKey($key)) {
            $resolved[$key] = @{ Path = $library.path; Folders = $folders; Project = $file.FullName }
        }
    }
}

$platform = @()
foreach ($key in ($resolved.Keys | Sort-Object)) {
    $id, $version = $key -split '/', 2
    $entry = $resolved[$key]
    $nuspec = $null
    foreach ($folder in $entry.Folders) {
        $candidate = Join-Path (Join-Path $folder $entry.Path) ($id.ToLowerInvariant() + '.nuspec')
        if (Test-Path -LiteralPath $candidate) { $nuspec = $candidate; break }
    }

    if ($Exceptions.Contains($id)) {
        $platform += [pscustomobject]@{ Package = $key; Reason = $Exceptions[$id] }
        continue
    }

    # This repository's OWN packages (packed from this tree into examples/local-feed, versioned off Directory.Build.props,
    # restored by the examples that prove module independence - D81/D82, BL-96's connector template). They ship the
    # repository's own LICENSE file, not a third-party licence, so there is nothing to prove permissive: they are ours.
    # The stock themes and the exemplar are packed from examples/ too; since BL-187 (1.1.2) the themes declare that same file.
    if ($id -like 'Stomme.*' -or $id -in @('NovaGloben.Module.Blogs', 'Stomme.Theme.Aurora', 'Stomme.Theme.Slate', 'NovaGloben.Module.KnowledgeBase')) {
        $platform += [pscustomobject]@{ Package = $key; Reason = "this repository's own package (the root LICENSE; packed from this tree)" }
        continue
    }

    if (-not $nuspec) {
        $violations += [pscustomobject]@{ Package = $key; Where = 'resolved'; Reason = 'its nuspec is not in the restore''s package folder, so its licence cannot be read' }
        continue
    }

    [xml]$spec = Get-Content -Raw -LiteralPath $nuspec
    $metadata = $spec.package.metadata
    $license = $metadata.license
    if ($license -and ($license.type -eq 'expression' -or -not $license.type)) {
        $expression = if ($license -is [string]) { $license } else { $license.'#text' }
        if (-not (Test-Permissive $expression)) {
            $violations += [pscustomobject]@{ Package = $key; Where = 'resolved'; Reason = "licence expression '$expression' is not on the permissive allowlist" }
        }
    }
    elseif ($license -and $license.type -eq 'file') {
        if (-not $VerifiedFiles.Contains($key)) {
            $violations += [pscustomobject]@{ Package = $key; Where = 'resolved'; Reason = "ships only a licence FILE ($($license.'#text')) - read it and record it in `$VerifiedFiles, or add it to `$Exceptions" }
        }
    }
    elseif ($metadata.licenseUrl) {
        if (-not $VerifiedUrls.Contains([string]$metadata.licenseUrl)) {
            $violations += [pscustomobject]@{ Package = $key; Where = 'resolved'; Reason = "declares only a licence URL ($($metadata.licenseUrl)) - read it and record it in `$VerifiedUrls, or add it to `$Exceptions" }
        }
    }
    else {
        $violations += [pscustomobject]@{ Package = $key; Where = 'resolved'; Reason = 'declares no licence at all' }
    }
}

if ($exceptionsHit.Count -gt 0) {
    Write-Host 'License gate: documented denylist exceptions (debt to burn down):' -ForegroundColor Yellow
    $exceptionsHit | ForEach-Object { Write-Host ("  ! {0}  [{1}]  {2}" -f $_.Package, $_.Where, $_.Reason) -ForegroundColor Yellow }
}

if ($platform.Count -gt 0) {
    Write-Host ("License gate: {0} resolved package(s) under a documented exception:" -f $platform.Count) -ForegroundColor Yellow
    $platform | ForEach-Object { Write-Host ("  ~ {0}  {1}" -f $_.Package, $_.Reason) -ForegroundColor Yellow }
}

if ($violations.Count -gt 0) {
    Write-Host 'License gate: FAILED - licences not proven permissive (docs/decisions.md D2, BL-118):' -ForegroundColor Red
    $violations | ForEach-Object { Write-Host ("  x {0}  [{1}]  {2}" -f $_.Package, $_.Where, $_.Reason) -ForegroundColor Red }
    exit 1
}

Write-Host ("License gate: PASSED (permissive-only; {0} resolved package(s) checked; {1} documented exception(s))." -f $resolved.Count, ($exceptionsHit.Count + $platform.Count)) -ForegroundColor Green
exit 0
