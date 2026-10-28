---
name: stomme-upgrade-stomme
description: Use when asked to upgrade, update or bump Nova Globen Stomme (the Stomme.Server, Stomme.Client and Stomme.Shared packages) to a newer version, to take a newer app template's changes, or to refresh the app's Stomme docs and stomme-* skills.
---

# Upgrade Stomme

You will have moved the app to a newer Stomme version: the three `Stomme.*` packages raised together, the
framework's template files merged, this app's `docs/` and `stomme-*` skills replaced by the new version's, the
database upgraded at the first start, and the app driven to prove it. How install and upgrade work:
[Install, run and upgrade](../../../docs/operations/install-run-upgrade.md).

## Steps

1. **Find the versions.** The current one is the `Stomme.Server` `PackageVersion` in `Directory.Packages.props`.
   List what the feed has (the .NET 8.0.2xx SDK or later has `dotnet package search`):
   ```
   dotnet package search Stomme.Server --exact-match --source https://api.nuget.org/v3/index.json
   ```
   It lists the versions oldest first (`--format json` for a machine-readable list). The template package,
   `Stomme.Application.Template`, is published at the same versions. Pick the target version. Read its release notes
   if the owner keeps them; note any switch or setting they mention.
2. **Back up.** Commit or stash the app's work so the upgrade is one reviewable change. Back up the installation you
   will start on the new version: the database(s), `Server/Data` (override file, SQLite files, backups) and the
   content folders. There is no downgrade command; going back is restoring this backup and the old versions.
3. **Scaffold the new template beside the app.** Install the template at the target version, then generate a scratch
   app with this app's exact name and ports (the name and the two ports are written into file names, namespaces,
   `launchSettings.json` and the docs), outside this repository:
   ```
   dotnet new install Stomme.Application.Template::<version>
   dotnet new stomme-app -n <AppName> -o <scratch folder> --HttpPort <http port> --HttpsPort <https port>
   ```
   `<AppName>` is the part before `.Server.csproj` in `Server/<AppName>.Server.csproj`; the ports are the two in
   `Server/Properties/launchSettings.json` (`http` profile, and the `https` one). Without `--HttpPort`/`--HttpsPort` the
   template picks random ports and the new docs name ports this app does not use. An older installed version of the
   template is replaced by itself ("is already installed, it will be replaced"). `dotnet new uninstall`, with no
   arguments, lists the installed template packages with their versions.
4. **Raise the packages.** In the app's `Directory.Packages.props`, set every `Stomme.*` `PackageVersion`
   (`Stomme.Server`, `Stomme.Client`, `Stomme.Shared`, and `Stomme.Sdk.Slim` when it is listed) to the target
   version, and set every other `PackageVersion` the scratch app's `Directory.Packages.props` lists to its value there.
   Keep the entries the app added for its own packages, unless a restore says they conflict.
5. **Merge the template files.** Diff each against the scratch app and take the framework's changes, keeping the app's
   own additions:
   - `Server/Program.cs`, `Client/Program.cs`, `Client/_Imports.razor`, the `AssemblyInfo.cs` files;
   - `Server/Stomme.Application.Server.csproj`, `Client/Stomme.Application.Client.csproj`,
     `Shared/Stomme.Application.Shared.csproj` (as named in this app);
   - `Server/appsettings.json` (the framework's defaults, including `AvailableDatabases` and new switches' shipped
     values), `Server/features.json`, `Server/Properties/launchSettings.json` (keep this app's ports), `NuGet.config`,
     `.gitignore`, `.editorconfig` (an app from an older template may not have one: copy it);
   - `Server/wwwroot/Modules/Templates` and `Server/wwwroot/Themes/Templates`: replace with the scratch app's.
   A diff tool over the two folders (`git diff --no-index <app folder> <scratch folder>`, excluding `bin`, `obj`,
   `Data`, `Content`) shows them all. A file that differs only in line endings has no framework change; take it or
   leave it.
6. **Replace the Stomme guidance.** Delete the app's `docs/` files that came from the template and every
   `.claude/skills/stomme-*` folder, and copy in the scratch app's `docs/` and `.claude/skills/stomme-*`. Do not touch
   skills whose names do not start with `stomme-`, or documents the app added itself. If the app keeps its own pages
   inside `docs/`, copy the template's files over them one by one instead of deleting the folder. Take the template's
   `AGENTS.md` and `CLAUDE.md` changes too, keeping the app's own sections. An app made from an older template may have
   no `docs/`, no `.claude/skills/` and no `AGENTS.md` or `CLAUDE.md` at all: copy them in whole.
7. **Build.**
   ```
   dotnet build
   ```
   Fix compile errors in the app's own code against the new API (the new docs describe it). Do not pin a framework
   package back to the old version to make an error go away.
8. **Start on a copy of real data.** Start the app (from `Server`) against the backed-up installation or a copy of it.
   The first start on the new version migrates the master and tenant databases and every module's schema, and adds any
   new admin pages. Read the log until the app listens.
9. **Drive it.** Sign in as the host, open the home page and the admin pages, and use every feature of the app's own
   modules: list, create, edit, delete. Check the browser console. Check `/admin/system` for the new version.
10. Delete the scratch app. Commit the upgrade as one change and say which version it moved from and to.

## Verify

- `dotnet build` succeeds with no `NU1605` (package downgrade) or `NU1608` warnings about `Stomme.*`.
- The first start logs no migration error and does not stop with "The installation did not complete".
- **System Info** (`/admin/system`) shows the target framework version as **Framework Version**, and
  `InstallationVersion` in `Server/Data/appsettings.override.json` is the target version.
- Users made before the upgrade still sign in.
- The app's own modules render and save on the upgraded data, and the browser console is clean.
- `git status` shows `docs/` and `.claude/skills/stomme-*` changed, and no app-specific skill or document removed.
- `grep` the new docs for the old version's names you relied on (a type or a setting key your code uses): each still
  exists, or your code has moved to its replacement.

## Common mistakes

- **Restore fails with a downgrade error (`NU1605`) naming a Microsoft or third-party package.** Cause: the app pins
  that package lower than the new framework needs. Fix: take its version from the scratch app's
  `Directory.Packages.props`.
- **Restore cannot find the new `Stomme.*` version.** Cause: a typo, or a version not yet published. Fix: check the version
  with `dotnet package search` against nuget.org.
- **The scratch app's docs mention another app name.** Cause: it was scaffolded without `-n <AppName>`, so the folder
  name was used. Fix: scaffold again with the exact name.
- **A database provider vanished from the installer, or a new switch is in an unexpected state.** Cause: the app kept
  its old `Server/appsettings.json`. Fix: merge the new template's file.
- **The app's own skill disappeared.** Cause: the whole `.claude/skills` folder was replaced. Fix: restore it from
  git and replace only the `stomme-*` folders.
- **The admin page System Update was used.** It upgrades a framework installed from a zip package and needs an updater
  program an app on the NuGet packages does not have; it does nothing useful here. Upgrade by package version.
- **The upgraded app was only built, not started.** The schema upgrade runs at the first start; a build proves nothing
  about it. Start it on real data and drive it.
