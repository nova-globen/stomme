#!/usr/bin/env pwsh
# Packs the framework a downstream app restores - Stomme.Server and its WHOLE project-reference closure - into -Output
# (BL-186). The closure is COMPUTED from the ProjectReference items, never listed: the hand-kept list this replaces
# (publish-nuget.yml, examples/pack-sdk.ps1) held 17 projects of 28 and missed Stomme.Infrastructure.Security.Cryptography
# and all eleven Stomme.Engine.* projects, so a 10.3.0 publish would have shipped a Stomme.Server whose dependencies
# did not exist (measured 2026-09-30: NU1101 on restore). One `dotnet pack` over a temporary solution of the closure packs
# them together; Stomme.Client is a Blazor WebAssembly app, packable only with IsPackable=true, which is passed to all.
#
#   pwsh scripts/pack-framework.ps1 -Output examples/local-feed
#
# Every package takes the version the repository declares (Directory.Build.props). No dotnet clean (BLAZOR106).
param(
    [Parameter(Mandatory)][string]$Output,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$server = [IO.Path]::GetFullPath((Join-Path $root 'src/Stomme.Server/Stomme.Server.csproj'))

$closure = [System.Collections.Generic.List[string]]::new()
$seen = @{}
$queue = [System.Collections.Generic.Queue[string]]::new()
$queue.Enqueue($server)
while ($queue.Count -gt 0) {
    $project = $queue.Dequeue()
    if ($seen.ContainsKey($project)) { continue }
    $seen[$project] = $true
    $closure.Add($project)
    [xml]$xml = Get-Content -LiteralPath $project
    foreach ($reference in @($xml.Project.ItemGroup.ProjectReference | Where-Object { $_ -and $_.Include })) {
        $queue.Enqueue([IO.Path]::GetFullPath((Join-Path (Split-Path $project) $reference.Include)))
    }
}

$work = Join-Path ([IO.Path]::GetTempPath()) ("stomme-pack-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
try {
    & dotnet new sln --name framework --output $work | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'could not create the packing solution' }
    $solution = Get-ChildItem $work -Filter 'framework.sln*' | Select-Object -First 1
    & dotnet sln $solution.FullName add @($closure) | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'could not add the closure to the packing solution' }

    New-Item -ItemType Directory -Force $Output | Out-Null
    & dotnet pack $solution.FullName -c $Configuration --output $Output -p:IsPackable=true
    if ($LASTEXITCODE -ne 0) { throw 'pack failed' }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

"Packed Stomme.Server and its closure ($($closure.Count) projects) into $Output"
$closure | ForEach-Object { '  ' + [IO.Path]::GetFileNameWithoutExtension($_) }
