# Deploy

A Nova Globen Stomme app deploys like any ASP.NET Core app: publish the `Server` project, run the result behind a reverse proxy,
and keep a few directories and the data-protection keys on persistent storage. This page lists what the app writes,
what must survive a redeploy, and the settings that matter behind a proxy or in a container.

## How it works

### What `dotnet publish` produces

Publishing `Server/Stomme.Application.Server.csproj` gives one folder that holds:

- the server (`Stomme.Application.Server.Stomme.dll`), the framework assemblies, and your `Client` and `Shared`
  assemblies;
- the WebAssembly runtime and boot files under `wwwroot/_framework`, built from the `Client` project, and the packages'
  static web assets;
- `appsettings.json` and `features.json`;
- `wwwroot/Modules/Templates` and `wwwroot/Themes/Templates`, copied by the project's `PublishStommeTemplates`
  target so **Create Module** and **Create Theme** work in the published app.

The folder needs the ASP.NET Core 10 runtime, or publish self-contained with `-r <rid> --self-contained`.

**Keep the development installation out of it.** The Web SDK publishes every `.json` file under `Server/` as content,
so a `Server/Data/appsettings.override.json` left by a local install - its connection string, `InstallationId` and any
`Installation:HostPassword` - lands in the publish folder as `Data/appsettings.override.json`, and the published app
then installs itself from it, silently, without the installer. The template's
`Server/Stomme.Application.Server.csproj` excludes the folder; an app made from a template before 10.73.0 lacks it
and must add it:

```xml
<ItemGroup>
  <Content Remove="Data\**" />
  <None Remove="Data\**" />
</ItemGroup>
```

After publishing, the publish folder must have no `Data` directory.

### The content root, and what the app writes

The content root is the current directory when the process starts. Everything below is relative to it:

| Path | What | Lost if not persisted |
|---|---|---|
| `Data/appsettings.override.json` | The installation's settings: connection string with password, database type, `InstallationId`, settings changed in the admin pages. `Program.cs` puts it here unless `STOMME_APPSETTINGS_PATH` names another directory. | The installation: the next start opens the installer. |
| `Data/*.db` | On a relational install: `Stomme.ModuleAggregates.db` (every storage-blind module's data, unless `ConnectionStrings:ModuleAggregates` names a server database), `Stomme.Features.db` (feature flags, unless `ConnectionStrings:FeatureManagement` is set); the master database too when it is SQLite. | Module data, flags, a SQLite installation. |
| `Data/backups` | Local backup archives and a staged restore (`Backup:Archive:Provider` = `Local`). | Backups. |
| `Content/Tenants` | Files in private folders. | Private uploads. |
| `wwwroot/Content/Tenants` | Files in public folders. | Public uploads. |
| `Content/Staging` | Chunked uploads in progress. | Uploads in flight. |
| `Content/Log` | The file log. | Logs. |
| `Packages` | Module and theme packages waiting to be unpacked at the next start. | Nothing once unpacked. |

The `Data` directory is spelled with a capital `D`; on Linux a mount at `data` does not catch it.

A module or theme installed through the admin pages unpacks into the application folder itself, so a new image or a
fresh publish discards it. Bundle modules into the app at build time instead
([stomme-bundle-external-module](../../.claude/skills/stomme-bundle-external-module/SKILL.md)).

### Data-protection keys

The keys protect the sign-in cookies, antiforgery tokens, the visitor cookie, and Stomme's per-site secret store (AI
keys, connection credentials and other secrets saved in the admin pages). Lose them and every user is signed out and
every stored site secret becomes unreadable and must be entered again. Stomme keeps them (since 10.73.2), in this order:

1. in `DataProtection:KeysPath`, when the deployment names a folder;
2. in ASP.NET Core's default folder for the process's user (`~/.aspnet/DataProtection-Keys` on Linux,
   `%LOCALAPPDATA%\ASP.NET\DataProtection-Keys` on Windows) when keys are already there - an existing installation, or a
   deployment that mounts that folder, keeps its ring;
3. otherwise in `DataProtection-Keys` beside the installation's override file (the folder `STOMME_APPSETTINGS_PATH`
   names - `Server/Data` in an app from the template), which a deployment persists anyway.

Keys alone are not enough when the app MOVES: unless `DataProtection:ApplicationName` is set, ASP.NET Core isolates the
keys per content-root path, so the same keys and data started from another folder (a service redeployed to a new
versioned directory) still sign everyone out. Set `DataProtection:ApplicationName` before the first install if the app
will move; setting or changing it later signs everyone out once and makes secrets stored before it unreadable.

### Behind a reverse proxy

- Stomme resolves the site from the request's host name. The proxy must pass the public host name in `Host` (or in
  `X-Forwarded-Host`), and the scheme in `X-Forwarded-Proto`.
- The host believes `X-Forwarded-For`, `X-Forwarded-Proto` and `X-Forwarded-Host` only from a proxy: by default from
  loopback and the private networks (`127.0.0.0/8`, `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, `::1`,
  `fc00::/7`), which covers a proxy on the same machine, in a container network or on the LAN. A proxy anywhere else is
  named in `ForwardedHeaders:KnownProxies` (addresses) or `ForwardedHeaders:KnownNetworks` (`address/prefix`) - as a
  JSON array or one comma-separated value - and then ONLY what is named is believed. Every trusted hop is walked, so a
  chain of proxies (a CDN, a tunnel, an edge proxy) yields the real client address as long as each hop is trusted and
  appends to `X-Forwarded-For`. The proxy in front of the app must OVERWRITE `X-Forwarded-Host` and `X-Forwarded-Proto`
  with what it saw. Still bind the app's own port to loopback or a private network that only the proxy reaches.
- Interactive Server pages hold a WebSocket (SignalR) connection. The proxy must pass `Upgrade` and `Connection` and
  allow long-lived connections.
- Client services that run on the server call the host's own API. By default those calls go to the host's first
  plain-HTTP listener, with the public host and scheme carried in headers, so the container needs no route to its own
  public name. A host with only an HTTPS listener calls its public address instead; set
  `Installation:InternalUrl` to a reachable URL of the app in that case.

### Health endpoints

- `/healthz` answers `200 alive` whenever the process serves requests, installed or not. Use it for liveness and
  container health checks. It answers for **any** `Host` header, also one that names no site (`127.0.0.1:8080`, an
  external monitor's own name) and on an installation with several sites. The two health paths skip site resolution
  and the HTTPS redirect, so a probe answers where any other path from an unknown host gets a 404, and is never
  redirected.
- `/readyz` answers `200 ready` only after the health probe job has run and found the host ready; before that, before
  the installation exists, and always when `Health:Enabled` is `false`, it answers `503 not-ready`. Do not use it as a
  liveness check: a fresh deployment would be restarted before anyone could open the installer.

## Using it from an app

### Publish and run

```
dotnet publish Server/Stomme.Application.Server.csproj -c Release -o ./publish
cd publish
ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS=http://127.0.0.1:5000 dotnet Stomme.Application.Server.Stomme.dll
```

Start the process with the publish folder as its current directory.

### A container

`Dockerfile` at the app's root (the build needs nuget.org reachable), with a `.dockerignore` beside
it that keeps local state out of the build context:

```
**/bin
**/obj
Server/Data
Server/Content
Server/wwwroot/Content
Server/Packages
```

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish Server/Stomme.Application.Server.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /out ./
RUN mkdir -p /app/Data /app/Content /app/wwwroot/Content /app/Packages /app/keys && chown -R $APP_UID /app
USER $APP_UID
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:8080
ENV DataProtection__KeysPath=/app/keys
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
  CMD bash -c 'exec 3<>/dev/tcp/127.0.0.1/8080 && printf "GET /healthz HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n" >&3 && head -n1 <&3 | grep -q " 200 "'
ENTRYPOINT ["dotnet", "Stomme.Application.Server.Stomme.dll"]
```

The runtime image has no `curl`, so the health check opens the port with bash. `DataProtection__KeysPath` puts the key
ring in the `myapp-keys` volume (see [Data-protection keys](#data-protection-keys)).

Run it with the persistent paths mounted and a restart policy that restarts after a clean exit (**System Info > Restart
Application** ends the process with success):

```
docker run -d --restart unless-stopped -p 127.0.0.1:8080:8080 \
  -v myapp-data:/app/Data \
  -v myapp-content:/app/Content \
  -v myapp-public:/app/wwwroot/Content \
  -v myapp-keys:/app/keys \
  myapp:latest
```

A bind mount from the host must be writable by the image's `$APP_UID` user.

When you try the container on your own machine, publish it on the **same port number it listens on**
(`-p 127.0.0.1:8080:8080`, or change `ASPNETCORE_URLS` to match). The host's calls to its own API go to the address the
browser used when that address is a loopback one, so with `-p 127.0.0.1:5000:8080` the container calls
`127.0.0.1:5000` inside itself, where nothing listens: the installer answers `500` and the log shows
`HttpRequestException: Connection refused (127.0.0.1:5000)`. Behind a proxy with a public host name this does not
arise.

If `dotnet publish` reports that the `wasm-tools` workload is required, add `RUN dotnet workload install wasm-tools`
before the publish in the build stage.

### One host, several sites

One container can serve a host's root site and sites in folders (`example.com/shop`), installed unattended on first
start, with SQLite (one file per tenant in the data volume), its secrets as files and every switch in the environment:

```yaml
services:
  app:
    image: myapp:1.0.0
    restart: unless-stopped
    ports: [ "127.0.0.1:8080:8080" ]
    volumes:
      - app-data:/app/Data
      - app-content:/app/Content
      - app-public:/app/wwwroot/Content
      - app-keys:/app/keys
    secrets: [ host-password, backup-passphrase ]
    environment:
      Database__DefaultDBType: "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server"
      ConnectionStrings__DefaultConnection: "Data Source=|DataDirectory|/root.db;"
      Installation__DefaultAlias: "example.com"
      Installation__HostEmail: "admin@example.com"
      Installation__HostPasswordFile: "/run/secrets/host-password"
      # a site in a folder on the same tenant, and one with a tenant (and SQLite file) of its own
      Installation__Sites__0__Name: "Shop"
      Installation__Sites__0__Aliases: "example.com/shop"
      Installation__Sites__1__Name: "Games"
      Installation__Sites__1__Aliases: "example.com/games"
      Installation__Sites__1__Tenant: "Games"
      Installation__Sites__1__ConnectionString: "Data Source=|DataDirectory|/games.db;"
      # a bundled module this host does not run: none of its jobs, hubs or endpoints, and it is placed on no new page
      FeatureManagement__module.reviews.enabled: "false"
      # sign-in for apps: each site is its own issuer (https://example.com, https://example.com/shop); give it a
      # signing certificate (OpenIddict__SigningCertificatePemPath and ...KeyPath), or every restart ends every token
      OpenIddict__Enabled: "true"
      # scheduled backups to Data/backups, kept 30 days
      Backup__Enabled: "true"
      Backup__Schedule__IntervalHours: "24"
      Backup__Schedule__PassphraseFile: "/run/secrets/backup-passphrase"
      # a mail relay that counts connections: probe it every 30 minutes, not every minute
      Health__MailServerProbeIntervalMinutes: "30"
secrets:
  host-password: { file: ./secrets/host-password }
  backup-passphrase: { file: ./secrets/backup-passphrase }
volumes: { app-data: {}, app-content: {}, app-public: {}, app-keys: {} }
```

- Every site keeps its own sign-in, cookies and users; the folder sites' requests reach the same container, so the proxy
  in front sends `example.com/*` to it (see [nginx in front](#nginx-in-front)). Each name in `Installation:Sites`
  is created on the first start that finds it missing; nothing is created twice.
- The **mail server** is a setting of each site, not of configuration: set it in **Admin > Site Settings > SMTP Settings** on each
  site (a relay on the host's network is the usual shape), or have a module of yours write the site's SMTP settings at
  start-up. See [Notifications](../services/notifications.md).
- A **module switched off** with `FeatureManagement__module.<id>.enabled=false` is still in the image and loaded; the
  host runs none of its jobs, hubs or endpoints and places it on no new page (a page it is already on keeps it). See
  [Configuration](../architecture/configuration.md).
- The **AI assistant** stays dormant until `AiAssistant:Enabled` is on and a site's administrator names a model for that
  site ([AI assistant](../ai/ai-assistant.md)).
- Keep the image's version and the compose file's settings in one place per host; upgrade by changing the image tag
  ([install, run, upgrade](install-run-upgrade.md)).

### Keep the data-protection keys

Nothing to add: name the folder in `DataProtection:KeysPath` (the container above sets it), or let Stomme keep the keys
beside the override file. The keys are stored unencrypted; protect the folder like a secret. Several instances behind one
load balancer must share the same key folder and `DataProtection:ApplicationName`. An app made from a template before
10.73.2 may carry its own `DataProtectionStartup`; it still works (it registers after Stomme's choice and wins) and can
be removed once the keys are where it put them - keep `DataProtection:KeysPath` and the application name it set.

### A Linux service (systemd)

```ini
[Unit]
Description=My Stomme app
After=network.target

[Service]
WorkingDirectory=/opt/myapp
ExecStart=/usr/bin/dotnet /opt/myapp/Stomme.Application.Server.Stomme.dll
Restart=always
RestartSec=5
User=myapp
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://127.0.0.1:5000
KillSignal=SIGINT

[Install]
WantedBy=multi-user.target
```

`Restart=always`, not `on-failure`: a restart from the admin pages exits with success.

### nginx in front

```nginx
map $http_upgrade $connection_upgrade { default upgrade; '' close; }

server {
    listen 443 ssl;
    http2 on;
    server_name example.com;
    ssl_certificate     /etc/letsencrypt/live/example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/example.com/privkey.pem;

    location / {
        proxy_pass         http://127.0.0.1:5000;
        proxy_http_version 1.1;
        proxy_set_header   Upgrade $http_upgrade;
        proxy_set_header   Connection $connection_upgrade;
        proxy_set_header   Host $host;
        proxy_set_header   X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;
        proxy_set_header   X-Forwarded-Host $host;
        proxy_read_timeout 100s;
    }
}
```

(The `map` belongs in the `http` block.) Raise `client_max_body_size` to at least `Files:MaxUploadBytes` if large
uploads must pass the proxy.

### First start on the server

Open the public address and run the installer there, so the site's first alias is the public host name; or install
unattended with `Installation:DefaultAlias` set to it
([Install, run and upgrade](install-run-upgrade.md#unattended-install)). Supply secrets through environment variables
(`ConnectionStrings__DefaultConnection`; the host password best as a file, `Installation__HostPasswordFile=/run/secrets/host-password`)
or the override file on the mounted `Data` volume, never baked into the image. A connection string supplied as an
environment variable is not copied into the override file by the install, so it must stay set for every later start.
Once the `Installation` section asks for an unattended install, the browser installer is refused, so a stranger who
reaches the host before the install completes cannot claim it.

## Configuration

| Key | Default | Effect |
|---|---|---|
| `ASPNETCORE_URLS` (environment) | ASP.NET Core's default | The listeners. Keep at least one plain-HTTP listener for the host's calls to itself, or set `Installation:InternalUrl`. |
| `ASPNETCORE_ENVIRONMENT` (environment) | `Production` | Outside `Development`: the `/Error` handler and HSTS. |
| `STOMME_APPSETTINGS_PATH` (environment) | `<content root>/Data` | Directory of the override file; created, with an empty file, when missing (`Program.cs` creates both too). |
| `Installation:InternalUrl` | empty | Where the host's calls to its own API go: empty (its plain-HTTP listener), a URL, or `public`. |
| `Installation:UseHttpsRedirection` | `false` | Leave off behind a TLS-terminating proxy. |
| `Health:LivenessPath`, `Health:ReadinessPath` | `/healthz`, `/readyz` | The health endpoints. |
| `Caching:*`, `Messaging:*` | memory cache, no bus | Needed for more than one instance; see below. |

## Rules and pitfalls

- **Persist `Data`, `Content`, `wwwroot/Content` and the data-protection keys.** Everything else is rebuilt by a new
  publish or image. Losing `Data` loses the installation itself.
- **A publish can carry your local installation.** Without `<Content Remove="Data\**" />` in the Server project (the template has it since 10.73.0), the
  local `Server/Data/appsettings.override.json` (connection string, host password) is published, and the deployed app
  installs from it instead of opening the installer. Check the publish folder for `Data` before shipping it.
- **Never expose the app port directly.** Put the TLS-terminating proxy in front; a client reaching the port itself
  cannot claim a host name or a scheme by header, and a `Host` that is no alias gets a 404.
- **Behind a proxy outside the private networks, links and sign-in break.** Its forwarded headers are not believed, so
  the app sees `http` and the proxy's address. Name it in `ForwardedHeaders:KnownProxies` or
  `ForwardedHeaders:KnownNetworks`.
- **The site answers only on its aliases.** A request whose host name matches no alias gets a plain 404. Add the public host
  name under **Admin > Site Settings > Site Urls** before switching DNS.
- **A restart from the admin pages is an exit.** Without a supervisor that restarts on a clean exit, the app stays down.
- **More than one instance needs more than a load balancer.** The default cache is in memory per instance, Stomme's
  internal sync events stay in their process, SQLite files cannot be shared, and the data-protection keys must be shared.
  Use server databases for `DefaultConnection`, `ModuleAggregates` and `FeatureManagement`, the Redis cache with
  `Caching:Distributed` and `Caching:ScaleOut`, and the message bus. See [Caching and sync](../services/caching-and-sync.md)
  and [Events and messaging](../services/events-and-messaging.md).
- **Do not use `/readyz` as a liveness probe.** It is `503` until the first health tick and always when health probing
  is off.
- **Start the process in the publish folder.** The content root is the current directory; started elsewhere, the app
  does not find `appsettings.json`.

## Related

- [Install, run and upgrade](install-run-upgrade.md)
- [Configuration](../architecture/configuration.md)
- [Security posture](../security/security-posture.md)
- [Backup and restore](../services/backup-restore.md)
- [Logging, health and metering](../services/logging-health-metering.md)
- Skill: [stomme-deploy-app](../../.claude/skills/stomme-deploy-app/SKILL.md)
