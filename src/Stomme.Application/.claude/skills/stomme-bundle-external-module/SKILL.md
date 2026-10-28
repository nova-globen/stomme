---
name: stomme-bundle-external-module
description: Use when the app must ship or install a Nova Globen Stomme module that lives in its own NuGet package - "bundle a module package", "add a module by PackageReference", "install a module .nupkg", "make this module a separate package", "Create Module", "turn a bundled module off with module.<id>.enabled".
---

# Bundle or install a module package

You will have a module that is NOT part of this app's source - a package built elsewhere, or one you generate with the
host's **Create Module** - running in the app, either **bundled at build time** by a `PackageReference` (it ships inside every
build and deployment of the app) or **installed at runtime** from the `Packages` folder. Read
[Module anatomy](../../../docs/modules/anatomy.md) (discovery, the three assemblies, in the app or as a package) and
[Settings and templates](../../../docs/modules/settings-and-templates.md) (`module.<id>.enabled`) first.

A module package carries three assemblies whose names end in `.Stomme` - `{Id}.Client.Stomme`, `{Id}.Server.Stomme`,
`{Id}.Shared.Stomme` - under `lib/net10.0/`, and no framework assemblies. Its nuspec declares the package types `Dependency`
and `Stomme.Framework` (with the minimum framework version).

## Steps

### A. Get or make the package

- **An existing package** (for example from nuget.org, or a folder): note its id and version. The app's `NuGet.config`
  restores from nuget.org; a package from anywhere else needs a source added for it.
- **A new one:** run the app, sign in as the host, open **Admin > Module Management** and choose **Create Module**. Give an
  owner name (no spaces or punctuation, and not "stomme"), a module name, a description, the `External` template
  and the framework reference (the Stomme version the module builds against; keep it at or below the app's). The host
  shows the location it writes to. It writes a solution named `{Owner}.Module.{Name}` into the folder that CONTAINS the app's
  folder (next to it, not inside it). It has `Client`, `Server`, `Shared` and `Package` projects, its own `nuget.config` and
  `Directory.*` files, and references Stomme only through the `Stomme.Sdk.Slim` package. Build and pack it from its
  folder with `pwsh pack.ps1`; the package lands in its `Package` folder.
- **By hand:** three projects - `{Id}.Client` (`Microsoft.NET.Sdk.Razor`, referencing `Stomme.Client`; `ModuleInfo` and
  the components), `{Id}.Server` (`Microsoft.NET.Sdk` with `<FrameworkReference Include="Microsoft.AspNetCore.App" />`,
  referencing `Stomme.Server`; controllers and startup) and `{Id}.Shared` (referencing `Stomme.Shared`), each with
  `<AssemblyName>{Id}.{Client|Server|Shared}.Stomme</AssemblyName>` and the same Stomme version as the app. Build them
  with `dotnet build -c Release`, then pack the three DLLs with a nuspec - a `Package/{Id}.Package.csproj` holding
  `<IncludeBuildOutput>false</IncludeBuildOutput>` and `<NuspecFile>{Id}.nuspec</NuspecFile>`, packed with
  `dotnet pack Package/{Id}.Package.csproj -o <your folder feed>`:

  ```xml
  <?xml version="1.0" encoding="utf-8"?>
  <package xmlns="http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd">
    <metadata>
      <id>Acme.Module.FieldNotes</id>
      <version>1.0.0</version>
      <authors>Acme</authors>
      <description>Field notes, a module bundled into the app.</description>
      <packageTypes>
        <packageType name="Dependency" />
        <packageType name="Stomme.Framework" version="10.72.0" />
      </packageTypes>
    </metadata>
    <files>
      <file src="..\Client\bin\Release\net10.0\Acme.Module.FieldNotes.Client.Stomme.dll" target="lib\net10.0" />
      <file src="..\Server\bin\Release\net10.0\Acme.Module.FieldNotes.Server.Stomme.dll" target="lib\net10.0" />
      <file src="..\Shared\bin\Release\net10.0\Acme.Module.FieldNotes.Shared.Stomme.dll" target="lib\net10.0" />
    </files>
  </package>
  ```

  Set the `Stomme.Framework` version to the Stomme version the module was built against.

### B1. Bundle it at build time (the default for a module the app always ships)

1. Make the package reachable to restore. For a local package, add a folder source to the app's `NuGet.config` and map the
   id to it, for example:

   ```xml
   <add key="local-modules" value="../packages" />
   ```

   under `<packageSources>` (a relative path is relative to `NuGet.config`), and under `<packageSourceMapping>`:

   ```xml
   <packageSource key="local-modules">
     <package pattern="Acme.Module.FieldNotes" />
   </packageSource>
   ```

2. Pin the version centrally in `Directory.Packages.props` (the app manages versions centrally):

   ```xml
   <PackageVersion Include="Acme.Module.FieldNotes" Version="1.0.0" />
   ```

3. Reference it, without a version, in `Server/Stomme.Application.Server.csproj` - the Server project only:

   ```xml
   <ItemGroup>
     <PackageReference Include="Acme.Module.FieldNotes" />
   </ItemGroup>
   ```

   Its three assemblies are copied to the Server's output and publish folders, where the host's startup scan finds them like
   the app's own. Do not reference it from `Client` or `Shared`: on a WebAssembly site the host streams the module's `Client`
   and `Shared` assemblies to the browser by itself.

4. Decide whether its page templates are applied. In `Server/features.json`, under `FeatureManagement`, the key is
   `module.<id>.enabled` with `<id>` the module's display NAME (its `ModuleDefinition.Name`, not the package id) lower-cased,
   spaces removed:

   ```json
   {
     "FeatureManagement": {
       "module.fieldnotes.enabled": true
     }
   }
   ```

   `false` keeps the module installed but places it on no page and skips its installer step; `true` or no key applies its
   templates. The template's `features.json` ships an example key (`module.blogs.enabled`) for a module the app may not have;
   remove it or replace it with your own.

### B2. Install it at runtime instead

1. Copy the `.nupkg` into the `Packages` folder of the running app's content root (`Server/Packages` when you run from the
   `Server` folder; in a deployment, the content root of the published app). The app's `.gitignore` already ignores
   `Server/Packages`.
2. Restart the app. At startup the host unpacks every package there into its bin folder - `lib/net*/` to bin, `wwwroot/` to the
   web root, `staticwebassets/` to `wwwroot/_content/{package id}` - writes a manifest `{package}.log` in `Packages`, and
   deletes the `.nupkg`. A package whose `Stomme.Framework` version is newer than the running framework is skipped.
3. A package installed this way lives in the bin folder only: a rebuild or a new deployment of the app does not carry it.
   Use B1 for anything the app must always have.

## Verify

1. `dotnet build` (B1) and `cd Server`, `dotnet run`.
2. The Server output folder (`Server/bin/Debug/net10.0`) holds the module's three `*.Stomme.dll` files.
3. **Admin > Module Management** lists the module by its name.
4. With the feature key on (or absent) and a module that declares page templates: after a restart its page exists on the
   site and renders the module; click its buttons in an interactive render mode and confirm they act. With the key `false`
   on a fresh installation: the page's address answers 404 (no page was created), while Module Management still lists the
   module and it can be added by hand from the control panel (**Add Module To Page**).
5. Its API answers an anonymous caller 401 or 403, not 404 (a 404 means its controller was not found).
6. On a WebAssembly site, the page renders the module and the browser console shows no assembly load error.

## Common mistakes

- **The module does not appear at all.** An assembly name without `Stomme` in it, a package that puts the DLLs somewhere
  other than `lib/net10.0/`, or the reference was added to `Client`/`Shared` instead of `Server`.
- **After adding the reference, the app's own code fails with CS0118 "'Module' is a namespace but is used like a type".**
  The package's namespaces start with the app's first namespace segment (`Acme.Module.FieldNotes` in an app named
  `Acme.Portal`): inside `Acme.Portal...` the compiler finds the namespace `Acme.Module` before the type
  `Stomme.Models.Module` that a `using` brings in. Write `Stomme.Models.Module` in full where the app names that type
  (a server manager's `ExportModuleAsync(Stomme.Models.Module module)`, for example), or give the module an owner other
  than the app's first segment.
- **Restore fails with NU1101 / "unable to find package".** The id is not mapped to a source in `NuGet.config` (package source
  mapping sends every unmapped id to nuget.org).
- **NU1008 or NU1010.** With central package management the version goes in `Directory.Packages.props` (`PackageVersion`), and
  the `PackageReference` carries none.
- **It builds but fails at runtime with a missing method or type.** The module was built against a newer Stomme than the
  app. The build-time path does not check the package's `Stomme.Framework` version; keep the module's Stomme version at
  or below the app's.
- **`module.<id>.enabled: false` has no effect.** The id was taken from the package id instead of the module's display name, or
  an administrator renamed the module in Module Management - the stored name decides the key.
- **Switched off, but the API still answers.** The feature key only governs page templates and the installer step; it is not a
  security switch.
- **A runtime-installed module disappears after a redeploy.** It was never part of the build; bundle it (B1).
- **Two modules with the same `Name`.** They share one feature key and confuse administrators; keep display names unique.
