#!/usr/bin/env pwsh
# The PRODUCTION-SLIM module SDK.
#
# Packs the slim Stomme module-facing SDK to the LOCAL NuGet feed (examples/local-feed), then restores +
# builds the INDEPENDENT split Blogs module (examples/Modules/Blogs) against PACKAGES ONLY — proving a
# module compiles against the slim surface with zero source references.
#
# The slim SDK is SIX packages: three clean framework libs (Stomme.Shared / Stomme.SharedKernel /
# Stomme.Mediator), two compile-surface shims (Stomme.Server.Sdk / Stomme.Client.Sdk — the app
# assemblies in lib/net10.0 with a CURATED dependency surface instead of the fat 42-dependency closure),
# and the Stomme.Sdk.Slim meta-package. Measured on Blogs.Server: fat Stomme.Sdk restore closure was
# 180 packages / ~1.2 GB in the global packages folder; slim is what this script proves instead.
#
# EXTENDS the full-SDK flow (examples/pack-sdk.ps1) — it does not replace it: the fat feed remains the path for
# a module that genuinely needs provider-specific surface, and _sdk-smoke still proves it.
#
# Versioning: everything flows from examples/Directory.Build.props <Version> (mirroring the root). Pass
# -SdkVersion X.Y.Z to rev the SDK-only packages (the two shims + the meta, whose shim pins are $(Version))
# independently of an app release; the framework lib packages always pack at the props version.
#
# No dotnet clean / no obj deletion (BLAZOR106). Run from the repo root: pwsh examples/pack-sdk-slim.ps1
[CmdletBinding()]
param(
    [string]$SdkVersion
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$feed = Join-Path $PSScriptRoot 'local-feed'
New-Item -ItemType Directory -Force -Path $feed | Out-Null

$sdkVersionArgs = @()
if ($SdkVersion) { $sdkVersionArgs = @("-p:Version=$SdkVersion") }

# 0. Purge the global-packages-folder entries this run re-packs. NuGet resolves an id+version from the
#    GLOBAL CACHE before any source, so repacking the SAME version (the norm here — everything is
#    props-versioned) would otherwise leave every consumer restoring a STALE package. Measured: a pack-sdk
#    run on this machine restored a stomme.client 10.2.1 cached from an older tree.
# The examples' own packages folder (each local-feed nuget.config sets globalPackagesFolder), never the machine-wide
# cache, where a release version must mean the published bytes.
$gpf = Join-Path $PSScriptRoot '.nuget-local'
foreach ($id in @('stomme.shared', 'stomme.sharedkernel', 'stomme.mediator', 'stomme.security.abstractions',
                  'stomme.tenancy.domain', 'stomme.tenancy.abstractions', 'stomme.persistence.abstractions', 'stomme.eventbus',
                  'stomme.server.sdk', 'stomme.client.sdk', 'stomme.sdk.slim')) {
    $cached = Join-Path $gpf $id
    if (Test-Path $cached) { Remove-Item $cached -Recurse -Force }
}

# 1. Build the server in Release so the shims pack TODAY's assemblies (Stomme.Client builds transitively).
dotnet build (Join-Path $root 'src/Stomme.Server/Stomme.Server.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'build failed: Stomme.Server' }

# 2. The clean framework libs the slim surface exposes (packed at the props version — they are the app's).
# Plus the tenancy contracts a tenant-owned aggregate compiles against.
foreach ($p in @('Stomme.Shared', 'Stomme.SharedKernel', 'Stomme.Mediator', 'Stomme.Security.Abstractions',
                 'Stomme.Tenancy.Domain', 'Stomme.Tenancy.Abstractions', 'Stomme.Persistence.Abstractions', 'Stomme.EventBus')) {
    dotnet pack (Join-Path $root "src/$p/$p.csproj") -c Release --output $feed
    if ($LASTEXITCODE -ne 0) { throw "pack failed: $p" }
}

# 3. The two compile-surface shims, then the meta (its restore pulls the shims from the feed, so order
#    matters). --no-build is NOT passed: the shims compile an empty assembly that is never packed.
foreach ($p in @('Stomme.Sdk.Slim/Stomme.Server.Sdk/Stomme.Server.Sdk.csproj',
                 'Stomme.Sdk.Slim/Stomme.Client.Sdk/Stomme.Client.Sdk.csproj',
                 'Stomme.Sdk.Slim/Stomme.Sdk.Slim/Stomme.Sdk.Slim.csproj')) {
    dotnet pack (Join-Path $PSScriptRoot $p) -c Release --output $feed @sdkVersionArgs
    if ($LASTEXITCODE -ne 0) { throw "pack failed: $p" }
}

Write-Host "`nFeed contents (slim SDK packages):" -ForegroundColor Cyan
Get-ChildItem $feed -Filter *.nupkg |
    Where-Object { $_.Name -match '^Stomme\.(Shared|SharedKernel|Mediator|Security\.Abstractions|Tenancy\.Domain|Tenancy\.Abstractions|Persistence\.Abstractions|EventBus|Server\.Sdk|Client\.Sdk|Sdk\.Slim)\.' } |
    Select-Object -ExpandProperty Name | Sort-Object

# 4. The proof: the REAL external module — the split Blogs module — restores + builds
#    against the slim SDK, packages only. Its three csprojs reference Stomme.Sdk.Slim.
$blogs = Join-Path $PSScriptRoot 'Modules/Blogs'
foreach ($p in @('NovaGloben.Module.Blogs.Shared/NovaGloben.Module.Blogs.Shared.csproj',
                 'NovaGloben.Module.Blogs.Client/NovaGloben.Module.Blogs.Client.csproj',
                 'NovaGloben.Module.Blogs.Server/NovaGloben.Module.Blogs.Server.csproj')) {
    $proj = Join-Path $blogs $p
    dotnet restore $proj
    if ($LASTEXITCODE -ne 0) { throw "Blogs restore failed: $p" }
    dotnet build $proj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Blogs build failed: $p" }
}

Write-Host "`nBL-44 PROOF: the split Blogs module built against the SLIM SDK, packages only, 0 errors." -ForegroundColor Green
