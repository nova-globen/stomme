---
name: stomme-deploy-app
description: Use when asked to deploy, publish, host, containerise or put this Nova Globen Stomme app online - a Dockerfile, a docker run or compose setup, a Linux service, a reverse proxy (nginx), persistent volumes, health checks, or moving an installation to a public host name.
---

# Deploy the app

You will have a published app running behind a reverse proxy, with its installation, uploads and data-protection keys
on persistent storage, a supervisor that restarts it, and a health check that does not restart-loop a fresh install.
How it works, and what each path holds: [Deploy](../../../docs/operations/deploy.md); settings:
[Configuration](../../../docs/architecture/configuration.md).

## Steps

1. **Decide where the data-protection keys live.** Stomme persists them itself
   ([Deploy > Data-protection keys](../../../docs/operations/deploy.md#data-protection-keys)): in
   `DataProtection:KeysPath` (environment variable `DataProtection__KeysPath`) when set, else beside the override file.
   Name a folder on persistent storage in `DataProtection__KeysPath`. If the app will later run from a different
   folder, also set `DataProtection__ApplicationName` before the first install. No code to add.
2. **Keep the local installation out of the publish.** `Server/Stomme.Application.Server.csproj` removes `Data\**`
   from content - check the line is there (an app made from a template before 10.73.0 lacks it; add
   `<Content Remove="Data\**" />` and `<None Remove="Data\**" />` in an item group of their own). Without it `dotnet publish` copies
   `Server/Data/appsettings.override.json` (your local connection string and host password) into the output, and the
   deployed app installs itself from it. After a publish, the output folder must have no `Data` directory.
3. **Choose the shape.**
   - Container: add the `Dockerfile` from [Deploy > A container](../../../docs/operations/deploy.md#a-container) at the
     app's root, and a `.dockerignore` with `**/bin`, `**/obj`, `Server/Data`, `Server/Content`,
     `Server/wwwroot/Content`, `Server/Packages`.
   - Linux service: `dotnet publish Server/Stomme.Application.Server.csproj -c Release -o ./publish`, copy `publish`
     to the server (for example `/opt/myapp`), and install the systemd unit from
     [Deploy > A Linux service](../../../docs/operations/deploy.md#a-linux-service-systemd) with `Restart=always` and
     `Environment=DataProtection__KeysPath=/var/lib/myapp/keys`. Publish into an empty folder.
4. **Persistent storage.** Make these survive a redeploy (volumes for a container, directories outside the publish
   folder or kept in place for a service): `Data`, `Content`, `wwwroot/Content`, and the keys directory. For a service
   that is redeployed by replacing `/opt/myapp`, point `STOMME_APPSETTINGS_PATH` at a persistent directory holding an
   `appsettings.override.json` (create it with `{}`), and keep `Data`, `Content` and `wwwroot/Content` with symbolic
   links or by deploying over the folder without deleting them.
5. **Database.** SQLite is the default and needs no server: each tenant's database is a file in `Data` (a site with a
   tenant of its own in `Installation:Sites` gets its own file), and so are the module and feature stores. Keep `Data`
   on a persistent volume; the backups go to `Data/backups` by default. Use PostgreSQL or SQL Server only when you will
   run more than one instance of the app; then also set `ConnectionStrings:ModuleAggregates` and
   `ConnectionStrings:FeatureManagement` to server databases. A compose file for one host with several sites:
   [Deploy > One host, several sites](../../../docs/operations/deploy.md#one-host-several-sites).
6. **Secrets.** Pass them as environment variables (`ConnectionStrings__DefaultConnection`,
   `Installation__HostPassword`) or write them into the override file on the persistent `Data` volume. Never bake them
   into the image or commit them. A connection string given as an environment variable is not written into the
   override file by the install: keep the variable set for every start, or the installer opens again.
7. **Reverse proxy.** Put nginx (or another proxy) in front with the configuration from
   [Deploy > nginx in front](../../../docs/operations/deploy.md#nginx-in-front): pass `Host`, `X-Forwarded-Proto`,
   `X-Forwarded-For`, `X-Forwarded-Host`, WebSocket upgrade, and a `client_max_body_size` that fits your uploads. Bind
   the app to `127.0.0.1` or a private network only.
8. **Run it.**
   - Container:
     ```
     docker build -t myapp:latest .
     docker run -d --name myapp --restart unless-stopped -p 127.0.0.1:8080:8080 \
       -v myapp-data:/app/Data -v myapp-content:/app/Content \
       -v myapp-public:/app/wwwroot/Content -v myapp-keys:/app/keys \
       myapp:latest
     ```
   - Service: `sudo systemctl enable --now myapp`.
9. **Install at the public address.** Open `https://<public host>/` and complete the installer there, so the first
   alias is the public host name; or install unattended with `Installation:DefaultAlias` set to it. When moving an
   existing installation, add the new host name under **Admin > Site Settings > Site Urls** before switching DNS.
10. **Health checks.** Liveness: `GET /healthz` (`200 alive`), for any `Host` header - the container check needs no
   site's host name. Readiness for a load balancer: `GET /readyz`, which is `503` until the first health probe has run
   after installation. Never use `/readyz` for liveness.
11. **Backups, if nobody will click through admin.** `Backup__Enabled=true`,
   `Backup__Schedule__IntervalHours=24` and `Backup__Schedule__PassphraseFile=<a mounted secret>` create the first backup
   schedule at start-up and switch its job on; archives go to `/app/Data/backups` (on the data volume) unless
   `Backup:Archive` names S3. A restart never adds a second schedule; later changes are made at `admin/backup`. See
   `docs/services/backup-restore.md`.

## Verify

- The publish output (or the image's `/app`) has no `Data` directory before the first start.
- `curl -i http://127.0.0.1:8080/healthz` (container) answers `200` and `alive` before and after installation, and adds
  no `127.0.0.1:8080` row under **Admin > Site Settings > Site Urls**;
  `/readyz` answers `503 not-ready` before installation and `200 ready` some time after it.
- The keys directory holds a `key-<guid>.xml` file after the first start.
- The public address opens the installer on a fresh deployment, and the site after installation; the page loads over
  HTTPS with no mixed-content or WebSocket errors in the browser console. An Interactive page responds to clicks.
- **System Info** (`/admin/system`) shows **Root Path** as expected (`/app` in the container) and **Environment** as
  `Production`.
- Upload a file to a private and a public folder, save a setting, then redeploy (rebuild the image and recreate the
  container, or republish the service). After the redeploy: the site is still installed, you are still able to sign
  in, the files are still there, and a secret saved in the admin pages (for example a connection's credentials) still
  reads correctly - that last check proves the keys survived. A browser session (or a saved sign-in cookie) from
  before the redeploy is still signed in.
- `docker inspect --format '{{.State.Health.Status}}' myapp` reports `healthy`.
- **Restart Application** in System Info brings the app back by itself within a few seconds.

## Common mistakes

- **After a redeploy the installer appears again.** Cause: `Data` (with `appsettings.override.json`) was not on
  persistent storage, or a volume was mounted at `/app/data` (lower case). Fix: persist `/app/Data`, restore the
  override file from the backup.
- **After a redeploy everyone is signed out and saved secrets read as unreadable.** Cause: the data-protection keys
  were lost with the container, or the app now runs from another folder and no application name is set (the log says
  `Unprotect ticket failed`). Fix: step 1 (`DataProtection__KeysPath` on a volume, and `DataProtection__ApplicationName` if the
  folder changes); re-enter the secrets once.
- **The deployed app never shows the installer and already has a host account with your local password.** Cause:
  `dotnet publish` copied the local `Server/Data/appsettings.override.json`. Fix: step 2, republish into an empty
  folder, and remove the `Data` folder it installed.
- **A local test of the container answers `500` on every page, the log saying `Connection refused (127.0.0.1:<port>)`.**
  Cause: the container is published on a different host port than the one it listens on (`-p 127.0.0.1:5000:8080`),
  and the host's calls to its own API follow the loopback address the browser used. Fix: publish on the same port
  (`-p 127.0.0.1:8080:8080`).
- **The site answers "not found" or the wrong site at the public address.** Cause: the public host name is not an
  alias of the site, or the proxy does not pass `Host`. Fix: add the alias; set `proxy_set_header Host $host`.
- **Links and redirects use `http://` behind a TLS proxy.** Cause: `X-Forwarded-Proto` not passed. Fix: pass it.
- **Interactive pages freeze after the first render.** Cause: the proxy does not pass the WebSocket upgrade. Fix: the
  `Upgrade`/`Connection` headers and `proxy_http_version 1.1`.
- **The container restarts every few minutes on a fresh deployment.** Cause: the health check calls `/readyz`. Fix:
  use `/healthz`.
- **Restart Application leaves the app down.** Cause: `Restart=on-failure` or a `--restart on-failure` policy; a
  restart from the admin pages exits with success. Fix: `Restart=always`, `--restart unless-stopped`.
- **Uploads fail above a few megabytes behind nginx.** Cause: nginx's default body size limit. Fix: raise
  `client_max_body_size`.
- **A module installed from the admin pages is gone after a redeploy.** Cause: packages unpack into the application
  folder. Fix: bundle the module at build time.
- **Permission denied writing to a mounted directory.** Cause: the bind-mounted host directory is not writable by the
  image's `$APP_UID` user. Fix: change the directory's owner, or use named volumes.
