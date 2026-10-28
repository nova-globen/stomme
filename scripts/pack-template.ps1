#!/usr/bin/env pwsh
# Packs the app template (src/Stomme.Application) as Stomme.Application.Template at the repository's version (BL-186):
#
#   dotnet new install Stomme.Application.Template
#   dotnet new stomme-app -o Acme.Portal   (stomme-app still works: the template keeps it as an alias)
#
# A scaffolded app restores on its own: the package carries a Directory.Packages.props written HERE - Stomme.* at this
# repository's version, and every other package the template references at the version the repository pins - and the
# template's NuGet.config restores from nuget.org. Without them the app answered NU1015 (no versions;
# measured 2026-09-30), because in the repository the template borrowed the root's central versions.
#
#   pwsh scripts/pack-template.ps1 -Output examples/local-feed
param(
    [Parameter(Mandatory)][string]$Output
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$template = Join-Path $root 'src/Stomme.Application'
[xml]$buildProps = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props')
$version = @($buildProps.Project.PropertyGroup.Version | Where-Object { $_ })[0]
if (-not $version) { throw 'Directory.Build.props declares no <Version>' }

[xml]$central = Get-Content -LiteralPath (Join-Path $root 'Directory.Packages.props')
$pinned = @{}
foreach ($entry in $central.Project.ItemGroup.PackageVersion) {
    if ($entry.Include) { $pinned[$entry.Include] = $entry.Version }
}

$stage = Join-Path ([IO.Path]::GetTempPath()) ("stomme-template-" + [Guid]::NewGuid().ToString('N'))
$content = Join-Path $stage 'content'
New-Item -ItemType Directory -Force $content | Out-Null
try {
    # What a checkout holds, never what a build or a run left beside it: the tracked files and the untracked ones git does
    # not ignore, less those deleted. The folder on a working machine carried a git-ignored Server/appsettings.json and
    # 34 module and theme template files (measured 2026-09-30) that a CI checkout does not have - staging the folder packed
    # them locally and the drive passed on a package the workflow would never produce.
    $files = & git -C $root ls-files --cached --others --exclude-standard -- 'src/Stomme.Application'
    if ($LASTEXITCODE -ne 0 -or -not $files) { throw 'git could not list the template''s files' }
    $prefix = 'src/Stomme.Application/'
    foreach ($file in $files | Sort-Object -Unique) {
        $path = Join-Path $root $file
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        $target = Join-Path $content $file.Substring($prefix.Length)
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        Copy-Item -LiteralPath $path -Destination $target -Force
    }

    # The app's appsettings.json: the host's defaults, as git holds them. An app commits it (BL-195): its Program sends what
    # the installer writes - the connection string, the installation id - to Data/appsettings.override.json, which the
    # app's .gitignore keeps out. This repository's root .gitignore keeps a local copy out of the template folder, so the
    # framework's file is the only source. A scaffold without it did not boot (measured 2026-09-30: FileNotFoundException
    # for appsettings.json in BuildAppAsync), which the local drive had hidden by packing an untracked copy.
    # The module and theme templates the host's Create Module and Create Theme generate from, into the app's own web root -
    # the place the app's .gitignore already keeps tracked (Server/wwwroot/{Modules,Themes}/Templates/External). The
    # Stomme.Server package does not ship them (its project keeps them out of every item group and copies them only on
    # its own publish), so a scaffolded app offered no template at all (BL-186 increment 5, measured 2026-10-01).
    $frameworkTemplates = & git -C $root ls-files -- 'src/Stomme.Server/wwwroot/Modules/Templates' 'src/Stomme.Server/wwwroot/Themes/Templates'
    if ($LASTEXITCODE -ne 0 -or -not $frameworkTemplates) { throw 'git lists no module or theme templates under src/Stomme.Server/wwwroot' }
    foreach ($file in $frameworkTemplates) {
        $target = Join-Path $content ('Server/' + $file.Substring('src/Stomme.Server/'.Length))
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        Copy-Item -LiteralPath (Join-Path $root $file) -Destination $target -Force
    }

    $appsettings = & git -C $root show ':src/Stomme.Server/appsettings.json'
    if ($LASTEXITCODE -ne 0 -or -not $appsettings) { throw 'git holds no src/Stomme.Server/appsettings.json' }
    $appsettings | Set-Content -LiteralPath (Join-Path $content 'Server/appsettings.json') -Encoding utf8

    # The app's central versions: exactly the packages its projects reference.
    $references = Get-ChildItem -LiteralPath $content -Recurse -Filter *.csproj | ForEach-Object {
        [xml]$project = Get-Content -LiteralPath $_.FullName
        $project.Project.ItemGroup.PackageReference | Where-Object { $_ -and $_.Include } | ForEach-Object { $_.Include }
    } | Sort-Object -Unique
    $versions = foreach ($id in $references) {
        $pin = if ($id -like 'Stomme.*') { $version } else { $pinned[$id] }
        if (-not $pin) { throw "the template references $id, which Directory.Packages.props does not pin" }
        "    <PackageVersion Include=""$id"" Version=""$pin"" />"
    }
    @(
        '<Project>'
        '  <!-- The versions this app builds against: Stomme.* at the framework version the template was packed from, and'
        '       the packages the framework was tested with. Upgrade Stomme by raising the three Stomme.* versions together. -->'
        '  <PropertyGroup>'
        '    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>'
        '  </PropertyGroup>'
        '  <ItemGroup>'
        $versions
        '  </ItemGroup>'
        '</Project>'
    ) | Set-Content -LiteralPath (Join-Path $content 'Directory.Packages.props') -Encoding utf8

    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $stage 'LICENSE') -Force
    Copy-Item -LiteralPath (Join-Path $template 'icon.png') -Destination (Join-Path $stage 'icon.png') -Force
    Copy-Item -LiteralPath (Join-Path $template 'README.md') -Destination (Join-Path $stage 'README.md') -Force

    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageType>Template</PackageType>
    <PackageId>Stomme.Application.Template</PackageId>
    <Version>$version</Version>
    <Title>Stomme Application Template</Title>
    <Authors>Nova Globen AB</Authors>
    <Company>Nova Globen AB</Company>
    <Description>Scaffolds an app on the Nova Globen Stomme $version framework packages (Stomme.*) - Server, Client and Shared projects - with its package versions and feed mapping included: dotnet new stomme-app -o MyCompany.MyProject</Description>
    <PackageTags>Stomme;Blazor;ASP.NET;Web;Multi-Tenant;Modular;template</PackageTags>
    <PackageLicenseFile>LICENSE</PackageLicenseFile>
    <PackageIcon>icon.png</PackageIcon>
    <PackageReadmeFile>README.md</PackageReadmeFile>
    <IncludeBuildOutput>false</IncludeBuildOutput>
    <IncludeContentInPack>true</IncludeContentInPack>
    <ContentTargetFolders>content</ContentTargetFolders>
    <NoDefaultExcludes>true</NoDefaultExcludes>
    <EnableDefaultItems>false</EnableDefaultItems>
    <NoWarn>`$(NoWarn);NU5128;NU5110;NU5111</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <Content Include="content/**/*" Exclude="content/icon.png" />
    <None Include="LICENSE;icon.png;README.md" Pack="true" PackagePath="" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $stage 'template.csproj') -Encoding utf8

    New-Item -ItemType Directory -Force $Output | Out-Null
    & dotnet pack (Join-Path $stage 'template.csproj') -c Release --output $Output
    if ($LASTEXITCODE -ne 0) { throw 'template pack failed' }
}
finally {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}

"Packed Stomme.Application.Template $version into $Output"
