# Security posture

Nova Globen Stomme fails closed: when it cannot establish who is calling, which site a request belongs to, or whether a
capability is switched on, the answer is no. An app built on it must hold the same line. This page lists what the
framework does for you, what every module must do itself, how secrets are kept, and how the server reaches the network.

## How it works

### Where Stomme fails closed

- **No tenant is not every tenant.** Without a resolved alias, `ITenantContext.CurrentTenantId` is `null` and
  tenant-owned reads return nothing ([tenancy.md](tenancy.md)).
- **No request is no permission.** `IAccessGuard`, `[Requires]` and `[RequiresPermissionKey]` deny every requirement
  when there is no `HttpContext` (a hosted job).
- **No rows is no permission.** A permission key with no rows on the site is denied for everyone, administrators
  included; a malformed key is denied, never reinterpreted.
- **No feature evaluator is feature off.** `[Requires(Feature = "...")]` denies when no evaluator is registered.
- **Off by default.** Every capability that reaches out, runs code, keeps extra data or costs money is switched off in a
  stock installation (see [../architecture/configuration.md](../architecture/configuration.md)).
- **Denials answer cleanly.** A `[Requires]` denial escaping a controller becomes **403** `application/problem+json`, and
  is logged under the Security log function.

### Antiforgery

MVC controllers validate an antiforgery token on every `POST`, `PUT`, `DELETE` (anything but `GET`, `HEAD`, `OPTIONS`,
`TRACE`), sent in the header `X-XSRF-TOKEN-HEADER` (`Stomme.Shared.Constants.AntiForgeryTokenHeaderName`). The
client's `Stomme.Services.ServiceBase` adds it to every call, so a client service built on `ServiceBase` needs nothing
more. A request that carries an `Authorization` header (a bearer token) is not checked when the TOKEN is what signs it in;
when the sign-in cookie signs it in, it is checked whatever else it carries (since 10.73.2: before, any `Authorization`
header, an invalid one included, switched the check off). Code that calls the API from the page with its own `fetch` must send the header itself. A `GET` is never checked
- never change state on a `GET`.

### Sign-in cookie and sessions

The Identity cookie is re-validated against the stored user on each request: a deleted user, a changed security stamp
or an ended server-side session signs the user out. Roles are re-read per site. See
[identity-and-permissions.md](identity-and-permissions.md).

### Secrets

| Secret | Where it belongs |
|---|---|
| The installation's connection strings, installation id, switches set from admin pages | `appsettings.override.json` in the directory `STOMME_APPSETTINGS_PATH` names (the app's `Server/Data` when unset). Written by the installer; ignored by git. |
| Secrets you supply at deploy time (certificate passwords, client secrets) | Environment variables (`OpenIddict__SigningCertificatePassword`), or the override file on the server. Never `appsettings.json`, which is committed. |
| A credential a site's feature needs at runtime (a webhook token, an API key, a connection's password) | The site secret store, `Stomme.Infrastructure.Security.ISiteSecretStore`: protected with ASP.NET Data Protection, stored per site under a reference key; your aggregate stores only the key. Connections (**Admin > Connections**) keep their credentials there. |
| Client secrets of the OpenID Connect provider | Hashed by the provider; never readable again. |

`ISiteSecretStore` (scoped, always registered):

- `SetAsync(siteId, referenceKey, secret, ct)` - protect and store; replaces a previous value.
- `GetAsync(siteId, referenceKey, ct)` - returns `SiteSecretResult` with `Status` `Found`, `NotFound` or `Unreadable`;
  never throws for a missing or unreadable value, never logs the secret.
- `DeleteAsync(siteId, referenceKey, ct)` - idempotent.
- A reference key is lower case, dotted, at least two segments of `[a-z0-9]` joined by single hyphens, at most 200
  characters (`SiteSecretStore.IsValidReferenceKey`): `notes.webhook.token`, `notes.channel.{id:N}.api-key`. A
  malformed key throws `ArgumentException`. The settings API refuses these rows to every caller, the Host included.

### The Data Protection key ring

The site secret store, the Identity cookie and the antiforgery tokens are all protected by ASP.NET Data Protection.
A key ring lost in a redeploy signs every user out and makes **every stored secret permanently unreadable** (`GetAsync`
answers `Unreadable`; an operator must re-enter each one). Stomme therefore persists the ring itself (since 10.73.2):
in `DataProtection:KeysPath` when set; else in ASP.NET Core's default per-user folder when keys are already there (an
existing installation keeps its ring); else in `DataProtection-Keys` beside the installation's override file, which a
deployment persists anyway. See below.

### Network egress

Server-side requests to a URL somebody typed must not reach the host's own loopback services, the private network or a
cloud metadata endpoint. Stomme's one guard is `Stomme.Infrastructure.Security.OutboundConnectGuard`:

- `CreateHandler()` - a `SocketsHttpHandler` that resolves the host itself, connects only to permitted addresses, and
  re-checks the address it actually connected to (so a DNS answer that changes between check and connect does not slip
  through). Redirects, proxy and cookies are off; the connect timeout is ten seconds.
- `CheckDestinationAsync(uri, allowedHosts, ct)` - the check to run when someone SAVES a URL; returns the refusal message,
  or `null` when allowed. Only absolute `http`/`https` URLs pass.
- Denied: loopback, RFC 1918, link-local (including `169.254.169.254`), IPv6 unique-local and link-local, multicast,
  unspecified and broadcast addresses, and the IPv4-mapped IPv6 forms of all of them.
- `IsDenial(exception)` tells a refusal from an ordinary network failure.

A host name on an **allowlist** may reach a private address. The allowlists are per site, and only a member of
`Host Users` may change them - widening a site's egress opens the installation's own network:

| Site setting | Used by |
|---|---|
| `ConnectivityAllowedHosts` | Connections and the AI assistant's model endpoint; set in **Admin > Connections** |
| `NotificationWebhookAllowedHosts` | The notification webhook channel |
| `McpClientAllowedHosts` | The MCP client (also the installation-wide `Mcp:Client:HostAllowlist`) |

Entries match a host exactly: a host name or a fully written IP address, no wildcards, ranges, schemes or ports.
Connectors use the site's egress through their own contract; see [../connectors/overview.md](../connectors/overview.md).

### The reverse proxy is part of the security boundary

Host names choose the alias, the tenant and the OpenID Connect issuer; the scheme decides whether the OpenID Connect
provider accepts a request; the client address (with the site) is the rate limiter's partition key. So the host believes
`X-Forwarded-For`, `X-Forwarded-Proto` and `X-Forwarded-Host` only from a proxy: by default a sender on loopback or a
private network (`127.0.0.0/8`, `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, `::1`, `fc00::/7`); a deployment
whose proxy is elsewhere names it in `ForwardedHeaders:KnownProxies` or `ForwardedHeaders:KnownNetworks`, and then
only what it names is believed. A caller from any other address is taken as it connected - its own host name, scheme
and address. Behind several proxies (Cloudflare, then a tunnel, then an edge proxy) the client's address is found by
walking `X-Forwarded-For` from the right past every trusted proxy to the first address that is not one; the entries to
its left are the caller's own and are never read. (Before 10.73.0 the host believed everyone: an app reached directly let a caller choose the issuer, pass
the provider's https requirement with `X-Forwarded-Proto: https`, escape the rate limiter with a new
`X-Forwarded-For` per request, and add an alias row per new `X-Forwarded-Host` on a one-site installation.)

The proxy is still part of the boundary: it must overwrite all three headers with what it saw, and Kestrel's port must
not be published. Outside Development the host sends HSTS;
`Installation:UseHttpsRedirection` (default `false`) adds the HTTPS redirect when nothing in front of it does.

## Using it from an app

### What every module must do

1. **Authorize every endpoint.** `[RequiresPermissionKey(key)]` on every action (or on the controller), or
   `[Authorize(Roles = ...)]` / `[Authorize(Policy = ...)]`. An action with no authorization attribute is open to
   anonymous callers.
2. **Gate every command and query** with `[Requires(key)]` on the request type.
3. **Scope every row.** Take the site from `SiteState`, the tenant from `ITenantContext`, the user from `ICurrentUser`.
   Load by id, then compare `row.SiteId` with the request's site; answer another site's row as not found.
4. **Check per row** when access depends on the data (owner, status): `IAccessGuard.IsAllowedAsync(null, key, ct)`
   inside the handler. A draft a visitor may not see reads as not found, not forbidden.
5. **Never trust client input.** Ids, owner ids, site ids, `authmoduleid`, file names, URLs and HTML are input.
   Validate lengths and formats in the domain (throw `ArgumentException`, answer 400). Never let a request body choose
   whose row it writes. Store user text as text; never render it with `MarkupString`.
6. **Keep secrets out of settings and models.** Use `ISiteSecretStore`. Never return a secret from an API; return
   whether one is set.
7. **Guard outbound calls** to user-supplied URLs with `OutboundConnectGuard`; check at save and connect through its
   handler.
8. **Switch risky capabilities off by default** and gate them at the router (below).
9. **Log refusals** with `ILogManager.Log(LogLevel.Error, this, LogFunction.Security, ...)` (`Stomme.Shared.LogLevel`,
   `Stomme.Enums.LogFunction`), never with the secret or the full input.

The [stomme-secure-a-module](../../.claude/skills/stomme-secure-a-module/SKILL.md) skill applies all of this to a
module, file by file.

### A switch that removes a controller

A permission policy runs before the action, so an action body cannot turn a switched-off feature into 404 for a caller
without the key - that caller gets 403, which says the endpoint exists. Make the router find no action instead:

```csharp
using System;
using Stomme.Infrastructure;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;

namespace Stomme.Application.Modules.Notes.Controllers
{
    /// <summary>The Notes API exists only while Notes:Enabled is true: otherwise every route answers 404.</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public sealed class NotesEnabledAttribute : ActionMethodSelectorAttribute
    {
        public override bool IsValidForRequest(RouteContext routeContext, ActionDescriptor action) =>
            HostConfiguration.Resolve(routeContext.HttpContext.RequestServices)?.GetValue("Notes:Enabled", false) == true;
    }
}
```

Put it in `Server/Modules/Notes/Controllers/NotesEnabledAttribute.cs` and `[NotesEnabled]` on the controller class.
The `[AttributeUsage]` line is required: `ActionMethodSelectorAttribute` is declared for methods only, so without it
the attribute on a class does not compile (CS0592). `Stomme.Infrastructure.HostConfiguration.Resolve` returns the
merged configuration (including `appsettings.override.json` and `features.json`); the plain `IConfiguration` in the
container lacks `features.json` (and, before 10.73.2, the override file). The value is read on every request, so editing the override file takes effect without a
restart. While the key is off, every route of the controller answers 404 to everyone, the Host included; while it is
on, the controller's own authorization applies. `Notes:Enabled` is this example's own key.

### Store and read a site secret

```csharp
#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure.Security;

namespace Stomme.Application.Modules.Notes.Application
{
    public sealed class NotesWebhookSecret(ISiteSecretStore secrets)
    {
        private const string ReferenceKey = "notes.webhook.token";

        public Task SaveAsync(int siteId, string token, CancellationToken ct) =>
            secrets.SetAsync(siteId, ReferenceKey, token, ct);

        /// <summary>The token, or null when none is stored or it can no longer be read (the key ring changed).</summary>
        public async Task<string?> ReadAsync(int siteId, CancellationToken ct)
        {
            var result = await secrets.GetAsync(siteId, ReferenceKey, ct);
            return result.IsFound ? result.Secret : null;
        }

        public Task ForgetAsync(int siteId, CancellationToken ct) =>
            secrets.DeleteAsync(siteId, ReferenceKey, ct);
    }
}
```

Put it in `Server/Modules/Notes/Application/NotesWebhookSecret.cs` and register it in
`NotesServerStartup.ConfigureServices`: `services.AddScoped<NotesWebhookSecret>();`. Take the site id from the
request (`SiteState.Alias.SiteId`), never from the client. Only code that has already checked the caller's permission
may call `SaveAsync`. The value is stored encrypted as a private site setting under the reference key; the settings
API (`api/Setting`) does not list it, even to the Host.

### Call a URL a user typed

```csharp
#nullable enable
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure.Security;

namespace Stomme.Application.Modules.Notes.Application
{
    public static class NotesWebhook
    {
        /// <summary>The refusal to show when the URL is saved, or null when it may be used.</summary>
        public static Task<string?> CheckAsync(Uri url, CancellationToken ct) =>
            OutboundConnectGuard.CheckDestinationAsync(url, Array.Empty<string>(), ct);

        /// <summary>Posts through the guard: the address is checked again where the socket connects.</summary>
        public static async Task<bool> PostAsync(Uri url, HttpContent body, CancellationToken ct)
        {
            using var client = new HttpClient(OutboundConnectGuard.CreateHandler()) { Timeout = TimeSpan.FromSeconds(30) };
            try
            {
                using var response = await client.PostAsync(url, body, ct);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex) when (OutboundConnectGuard.IsDenial(ex))
            {
                return false; // a private or loopback address: refused by policy, not a network failure
            }
        }
    }
}
```

`CheckAsync` answers, for example, "The destination '127.0.0.1' is not permitted: it resolves to a loopback, private,
link-local or otherwise internal address." for `http://127.0.0.1:8080/x`, `http://169.254.169.254/...` and
`http://10.0.0.5/`, "The URL must be an absolute http or https URL." for `ftp://...`, and `null` for a public host. It
resolves the host name to decide, so it needs DNS.

### Persist the Data Protection key ring

Nothing to write: the host chooses the folder as above, and creates it. What remains is the deployment's:

- **Persist the folder.** With the default choice it is `DataProtection-Keys` beside `appsettings.override.json`
  (`Server/Data` in this app), which a deployment keeps anyway. A container names a volume in
  `DataProtection__KeysPath` ([deploy.md](../operations/deploy.md#data-protection-keys)).
- **Restrict access to it** like the override file: the `key-{guid}.xml` files in it are the keys, unencrypted.
- **Several instances share one folder** and one `DataProtection:ApplicationName`.
- **Set `DataProtection:ApplicationName` only before the first secret is stored**, and only if the app will run from a
  different folder later (ASP.NET Core otherwise isolates the ring per content-root path). Setting or changing it on an
  existing installation signs everyone out and leaves secrets stored under the old name unreadable.

Driven in Stomme's own tests: a site secret stored, the host stopped, the ring found in the named folder, the host
started again and the secret read back. An app made before 10.73.2 may carry its own startup that persists the ring;
it still works (it registers after Stomme's and wins) and can be removed if it used the same folder.
### Rate limiting your own endpoint

The host runs ASP.NET Core's rate limiter after authentication. A module adds a named policy to the same options from
its startup (`services.AddRateLimiter(options => options.AddPolicy(...))`) and puts `[EnableRateLimiting("name")]` on
the action. The host answers a refusal with 429. Not driven by the authors of this page.

## Configuration

| Key | Default | Effect |
|---|---|---|
| `Installation:UseHttpsRedirection` | `false` | Redirect HTTP to HTTPS in the app |
| `DataProtection:KeysPath` | (see above) | Folder the Data Protection key ring is kept in |
| `DataProtection:ApplicationName` | (unset) | Fixed application name for the key ring; set before the first install only |
| `Mcp:Client:HostAllowlist` | `[]` | Installation-wide hosts the MCP client may reach on a private address |
| `RateLimiting:Account:*` | see [identity-and-permissions.md](identity-and-permissions.md) | Account endpoint limits |

## Rules and pitfalls

- **Non-private settings of your own entity are readable by any signed-in user.** The settings API (`api/Setting`)
  refuses an anonymous read of a custom `EntityName` with 403 (since 10.73.2; before, anyone could read it), and answers
  any signed-in caller: `GET api/Setting?entityName=NotesConfig&entityId=-1` with a plain user's cookie returns every
  non-private row. A value an anonymous page needs goes through an endpoint of your own. `IsPrivate = true` hides a row from everyone but administrators (`Host Users`
  for a `master:` name) and holders of an `Edit` permission row for that entity and id (a read by id answers 204).
  Writing a custom entity's row needs `Administrators` for
  `entityId` `-1` (`Host Users` when the name starts with `master:`) and a matching permission row for any other id.
  Non-private `Site` settings are public the same way. Never keep a secret in a setting - use `ISiteSecretStore`.
- **Redeploying without the key ring's folder loses every stored secret.** Persist it (see above) before going live.
- **`[AllowAnonymous]` on a controller class disables every `[RequiresPermissionKey]` in it.** Put `[AllowAnonymous]` on
  the single action that needs it, and grant anonymous access through a key held by `All Users` instead where you can.
- **A check in the component is not a check.** `IsAuthorizedPermission` hides controls; the server decides.
- **Do not change state on `GET`.** Antiforgery does not cover it.
- **Do not open a socket or an `HttpClient` of your own to a user-supplied address.** Use the guard's handler; a client
  without it reaches `127.0.0.1` and the cloud metadata endpoint.
- **Do not log secrets or whole request bodies.** Logs are kept and shown in **Admin > Event Log** (Host only).
- **Expose the app only behind its proxy, and name a proxy outside the private networks.** Forwarded headers are believed from loopback and the private networks, or only from the proxies `ForwardedHeaders:KnownProxies` / `KnownNetworks` name.
- **Do not trust a `returnurl` because it starts with `/`.** `//other.example/x` and `/\other.example/x` start with
  `/` and leave the site. The framework's own sign-in pages refuse them since 10.73.0 and send the person home. In your
  own code pass any return address through `Utilities.LocalReturnUrl(returnUrl)` (`Stomme.Shared`, the rules of
  ASP.NET Core's `Url.IsLocalUrl`, except that a `~/` path is refused too) before redirecting to it.

## Related

- [tenancy.md](tenancy.md) - tenant and site isolation
- [identity-and-permissions.md](identity-and-permissions.md) - permission keys and where they are checked
- [authentication-options.md](authentication-options.md) - external logins and the OpenID Connect provider
- [../architecture/configuration.md](../architecture/configuration.md) - every switch, the override file
- [../operations/deploy.md](../operations/deploy.md) - persistent data, reverse proxies
- [../connectors/overview.md](../connectors/overview.md) - connections, vaulted credentials, connector egress
- [../../.claude/skills/stomme-secure-a-module/SKILL.md](../../.claude/skills/stomme-secure-a-module/SKILL.md)
