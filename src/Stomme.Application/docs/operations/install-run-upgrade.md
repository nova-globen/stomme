# Install, run and upgrade

The app needs no database settings to start. On the first run it opens a web installer that creates the database, the
first tenant and site, and the host account. Every later start runs the same install-or-upgrade path, which brings the
database schema up to the Nova Globen Stomme version the app was built with. Upgrading Stomme means raising its package
versions and taking the matching template files, docs and skills.

## How it works

### Start-up and the install check

`Server/Program.cs` calls `Stomme.Server.Program.InstallAsync(app)` before serving. It calls
`Stomme.Infrastructure.IDatabaseManager.InstallAsync()`, which:

1. Reads `ConnectionStrings:DefaultConnection` and `Database:DefaultDBType` (see [Configuration](../architecture/configuration.md)).
2. When the connection string is empty, does nothing: the app starts and the first request shows the installer.
3. When it is set and the installation exists (the master database answers and holds a tenant), runs the upgrade path:
   it migrates the master database, every tenant database and every module's schema, and adds any admin page a newer
   Stomme version introduced (existing pages are never changed).
4. When it is set but nothing is installed yet, runs an **unattended install** if the `Installation` section carries
   what it needs (below). If it does not, the result carries an error message and the app **logs it and exits**.

A staged backup restore, if one is waiting, is applied right after (only when `Backup:Enabled`).

### The web installer

The installer is a four-step wizard: **Database**, **Account**, **Configure**, **Finish**.

- **Database** lists the entries of `AvailableDatabases` in `appsettings.json` in alphabetical order: Azure SQL,
  LocalDB, PostgreSQL, RavenDB, SQL Server, SQLite. The one preselected is not SQLite; pick the one you want. Each has
  its own form (SQLite asks only for a file name), or **Enter Connection String**. Default database names have the form
  `Stomme-yyyyMMddHHmm` (SQLite's file `Stomme-yyyyMMddHHmm.db`); Azure SQL's fields hold placeholders such as
  `{SQL Database Name}` to replace.
- **Account** is the host account: **Username** (empty; type `host` or your own), **Password** and **Confirm**,
  **Email**, **Full Name**, and the **Template** of the first site (**Default Site Template**, preselected, or **Empty
  Site Template**). The password must have at least six characters, with an upper-case letter, a lower-case letter, a
  digit and a non-alphanumeric character.
- **Configure** shows the module-wide settings that modules declare for installation time. A stock app shows the
  framework's own (a note about **Notifications**, and **HtmlText**'s **Default Content**); the defaults are fine.
- **Finish** shows the database and user name and installs on **Install Now**. Its **Register** box ("Please Register
  Me For Major Product Updates And Security Bulletins") is unticked by default; leave it unticked unless you mean to
  send the host's e-mail and name to the package registry.

The install creates the master database, the tenant `Master`, the site `Default Site` from the default site template
with the default theme, the host user in the **Host Users** role, and an **alias** equal to the host and port the
installer was opened at (for example `localhost:44358`). It writes the connection string, the database type and the
`InstallationId` to the override file. Then the browser is sent to the new site.

Module data (storage-blind aggregates) and feature flags live, on a relational install, in SQLite files in the data
directory (`<content root>/Data`) unless `ConnectionStrings:ModuleAggregates` and `ConnectionStrings:FeatureManagement`
name server databases. Keep that directory with the database.

## Using it from an app

### Run

```
dotnet build
cd Server
dotnet run
```

The `http` launch profile listens on `http://localhost:44358`; `dotnet run --launch-profile https` adds
`https://localhost:44359`. Both set `ASPNETCORE_ENVIRONMENT=Development`. Run from `Server`: the content root is the
current directory, and `appsettings.json` is looked for there.

Under `dotnet run` the app's files land in:

| Path | What |
|---|---|
| `Server/Data/appsettings.override.json` | What the installation and the admin pages wrote, secrets included. |
| `Server/Data/*.db` | SQLite databases: the master database when you chose SQLite, `Stomme.ModuleAggregates.db`, `Stomme.Features.db`. |
| `Server/Data/backups` | Local backups, when backup is on. |
| `Server/Content/Tenants` | Files in private folders. |
| `Server/wwwroot/Content/Tenants` | Files in public folders. |
| `Server/Content/Log` | The file log. |
| `Server/Packages` | Drop a module or theme `.nupkg` here; it is unpacked at the next start. |

All of them are in the app's `.gitignore`.

### Sign in and administer

Sign in at `/login` with the host account. Administration is under `/admin`. Host-only pages include
**System Info** (`/admin/system`), **Site Management** (`/admin/sites`), **Scheduled Jobs** (`/admin/jobs`) and
**Setting Management** (`/admin/settings`). **Feature Flags** is `/admin/features`.

### Unattended install

Put the database and the host account in configuration before the first start, for example in
`Server/Data/appsettings.override.json` (never in a committed file):

```json
{
  "Database": {
    "DefaultDBType": "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server"
  },
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=|DataDirectory|/MyApp.db;"
  },
  "Installation": {
    "DefaultAlias": "localhost:44358",
    "HostEmail": "host@example.com",
    "HostPassword": "Change-Me-1"
  }
}
```

- `ConnectionStrings:DefaultConnection`, `Installation:DefaultAlias`, `Installation:HostEmail` and
  `Installation:HostPassword` are required; with the connection string set and any of the others missing, the app logs
  one `fail` line naming the missing keys, "The installation did not complete: Error Installing Master Database For
  DefaultConnection: an automated installation also needs Installation:HostEmail, Installation:HostPassword (or
  HostPasswordFile).", and exits.
- **The password from a file** (a container secret): set `Installation:HostPasswordFile` to the file's path in place of
  `Installation:HostPassword` (`Installation__HostPasswordFile=/run/secrets/host-password`). The file's trailing line
  break is ignored. A missing or empty file, or both keys set, stops the install with a `fail` line naming the key and
  the path, never the password.
- **No browser wizard once the `Installation` section asks for an unattended install**: when any of
  `Installation:DefaultAlias`, `HostEmail`, `HostPassword` or `HostPasswordFile` is set and the install has not
  completed (the connection string is missing, say), the home page says the host is configured for an unattended
  installation and points at the log, and the install API refuses anyone but the Host. A stranger who reaches a public
  host first cannot claim it.
- The values may come from any configuration source, environment variables included
  (`ConnectionStrings__DefaultConnection`, `Installation__HostPassword`, ...). The install writes `InstallationId` to the
  override file, but **not** a connection string or database type it read from an environment variable: keep those
  variables set for every later start, or the app opens the installer again.
- `Database:DefaultDBType` is one of the `DBType` values in `AvailableDatabases`:
  `Stomme.Database.SqlServer.SqlServerDatabase, Stomme.Server` (also LocalDB and Azure SQL),
  `Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server`,
  `Stomme.Database.PostgreSQL.PostgreSQLDatabase, Stomme.Server`,
  `Stomme.Database.RavenDb.RavenDatabase, Stomme.Server`. Left empty it means SQL Server.
- **SQLite's busy timeout**: a command waits 30 seconds for a file another connection has locked, then fails with
  `SQLite Error 5: 'database is locked'`. Set another wait in the connection string, `Default Timeout=60;` (seconds);
  it applies to every SQLite store the host opens with that string (each file runs in WAL, so readers never block the
  writer).
- Connection string shapes the installer itself builds: SQLite `Data Source=|DataDirectory|/name.db;`;
  PostgreSQL `Server=host;Port=5432;Database=name;Integrated Security=true;` (with **Custom**
  security, `User ID=user;Password=secret;` in place of `Integrated Security=true;`);
  SQL Server `Data Source=server;Initial Catalog=name;Integrated Security=SSPI;Encrypt=false;TrustServerCertificate=false;`
  (with **Custom** security, `User ID=user;Password=secret;` in place of `Integrated Security=SSPI;`; Azure SQL: **Custom**
  security and `Encrypt=true;`);
  RavenDB `Url=http://host:port;Database=name;`. `|DataDirectory|` means `<content root>/Data`.
- `DefaultAlias` may list several aliases separated by commas; the first is the site's primary one. Use the host name
  (and port) the site will be reached at.
- Optional: `Installation:HostUsername` and `Installation:HostName` (both default `host`), `Installation:SiteTemplate`,
  `Installation:DefaultTheme`, `Installation:DefaultContainer` (each an assembly-qualified type name). The first site's
  render mode, runtime and prerendering come from the top-level `RenderMode`, `Runtime` and `Prerender` keys.
- The unattended path creates the database too, the same way the wizard does.

After the install, remove `Installation:HostPassword` from the file (or the secret `HostPasswordFile` names); the account
exists in the database.

### More sites from configuration: `Installation:Sites`

`Installation:Sites` lists the sites beyond the first. On every start of an installed host, each entry whose first alias
does not name a site yet is created, the way **Admin > Site Management > Add Site** creates one; an entry that exists
is left alone, so a restart never duplicates a site and an entry added later appears on the next start. Each entry:

| Key | Required | Meaning |
|---|---|---|
| `Name` | yes | The site's name. |
| `Aliases` | yes | Its host names, comma-separated; the first is its primary alias. A folder site: `example.com/sv`. |
| `Tenant` | with `ConnectionString` | The tenant. Absent: the installation's own (`Master`). |
| `ConnectionString` | no | Creates `Tenant` as a NEW tenant with this database of its own (a SQLite file in the data directory: see the example below). The host account is created in it, so the host password (`HostPassword` or `HostPasswordFile`) must be set on that start. |
| `DatabaseType` | no | The new tenant's database type; default `Database:DefaultDBType`. |
| `SiteTemplate`, `DefaultTheme`, `DefaultContainer`, `RenderMode`, `Runtime` | no | As for the first site. |

The whole list is checked before anything is written. A missing name or alias, an alias listed twice or already on the
installation, a tenant that does not exist (or a new one named like an existing one), an unknown database type, a
SQLite folder that cannot be written: the app logs `Installation:Sites is not valid, so no site was created:` with each
problem by its entry (`Installation:Sites:1: the alias 'shop.example.com' is also listed by Installation:Sites:0.`)
and does not serve. A connection string is never written to the log.

Every value is a plain string, so the list can be given entirely as environment variables, as a compose file would:

```yaml
environment:
  Installation__DefaultAlias: example.com
  Installation__HostEmail: host@example.com
  Installation__HostPasswordFile: /run/secrets/host-password
  Installation__Sites__0__Name: Svenska
  Installation__Sites__0__Aliases: example.com/sv
  Installation__Sites__1__Name: Shop
  Installation__Sites__1__Aliases: shop.example.com
  Installation__Sites__1__Tenant: Shop
  Installation__Sites__1__ConnectionString: Data Source=|DataDirectory|/shop.db;
```

### Start again from nothing (development, SQLite)

Stop the app, then delete `Server/Data` (the databases and the override file) and, if you uploaded files,
`Server/Content` and `Server/wwwroot/Content`. The next start opens the installer. For a server database, drop the
database as well; a connection string pointing at an existing installation goes straight to the upgrade path.

### Restart

**System Info > Restart Application** stops the process; it does not start it again. Under `dotnet run` the app
exits. In production a supervisor (systemd, a container restart policy, IIS) must start it again. See [Deploy](deploy.md).

### Upgrade Stomme

An app upgrades Stomme by package version, never through the **System Update** admin page (that page applies a
framework zip package with a separate updater program, which an app built on the NuGet packages does not have).

1. Back up the database(s), the data directory and the override file. There is no downgrade command; going back means
   restoring this backup and the previous package versions.
2. Install the template of the target version (it replaces an older installed version by itself) and scaffold a
   scratch app with the **same name and the same ports** as this one - the name and the two ports of
   `Server/Properties/launchSettings.json` are substituted into every file but `README.md`, the docs included:
   ```
   dotnet new install Stomme.Application.Template::<version>
   dotnet new stomme-app -n <AppName> -o <scratch folder outside this repository> --HttpPort <http port> --HttpsPort <https port>
   ```
3. In `Directory.Packages.props`, set every `Stomme.*` version to the new version together, and take the scratch
   app's versions of the other pinned packages.
4. Compare the scratch app's template files with this app's and take the framework's changes:
   `Server/Program.cs`, `Client/Program.cs`, the three `.csproj` files, `Server/appsettings.json` (the framework's
   defaults, including `AvailableDatabases`), `Server/features.json`, `NuGet.config`, `.gitignore`, `.editorconfig`,
   `Server/wwwroot/Modules/Templates`, `Server/wwwroot/Themes/Templates`. Keep your own additions.
5. Replace this app's `docs/` and `.claude/skills/stomme-*` folders with the scratch app's, and take the scratch app's
   `AGENTS.md` and `CLAUDE.md` changes. They describe the Stomme version the app is on. Do not touch skills or
   documents the app added itself.
6. Build, start, and check the boot log: the upgrade runs at the first start on the new version and sets
   `InstallationVersion` in the override file to it. Then drive the app.

The step-by-step version, with checks, is the
[stomme-upgrade-stomme](../../.claude/skills/stomme-upgrade-stomme/SKILL.md) skill.

## Configuration

| Key | Default | Effect |
|---|---|---|
| `ConnectionStrings:DefaultConnection` | empty | Empty: the installer runs. |
| `Database:DefaultDBType` | empty (SQL Server) | The provider. |
| `Installation:*` | empty | The unattended install, above. |
| `AvailableDatabases` | six entries | What the wizard offers. Removing an entry hides it. |
| `RenderMode`, `Runtime`, `Prerender` | `Static`, `Server`, absent | The first site's rendering, at install only. |
| `STOMME_APPSETTINGS_PATH` (environment variable) | `<current directory>/Data`, set by `Program.cs` | Where the override file lives. |

## Rules and pitfalls

- **A partial unattended configuration stops the app.** A connection string with no `DefaultAlias`, `HostEmail` or
  `HostPassword` is reported once in the log and the process exits. Either supply all four or none.
- **A weak `HostPassword` in an unattended install fails silently.** The site is created, the host user is not, and
  nothing is logged. Check that you can sign in right after an unattended install.
- **The first alias is the address you installed from.** Install at `localhost:44358` and deploy to `example.com`, and
  `example.com` resolves to no site. Add the public address under **Admin > Site Settings > Site Urls** before moving, or
  install through the public address.
- **Run from the `Server` folder** (or set the content root). Started elsewhere, the host does not find
  `appsettings.json` and the data directory moves.
- **Do not delete the override file of a live installation.** It holds the connection string; without it the app opens
  the installer against a fresh database.
- **Do not change `InstallationId`.** It keys the cache and identifies the installation.
- **Keep the data directory with the database.** On a relational install the module data and feature flags are SQLite
  files there unless you configured server databases for them; restoring the database without them loses module data.
- **Upgrade all `Stomme.*` packages together.** Mixed versions fail at restore or at run time.
- **A connection string given only as an environment variable is not saved.** The install keeps using it while the
  variable is set; start once without it and the installer opens.
- **Take the framework's `appsettings.json` on upgrade.** It holds `AvailableDatabases` and the defaults new switches
  rely on; a stale copy can hide a database provider or ship a switch in an unexpected state.

## Related

- [Configuration](../architecture/configuration.md)
- [Deploy](deploy.md)
- [Startup and discovery](../architecture/startup-and-discovery.md)
- [Backup and restore](../services/backup-restore.md)
- [Tenancy](../security/tenancy.md)
- Skills: [stomme-run-and-install](../../.claude/skills/stomme-run-and-install/SKILL.md),
  [stomme-upgrade-stomme](../../.claude/skills/stomme-upgrade-stomme/SKILL.md)
