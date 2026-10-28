# An INDEPENDENT fact-check of the app template's guidance (BL-239): opencode's read-only plan agent reads one shipped doc or
# skill at a time against the source and reports only what is wrong or unsupported, with file:line evidence - a read not
# anchored on the writers'. One report per file in -Out; files already reported are skipped, so a re-run continues.
#
#   pwsh scripts/app-guidance/factcheck.ps1                                  # every doc and skill
#   pwsh scripts/app-guidance/factcheck.ps1 -Only 'docs\security\','stomme-oidc-provider'
#
# Its findings are LEADS, not facts: measured 2026-10-24, it found real errors (a wrong log placeholder, an overstated
# provider claim) and confident false ones (it read a git-ignored local appsettings.json the template never ships). Check
# each in the source before applying it. About 5-25 minutes per file; the model must be one `opencode models` lists and
# whose quota holds (the default free model's ran out).
param(
    [int]$Workers = 8,
    [string]$Model = 'mimo/mimo-v2.6-pro',
    [string[]]$Only = @(),
    [string]$Out = (Join-Path ([IO.Path]::GetTempPath()) 'stomme-app-guidance-factcheck')
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot)
New-Item -ItemType Directory -Force $Out | Out-Null
$app = Join-Path $repo 'src\Stomme.Application'
$files = @(Get-ChildItem (Join-Path $app 'docs'), (Join-Path $app '.claude\skills') -Recurse -File -Filter *.md |
    Where-Object Name -ne 'README.md' |
    Where-Object { $path = $_.FullName; $Only.Count -eq 0 -or ($Only | Where-Object { $path -like "*$_*" }) } |
    ForEach-Object { $_.FullName.Substring($repo.Length + 1).Replace('\', '/') })
$todo = @($files | Where-Object { -not (Test-Path (Join-Path $Out (($_ -replace '[/\\.]', '_') + '.txt'))) })
"files $($files.Count), to check $($todo.Count), reports in $Out"
$todo | ForEach-Object -ThrottleLimit $Workers -Parallel {
    $file = $_
    $report = Join-Path $using:Out (($file -replace '[/\\.]', '_') + '.txt')
    $prompt = @"
Fact-check one document against the source code in this repository. Do NOT modify any file - read only.
The document: $file
It is guidance for an AI agent building an app on Stomme's NuGet packages (it has no Stomme source). For each factual
claim about Stomme - type names, namespaces, members and signatures, config keys and their defaults, routes, permission
keys, UI locations, behaviour, and whether a type is public - check it against the source under src/. For each C# or
Razor sample, check that the types, members, namespaces and signatures it uses exist as written and are public. An app's
defaults are git's src/Stomme.Server/appsettings.json, never a file git ignores.
Report ONLY what is wrong or unsupported, each as: the claim quoted briefly; what the source actually says; file:line
evidence. Ignore the app's own sample types (namespaces starting Stomme.Application) except where they call Stomme.
If everything checks out, say exactly: ALL CLAIMS CHECKED OUT. Be concise.
"@
    Set-Location $using:repo
    $text = & opencode run --agent plan -m $using:Model $prompt 2>&1 | Out-String
    $kept = ($text -split "`n" | Where-Object { $_ -notmatch '^\s*(→|✱|>|\$) ' }) -join "`n"
    if ($kept.Trim().Length -gt 0) { Set-Content -LiteralPath $report -Value $kept }
    "done $file"
}
