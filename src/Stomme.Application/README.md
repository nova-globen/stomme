# Nova Globen Stomme Application Template

Scaffolds an app on the Stomme framework packages, with the native templating of the .NET CLI. The template and the
framework come from nuget.org:

```
dotnet new install Stomme.Application.Template
dotnet new stomme-app -o MyCompany.MyProject
cd MyCompany.MyProject
dotnet build
cd Server
dotnet run
```

Browse to the URL it prints. The first run opens the installer (database, host account); to install unattended, put
the `Database`, `ConnectionStrings` and `Installation` sections in `Server/Data/appsettings.override.json` first.

You need no copy of the Stomme source: the app uses Stomme as a package dependency.

- `Directory.Packages.props` pins the framework version the template was packed from. To upgrade, raise the three
  `Stomme.*` versions together.
- `NuGet.config` restores every package from nuget.org.
- `Server/Program.cs` runs the framework's own host (`Stomme.Server.Program`): its services, its middleware, the
  install or upgrade of the database, and a staged backup restore. Your own services belong in a module's startup
  (`IServerStartup`), which the host discovers in this app's assemblies.
- `Client` is the app's WebAssembly client: your modules' and themes' components, discovered the way a module's client
  assembly is, and the runtime a WebAssembly site's browser downloads. Its `Program.cs` hands the start to Stomme's
  client (`Stomme.Client.Program.Main`). An app made from a template before this (10.4.0 to 10.5.2) had a class library
  here and could not serve a WebAssembly site (`_framework/dotnet.js` answered 404); to fix one, give `Client` this
  project's `Sdk="Microsoft.NET.Sdk.BlazorWebAssembly"`, its properties, its `Microsoft.AspNetCore.Components.WebAssembly`
  reference and its `Program.cs`, on Stomme packages that carry no boot script of their own (after 10.5.2). Static and
  Server sites never needed it.
- `Shared` holds the types both sides use.
- `Server/appsettings.json` holds the framework's defaults and is committed: a clean checkout boots from it. What the
  installation writes - the connection string with its password, the installation id, switches set from the admin
  pages - goes to `appsettings.override.json` in the directory `STOMME_APPSETTINGS_PATH` names, and to
  `Server/Data` when nothing names one; `Server/Program.cs` creates the file when it is missing, and the app's
  `.gitignore` keeps `Server/Data` (the SQLite databases too) out of source control. In a container, point
  `STOMME_APPSETTINGS_PATH` at a mounted directory so the installation survives a new image. The override file is
  read last, after environment variables. Keep secrets out of any file you commit. (An app made before this, 10.4.0 to
  10.5.2, ignored `appsettings.json` because the installer wrote its secrets there; take this `Program.cs` and
  `.gitignore`, then move the installed `Database`, `ConnectionStrings` and `InstallationId` values into the
  override file.)

Add your own modules and themes in these three projects, following the Stomme folder and namespace conventions. Start
with [AGENTS.md](AGENTS.md): it says what Stomme already provides and how to build on it; how each piece works is in
[docs/](docs/README.md), and the skills under `.claude/skills/` walk through building each kind of piece - a module first
(`stomme-create-module`).

## What your app gets, and the switch for each

Everything below arrives with the `Stomme.*` packages. The only framework files in your source are the templates Create
Module and Create Theme generate from, under `Server/wwwroot`. A stock app is conservative:
anything that reaches out, runs code, keeps extra data or costs money is **off** until you turn it on. Set a switch in
`Server/appsettings.json` (or `appsettings.{Environment}.json`, environment variables, or the installation's override file,
which wins over the others); `true`/`false` unless noted.

**Always there**

- Multi-tenancy: one installation, many tenants (a database each, or shared) and many sites per tenant, resolved by host name.
- Your choice of database at install: SQL Server, SQLite, PostgreSQL or RavenDB. Modules are storage-blind and work on all four.
- Users, roles and per-site permissions on ASP.NET Identity; lockout; external OpenID Connect / OAuth2 login per site.
- Modules and themes discovered by convention, bundled at build time or installed as packages; **Create Module** and
  **Create Theme** for the host, generating from the templates in `Server/wwwroot/{Modules,Themes}/Templates`.
- Pages, content, files and folders with per-user privacy, search, localization, notifications (email and in-app),
  a site landing page, connections to external systems through a catalogue of connectors (`GET api/Connections/connectors`
  lists them) with a per-site egress allowlist the Host sets, datasets with classification, dashboards, formats, and the
  right to erasure.
- Feature flags (the `FeatureManagement` section, `Server/features.json`), including `module.<id>.enabled` for a bundled
  module.

**Switched**

Most capabilities are behind a switch and off in a stock app: the AI assistant, the MCP server, pipelines (and their webhooks,
scripts and processes), backup, data export, a user's data package, collaboration, SignalR push, the message bus, your own
OpenID Connect provider, SAML2 and Windows login, help topics, connection health probes, cloud cost alerts, temporary-file
cleanup and demo content. Health probing is on. Every switch, with its default, the settings that go with it and its
limits, is in [docs/architecture/configuration.md](docs/architecture/configuration.md); a module's MCP surface also has
its own feature flag (for example `datasets.mcp`), off by default.

## Bundling an external module by build-time PackageReference

Instead of installing a module `.nupkg` through the admin marketplace UI, an app can **bundle** it at build time:

1. Add a `<PackageReference>` to the module package in `Server/Stomme.Application.Server.csproj` (a commented
   example is already there). The module's `.Stomme`-named assembly bakes into the publish output and is
   discovered at startup by the existing convention scan — no upload, no restart-to-unpack, no framework edit.
2. Decide whether each bundled module is enabled/seeded by default in `Server/features.json`, under the
   `FeatureManagement` section, with the key `module.<id>.enabled` (`<id>` = the module's display name
   lower-cased, whitespace removed). For example `"module.blogs.enabled": false` ships the Blogs assembly but
   does **not** seed it onto a page, so it does not render; `true` or an absent key seeds it as normal.

Precedence is `config → DB/admin toggle → catalog default`, so the config knob overrides the admin toggle,
and an app that ships no feature keys behaves exactly as an un-bundled one. The same section is
Microsoft.FeatureManagement-shaped, so Azure App Configuration can drive it later with no code change.

*Known Issues*

- do not use the term "Stomme" or "Module" in your output name or else you will experience namespace conflicts


*Licence*

Your app's own code is yours. Stomme, the framework it runs on, is source-available: free under PolyForm Noncommercial
1.0.0 or PolyForm Small Business 1.0.0 (fewer than 100 people and under USD 1M revenue), and a commercial licence from Nova
Globen AB for any other commercial use. The `LICENSE` file inside each `Stomme.*` package carries both texts; pass on
its required notice, `Required Notice: Copyright Nova Globen AB (https://nova-globen.se)`, with any copy of Stomme you
distribute.
