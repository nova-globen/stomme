# Nova Globen Stomme `setup/` — build, bundle, deploy

This folder builds a deployable Stomme bundle and installs it on a VM (Linux or Windows), configuring the
database, the optional OpenIddict IdP (with a **persistent** signing certificate), TLS, host/port and the host
account. It mirrors the Fravashi (formerly Fravashi) `setup/` layout (build-bundle → two-tier install → release manifest), adapted
to Stomme's shape: Stomme is an **ASP.NET Core / Blazor web app** you publish and run behind a reverse
proxy — there is no container studio image or launcher (unlike Fravashi), so a "bundle" is the published
`Stomme.Server` plus the installer that configures it.

```
setup/
  build-bundle.ps1 / .sh      Publish Stomme.Server for one RID -> dist/stomme-<rid>/
  install.ps1 / .sh           NETWORK installer: fetch a release bundle, verify it against SHA256SUMS, unpack, delegate
  install/
    install-windows.ps1       LOCAL INTERACTIVE installer (Windows) — prompts + writes config
    install-unix.sh           LOCAL INTERACTIVE installer (Linux/macOS) — prompts + writes config
  release/
    release-config.json       Release-wide policy (criticality floor)
    variants.json             Variant x RID matrix the release pipeline fans out
    build-release.ps1         Build every variant x RID into dist/release/<version>/ + manifest + SHA256SUMS
    release-manifest.example.json  Shape of the published manifest install.* consumes
  deploy/
    stomme.service.template   systemd unit the unix installer fills in and installs
    nginx.conf.example        Reverse-proxy example (TLS termination + Blazor upgrade headers)
  Dockerfile / docker-entrypoint.sh   OPTIONAL container deploy (primary path is publish + systemd)
```

## 1. Build a bundle

```powershell
# Windows host, Windows target:
pwsh setup/build-bundle.ps1 -Rid win-x64 -Version 1.0.0

# Cross-publish a Linux target from ANY host (works on Windows — this is how CI builds the VM bundle):
pwsh setup/build-bundle.ps1 -Rid linux-x64 -Version 1.0.0
# or on a Linux/macOS host:
./setup/build-bundle.sh linux-x64 setup/dist 1.0.0 full
```

Each run produces `setup/dist/stomme-<rid>/` containing `app/` (the published host), the matching local
installer, `deploy/` templates, and a `bundle.json` stamp. Pass `-Zip` / `ZIP=1` to also produce a
`stomme-<variant>-<rid>.zip` for a release (no version in the name - the release folder carries it). Bundles are **self-contained by default**
(the VM needs no .NET install); pass `-SelfContained:$false` / `SELF_CONTAINED=0` for a smaller
framework-dependent build that needs the ASP.NET 10 runtime on the target.

> **BLAZOR106:** the build scripts never `dotnet clean` and never delete `obj/` or `.dswa.cache.json` — a
> clean rebuild mangles the FluentUI static-web-asset paths on this toolchain. `dotnet publish` is incremental.

### Build a release for a downloads host

```powershell
pwsh setup/release/build-release.ps1 -Version 10.75.1
```

It builds every variant x RID in `release/variants.json` into `setup/dist/release/<version>/`, beside
`install.sh`, `install.ps1`, a `release-manifest.json` and a `SHA256SUMS`. Publishing is a separate act the script never
does: copy the folder to `<base>/<version>/`, then make `<base>/latest/` an exact copy of it - the network installers read
`<base>/latest/` unless given a version.

## 2. Deploy on a Linux VM (end to end)

1. **Get the bundle onto the VM** — either build `linux-x64` and `scp` `setup/dist/stomme-linux-x64/`, or use
   the network installer once a release exists:
   ```bash
   curl -fsSL https://github.com/nova-globen/stomme/releases/latest/download/install.sh | bash
   ```
   Releases live on the project's GitHub Releases; `--base-url` points at a copy laid out the same way. The network installer checks the bundle against the release's `SHA256SUMS` before
   unpacking, then launches the interactive installer below.

2. **Run the interactive installer** from inside the bundle (as root, so it can install the systemd unit):
   ```bash
   sudo ./install-unix.sh
   ```
   It prompts for, and writes into `app/appsettings.Production.json`:
   - **Database** — provider (`sqlserver`/`localdb`/`azuresql`/`sqlite`/`postgres`/`ravendb`) + connection
     string → `Database:DefaultDBType` + `ConnectionStrings:DefaultConnection`.
   - **Host account** — alias/hostname, email, password → the `Installation` section.
   - **OpenIddict IdP** — if enabled, it **generates a persistent signing certificate for you** with `openssl`
     (RSA-2048, `digitalSignature`+`keyEncipherment`, a PEM pair under `app/certs/`) and writes the
     `OpenIddict:*CertificatePemPath`/`KeyPath` keys (see §4).
   - **TLS** — `proxy` (default; TLS terminates at nginx/Caddy, Kestrel serves HTTP on `127.0.0.1:<port>`) or
     `kestrel` (Kestrel serves HTTPS from a cert you supply).
   - **Port** and a generated **InstallationId**.

   Because the DB **and** the host account are prefilled, the app boots straight to the site — the first-run
   web wizard is skipped (`IsInstalled()` is true once `ConnectionStrings:DefaultConnection` is set).

3. **Front it with a reverse proxy** — copy `deploy/nginx.conf.example`, set your hostname + TLS cert, reload
   nginx. Stomme resolves the tenant from the `Host` header, so `proxy_set_header Host $host` is required.

   > **Behind a CDN / tunnel, prefer `Runtime=WebAssembly` (or `Static` SSR) over the stock `Runtime=Server`.**
   > Blazor **Server** holds a persistent SignalR **WebSocket circuit** per user — one long-lived socket, every
   > interaction a round-trip. Behind a stateful-hostile edge (e.g. a **Cloudflare Tunnel + Access**) that socket
   > connects but does not reliably survive: the CDN's WebSocket idle cutoff, auth-session refreshes, and tunnel
   > connection recycling drop it, and a failed reconnect makes **every** user see *"An Unexpected Error Has
   > Occurred"* on the first interaction — with **no request reaching the origin**, so it is invisible in both the
   > browser network tab and the server log. **WebAssembly** runs the app in the browser over stateless HTTPS, so
   > there is no circuit to drop; each call is an independent request that carries the auth cookie. Set it per site
   > (Admin → Site Settings → *Runtime*) or seed it at install via the `Runtime` key in `appsettings`. Server mode
   > *can* be hardened (force WebSockets end-to-end, exclude `/_blazor` from the auth edge, raise SignalR
   > keep-alive, disable proxy buffering), but that hardens a stateful socket instead of removing it. Measured on
   > the UAT deploy, 2026-07-22.

4. **Start it:** `sudo systemctl start stomme` (the installer wrote and enabled the unit; an install made before the rename keeps its `stomme` unit, so start that one there).

### Non-interactive / automated

Every prompt has a flag, so the whole install can be scripted or dry-run:

```bash
# Preview the exact config + systemd unit without writing anything:
./install-unix.sh --non-interactive --dry-run \
  --db postgres --conn "Server=127.0.0.1;Port=5432;Database=stomme;User Id=stomme;Password=..." \
  --alias example.com --host-email admin@example.com --host-password 'S3cret!' --idp yes --port 5000
```

Windows is identical with `install-windows.ps1` (parameters `-Db`, `-Conn`, `-Idp yes`, `-NonInteractive`,
`-DryRun`, …); its IdP cert is generated as a password-protected `.pfx` via the .NET X509 APIs.

## 3. Deploy with a container (optional)

```bash
docker build -f setup/Dockerfile -t stomme:latest .
docker run -p 8080:8080 \
  -v $PWD/appsettings.Production.json:/app/appsettings.Production.json:ro \
  -v $PWD/certs:/app/certs:ro stomme:latest
```

The container path is a convenience; the primary VM path is the published bundle + systemd above.

## 4. OpenIddict IdP — persistent signing certificate (D66)

The authority (behind `OpenIddict:Enabled`) previously used a **shared ephemeral** signing key regenerated on
every process start, so the JWKS `kid` and every issued token changed on each restart — fine for a spike, broken
for a real deployment (each restart invalidates outstanding tokens and rotates the published JWKS). D66 lets the
authority load a signing certificate from configuration and **falls back to the ephemeral key when none is
configured**, so a stock install is unchanged.

Config keys (written by the installer; documented here so they can also be set by hand):

| Key | Meaning |
| --- | --- |
| `OpenIddict:Enabled` | Turn the co-hosted authority on. |
| `OpenIddict:SigningCertificatePath` + `:SigningCertificatePassword` | A PKCS#12 (`.pfx`) signing cert. |
| `OpenIddict:SigningCertificatePemPath` + `:SigningCertificateKeyPath` | A PEM cert + key pair (the `openssl` shape). |
| `OpenIddict:EncryptionCertificatePath` / `…Password` / `…PemPath` / `…KeyPath` | Same, for the encryption cert (protects authorization codes). A single cert carrying both `digitalSignature` and `keyEncipherment` usages may be pointed at both — the installer generates exactly that. |

When **no** certificate is configured, the authority keeps the pre-D66 ephemeral keys — every existing test and
the ephemeral path are byte-for-byte unchanged.

**The authority's own store needs NO configuration (BL-55).** OpenIddict's applications/tokens live in a
dedicated per-tenant store addressed by `ConnectionStrings:{tenant}_OpenIddict`. When that key is absent — the
normal case; neither installer writes it — the store is **derived** from the tenant's own connection string as a
dedicated sibling (`stomme.db` → `stomme_OpenIddict.db`; `Database=stomme` → `Database=stomme_OpenIddict`,
same for a RavenDB descriptor) and its schema/database is created automatically on first use, whatever database
the install chose. Set the explicit `{tenant}_OpenIddict` key only to place the authority store somewhere else —
never point it at the tenant's own database (the authority store is isolated by design, D61).

**Multi-tenant note.** Stomme's OpenIddict authority isolates tenants by the **host-inferred issuer + the
per-tenant store**, not by the key (measured in the B1 spike, D61). A cross-host token is rejected by
OpenIddict's native issuer check regardless of the signing key, so a **single persistent signing certificate
shared across all tenants is correct** here. Independent per-tenant keys / rotation remain the separate,
deferred B1b work.

Generate a cert by hand (equivalent to what the installer does):

```bash
# PEM pair (unix / openssl):
openssl req -x509 -newkey rsa:2048 -sha256 -days 3650 -nodes \
  -keyout openiddict-signing.key -out openiddict-signing.pem \
  -subj "/CN=Stomme OpenIddict" -addext "keyUsage=critical,digitalSignature,keyEncipherment"
```

## Verifying a deploy

- **Site renders:** browse the alias through the reverse proxy; complete any remaining first-run step.
- **IdP is persistent:** `curl https://<alias>/.well-known/jwks`, note `keys[0].kid`, restart the service, and
  confirm the `kid` is unchanged. With the ephemeral fallback it would change on every restart. (This is the
  exact property the D66 driving test `OpenIddictSigningCertificateTests` asserts against the real host.)
