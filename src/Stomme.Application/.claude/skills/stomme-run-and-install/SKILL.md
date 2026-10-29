---
name: stomme-run-and-install
description: Use when asked to build, run, start or install this Nova Globen Stomme app, to get past the first-run installer, to install it unattended, to sign in as the host, or to reset a local installation and start again.
---

# Run and install the app

You will have the app running locally with an installed site you can sign in to as the host, either through the web
installer or unattended from configuration. How it works: [Install, run and upgrade](../../../docs/operations/install-run-upgrade.md)
and [Configuration](../../../docs/architecture/configuration.md).

## Steps

1. Build from the app's root (the folder holding `Stomme.Application.slnx`):
   ```
   dotnet build
   ```
   A restore error naming `Stomme.*` means the feed in `NuGet.config` was not reachable; fix that before anything else.
   Do not run `dotnet clean`.
2. Decide: web installer (step 3) or unattended (step 4). For an agent working alone, unattended is faster and
   repeatable; SQLite needs no server.
3. **Web installer.** From `Server`:
   ```
   cd Server
   dotnet run
   ```
   Open the URL it prints (`http://localhost:44358` with the default profile). The installer opens at `?step=database`:
   - **Database**: the list is alphabetical and does not start on SQLite; choose **SQLite** for local work (or a server
     you have) and keep or change the file name. **Next**.
   - **Account**: **Username** is empty - type `host`; **Password** and **Confirm** (at least six characters, with
     upper case, lower case, a digit and a symbol); **Email**; **Full Name**; leave **Template** on **Default Site
     Template**. **Next**.
   - **Configure**: shows the framework's own install-time settings (a note about Notifications, HtmlText's **Default
     Content**); the defaults are fine. **Next**.
   - **Finish**: leave **Register** unticked, press **Install Now**. After some seconds the browser goes to the new
     site's home page.
4. **Unattended.** Before the first start, write `Server/Data/appsettings.override.json` (create the folder; the file is
   git-ignored). Generate a password; do not reuse a real one:
   ```json
   {
     "Database": {
       "DefaultDBType": "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server"
     },
     "ConnectionStrings": {
       "DefaultConnection": "Data Source=|DataDirectory|/App.db;"
     },
     "Installation": {
       "DefaultAlias": "localhost:44358",
       "HostEmail": "host@example.com",
       "HostPassword": "<generated, e.g. Xk4!q9-Lm2>"
     }
   }
   ```
   `DefaultAlias` must be the host and port you will browse to (see `Server/Properties/launchSettings.json`). Then:
   ```
   cd Server
   dotnet run
   ```
   The install runs before the app starts listening. When it is done, remove `HostPassword` from the file.
5. Sign in at `/login` as `host` with that password. Administration is at `/admin`.
6. To start again from nothing (SQLite only): stop the app, delete `Server/Data`, `Server/Content` and
   `Server/wwwroot/Content`, and repeat from step 3 or 4. With a server database, also drop the database.

## Verify

- The console shows the app listening and no "The installation did not complete" error.
- The home page renders the default site with its theme; the browser console has no errors.
- `/login` signs you in; `/admin` lists the administration pages; **System Info** (`/admin/system`) opens.
- `Server/Data/appsettings.override.json` now holds `ConnectionStrings:DefaultConnection`, `Database:DefaultDBType`,
  `InstallationId`, `InstallationVersion` and `InstallationDate`, and `Server/appsettings.json` is unchanged
  (`git status` shows no change to it).
- `Server/Data` holds the master database (SQLite), `Stomme.ModuleAggregates.db` and `Stomme.Features.db`.
- `/healthz` answers `alive`.
- The console log has no `fail:` line. (A `fail` from `Microsoft.AspNetCore.Antiforgery` saying the token "could not be
  decrypted" is a cookie left in the browser by another app on the same host name; cookies ignore the port. Clear the
  site's cookies and sign in again. The same clash can sign you out while two apps run on `localhost`.)

## Common mistakes

- **The app exits right after start with "The installation did not complete: Error Installing Master Database For
  DefaultConnection: an automated installation also needs Installation:HostPassword (or HostPasswordFile)"** (or
  whichever of `Installation:DefaultAlias`, `Installation:HostEmail` and the password are missing). Cause: a connection
  string is set but those are not. Fix: add them (the password may be a file: `Installation:HostPasswordFile`).
- **The home page says "Configured For An Unattended Installation" instead of showing the installer.** Cause: the
  `Installation` section sets `DefaultAlias`, `HostEmail`, `HostPassword` or `HostPasswordFile`, so the browser wizard is
  refused, but the unattended install did not run (no connection string, or a bad password file - the log says which).
  Fix: complete the configuration, or clear those keys to use the web installer.
- **The address you browse to answers a blank 404.** Cause: it is not an alias - only configured host names are (the
  installer records the address it was opened at; unattended uses `DefaultAlias`), and any other host gets a 404. Fix:
  browse to the installed address and add the new one under **Admin > Site Settings > Site Urls**, or reset (step 6).
- **The installer's secrets ended up in `Server/appsettings.json`.** Cause: `Server/Program.cs` lost its
  `KeepInstallStateOutOfAppSettings` call, which creates `appsettings.override.json` (in `Data`, or in the directory
  `STOMME_APPSETTINGS_PATH` names) before the host starts; without that file the installer writes to
  `appsettings.json`. Fix: restore `Program.cs` from the template, move the values into the override file, revert
  `appsettings.json`.
- **"The configuration file 'appsettings.json' was not found and is not optional" at start.** Cause: the app was started from another folder. Fix: run from `Server`.
- **The unattended install finished but no host account exists; sign-in fails.** Cause: `HostPassword` did not meet
  the password rules. The install does not report this: the site is created without its host user and no error is
  logged. Fix: reset (step 6) and use a password with upper case, lower case, a digit and a symbol, at least six
  characters.
- **Admin > System Info > Restart Application stopped the app for good.** Cause: it ends the process; `dotnet run` does
  not restart it. Fix: run `dotnet run` again.
