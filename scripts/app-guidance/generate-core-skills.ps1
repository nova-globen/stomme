# Writes the app template's six CORE skills from their templates and the canonical Notes module (BL-239). Each template in
# templates/ is a SKILL.md whose code blocks are placeholders, {{FILE:<path under canonical/>}} or {{FILE:<path>|<indent>}};
# each placeholder becomes that file of the canonical module, indented. The canonical module is real code that was built,
# run and driven in an app made from the template - generating the skills from it is what keeps their samples true.
#
#   pwsh scripts/app-guidance/generate-core-skills.ps1          # write the six SKILL.md files
#   pwsh scripts/app-guidance/generate-core-skills.ps1 -Check   # exit 1 when a SKILL.md differs from what would be written
#
# Edit a core skill's TEXT in its template, its CODE in canonical/ (and drive the change in an app first - the
# stomme-app-guidance skill says how), then run this. Never edit those six SKILL.md files by hand: AppTemplateAgentGuidanceTests
# fails when one has drifted from its template.
param([switch]$Check)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$canon = Join-Path $here 'canonical'
$skills = Join-Path (Split-Path (Split-Path $here)) 'src\Stomme.Application\.claude\skills'
$drift = @()
foreach ($template in Get-ChildItem (Join-Path $here 'templates') -Filter '*.md') {
    $text = [IO.File]::ReadAllText($template.FullName).Replace("`r`n", "`n")
    $text = [regex]::Replace($text, '\{\{FILE:([^}|]+)(?:\|(\d+))?\}\}', {
        param($m)
        $path = Join-Path $canon ($m.Groups[1].Value -replace '/', '\')
        if (-not (Test-Path -LiteralPath $path)) { throw "$($template.Name) names $($m.Groups[1].Value), which canonical/ does not hold" }
        $pad = ' ' * $(if ($m.Groups[2].Success) { [int]$m.Groups[2].Value } else { 0 })
        $body = [IO.File]::ReadAllText($path).Replace("`r`n", "`n").TrimEnd("`n")
        (($body -split "`n") | ForEach-Object { if ($_.Length -gt 0) { $pad + $_ } else { '' } }) -join "`n"
    })
    $target = Join-Path $skills ($template.BaseName + '\SKILL.md')
    if (-not (Test-Path -LiteralPath $target)) { throw "no skill folder for template $($template.Name): $target" }
    $current = [IO.File]::ReadAllText($target).Replace("`r`n", "`n")
    if ($Check) {
        if ($current -ne $text) { $drift += $template.BaseName }
    }
    elseif ($current -ne $text) {
        [IO.File]::WriteAllText($target, $text, (New-Object Text.UTF8Encoding($false)))
        "wrote $($template.BaseName)"
    }
}
if ($Check) {
    if ($drift.Count -gt 0) { "drifted from their templates: $($drift -join ', ')"; exit 1 }
    'the six core skills match their templates'
}
