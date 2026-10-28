# Nova Globen Stomme

**The frame your application stands on.** *Stomme* is Swedish for the frame of a building: the part you do not see once it
is finished, and the part everything else rests on.

Stomme is a multi-tenant application framework and CMS for **.NET 10 and Blazor**. It gives every application the things
every application needs, finished and tested, so the people building it - or the AI agents building it with them - can
spend their time on the product:

- **Sites, tenants and sign-in** - many sites on one installation, each with its own users and roles; its own OpenID
  Connect provider; external sign-in (OpenID Connect, SAML, Windows); two-factor sign-in.
- **Pages, modules and themes** - a composable UI that runs as static pages, Blazor Server, WebAssembly or a desktop app;
  your own modules and themes plug in by convention.
- **Your data, on your database** - SQL Server, PostgreSQL, SQLite or RavenDB, chosen at install; a module's data is
  storage-blind and tenant-owned by default.
- **Work in the background** - jobs, notifications by e-mail and in-app, files and folders, search, backups and restore,
  data export and erasure.
- **AI where it helps** - an assistant, an MCP server for your agents, AI steps in pipelines, with metering and quotas.
- **Pipelines** - a visual editor for automations, triggers and a catalogue of connectors, and an importer for n8n
  workflows.

This repository is a snapshot of Stomme's source for each release, published without history.

## Start an application

New applications are made from the app template, which ships an `AGENTS.md`, a `docs/` folder and step-by-step skills
for developers and AI agents:

```
dotnet new install Stomme.Application.Template
dotnet new stomme-app -o MyCompany.MyProject
```

The packages are published to nuget.org under the `Stomme.*` ids. The product page is <https://nova-globen.se/products/stomme>.

## Build this source

Requires the .NET 10 SDK.

```
dotnet build Stomme.slnx
dotnet run --project src/Stomme.Server
```

The first run opens a web installer (database and host account). `src/Stomme.Application` is the app template;
`examples/` holds an exemplar module (`examples/Modules/KnowledgeBase`) and two themes.

Some test suites need real databases (PostgreSQL, SQL Server, RavenDB) and fail rather than skip without one; point them
at your own servers with the `POSTGRES_TEST_*`, `SQLSERVER_TEST_*` and `RAVENDB_TEST_URL` environment variables. Tests
without the `ExternalDatabase` trait need nothing:

```
dotnet test Stomme.slnx --filter "Category!=ExternalDatabase"
```

## Licence

Stomme is **source-available**: free under the **PolyForm Noncommercial 1.0.0** or the **PolyForm Small Business 1.0.0**
licence, at your choice, and a **commercial licence** from Nova Globen AB for any other use. See [`LICENSE.md`](LICENSE.md)
for who needs which, and [`LICENSE`](LICENSE) for the binding texts. Parts of Stomme come from the Oqtane Framework and
Elsa Workflows (MIT); their notices are in [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md). The module and theme
templates and the example modules Blogs and KnowledgeBase are MIT, so code you start from them is yours to license; see
[`LICENSE.md`](LICENSE.md), "The parts that are MIT".

    Required Notice: Copyright Nova Globen AB (https://nova-globen.se)

Contributions are not accepted at this time. Report a security issue to Nova Globen AB through <https://nova-globen.se>.
