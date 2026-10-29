# Tenancy

One Nova Globen Stomme installation serves many sites. A request is matched by its host name and path to an **alias**, the alias
names a **site**, and the site belongs to a **tenant**, which is a database. Every piece of data an app keeps belongs to
exactly one of three scopes - the installation, a tenant, or a site - and code that reads or writes data must know which.

## How it works

### The three levels

- **Alias** (`Stomme.Models.Alias`): a URL without its scheme - `example.com`, `www.example.com` or
  `example.com/products`. It carries `AliasId`, `Name`, `TenantId`, `SiteId` and `IsDefault`. `Path` is the part after
  the first `/` (empty for a bare host). `SiteKey` is `"{TenantId}:{SiteId}"`, the key the framework uses to tell sites
  apart inside one process (cache entries, the sign-in cookie, file paths). A request to an alias that is not the site's
  default alias is redirected to the default one.
- **Site** (`Stomme.Models.Site`): pages, modules, users' roles on that site, permissions, settings, files and folders.
  One tenant can hold many sites.
- **Tenant** (`Stomme.Models.Tenant`): `TenantId`, `Name`, `DBConnectionString` and `DBType`. `DBConnectionString`
  holds the NAME of a connection string in configuration (`ConnectionStrings:{name}`), not the string itself. Two
  tenants can share one database, or each can have its own.

### Resolving a request

1. The tenant middleware asks `Stomme.Infrastructure.ITenantManager.GetAliasAsync()` for the request's alias. It
   matches `Request.Host + Request.Path` against the alias table (cached).
2. The alias is stored on the scoped `Stomme.Shared.SiteState` (`SiteState.Alias`) and on the request
   (`HttpContext.GetAlias()`, in `Stomme.Extensions`).
3. The tenant named by the alias is resolved and remembered for the scope, so every database context opened later in the
   request is routed to that tenant's database.
4. When no alias matches: an uninstalled host runs the installer. An installed host answers a plain 404 with an empty
   body and writes nothing - only configured host names are aliases (`Installation:DefaultAlias` at install, then
   **Admin > Site Management**). The health endpoints are the exception: they answer for any host.

### What lives where

| Scope | What | Where it is stored |
|---|---|---|
| Installation | Tenants, aliases, scheduled jobs, module definitions, themes, `Host` settings | The master database (the installation's `DefaultConnection`) |
| Installation | Storage-blind module aggregates | One shared module store per installation, separated by tenant id (see below) |
| Installation | Configuration switches (`OpenIddict:Enabled`, `Saml2:Enabled`, ...) | `appsettings.json`, environment variables, `appsettings.override.json` |
| Tenant | User accounts (`Stomme.Models.User`, the ASP.NET Identity user), the roles `All Users`, `Unauthenticated Users` and `Host Users` | The tenant's database |
| Site | Pages, modules on pages, the roles `Registered Users` and `Administrators` and any custom role, a user's role memberships, permission rows, `Site` settings, files and folders, the per-site external login | The tenant's database, keyed by `SiteId` |

A user account belongs to a tenant, but a password sign-in on a site succeeds only when the account holds
`Registered Users` on THAT site (a `Host Users` member, whose role has no site, signs in everywhere). An account that
is registered on site A and not on site B is refused at B's login page. What an account may do differs per site,
because roles are granted per site.

### The ambient tenant for application code

Application code that stores tenant-owned data reads the current tenant through
`Stomme.Tenancy.Abstractions.ITenantContext` (scoped):

- `CurrentTenantId` - a `Stomme.Tenancy.Domain.TenantId`, or `null` when no tenant is in scope.
- `IsSystemScope` - `true` only inside an explicit system scope.

The host's implementation resolves the tenant in this order: an explicit scope opened with
`Stomme.Persistence.Abstractions.ITenantScopeFactory.BeginScope(Guid?)` (a singleton), then `SiteState.Alias`, then
the request's alias. When none yields a tenant it reports "no tenant, not system scope", and the tenant filter on
tenant-owned data then matches nothing. That is the fail-closed default: code that runs without an alias reads no
tenant's rows, never every tenant's rows. System scope (`BeginScope(null)`) is entered only on purpose.

`TenantId` is a value object over a `Guid`. Framework tenant `1` maps to `TenantId.Default`
(`00000000-0000-0000-0000-000000000001`).

### Tenant isolation is the store's job; site isolation is yours

An aggregate that implements `Stomme.Persistence.Abstractions.ITenantOwned` (`TenantId TenantId { get; }`) is
filtered by tenant on every provider: a read returns only the current tenant's rows. The store does NOT filter by
site. A site id repeats inside a tenant's data, and `GetByIdAsync` returns another site's row of the same tenant if you
pass its id. Every module aggregate that belongs to a site must carry a `SiteId`, and every read and write must check it
against the request's site. See [identity-and-permissions.md](identity-and-permissions.md) and the
[stomme-secure-a-module](../../.claude/skills/stomme-secure-a-module/SKILL.md) skill.

## Using it from an app

### Read the request's site and tenant on the server

`SiteState` and `ITenantContext` are scoped services. Inject them into a handler, a server-side service or a controller.
Refuse to write when either is missing; never fall back to a guess.

```csharp
#nullable enable
using System;
using Stomme.Shared;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;

namespace Stomme.Application.Modules.Notes.Application
{
    /// <summary>The request's tenant and site. A request with neither is refused, never written to a guess.</summary>
    internal static class NotesScope
    {
        public static int Site(SiteState siteState) =>
            siteState.Alias?.SiteId is int siteId && siteId > 0
                ? siteId
                : throw new InvalidOperationException("Notes belong to the request's site; none is resolved.");

        public static (TenantId TenantId, int SiteId) Resolve(ITenantContext tenantContext, SiteState siteState) =>
            (tenantContext.CurrentTenantId
                ?? throw new InvalidOperationException("Notes belong to a tenant; none is resolved."),
             Site(siteState));
    }
}
```

This is the scope helper of the Notes module the
[stomme-create-module](../../.claude/skills/stomme-create-module/SKILL.md) skill builds; there it sits beside the
handlers in `Server/Modules/Notes/Application/NoteRequests.cs` (with two more members, the per-row check and the
mapping to `NoteModel`). The Server project references the `Stomme.Server` package, which brings
`Stomme.Tenancy.Abstractions`, `Stomme.Tenancy.Domain` and `Stomme.Shared`.

### Mark an aggregate tenant-owned and stamp it

```csharp
#nullable enable
using System;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Application.Modules.Notes.Domain
{
    public sealed class Note : AggregateRoot<Guid>, ITenantOwned
    {
        // Every store builds the aggregate through this constructor and then sets the properties. Not for application code.
        private Note()
        {
            TenantId = null!;
            Title = string.Empty;
        }

        public TenantId TenantId { get; private set; }

        public int SiteId { get; private set; }

        public string Title { get; private set; }

        public static Note Create(Guid id, TenantId tenantId, int siteId, string title)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), "A note belongs to a site.");
            }

            return new Note { Id = id, TenantId = tenantId, SiteId = siteId, Title = (title ?? string.Empty).Trim() };
        }
    }
}
```

This is a shortened `Note` that shows only the tenancy part; the real one (`Server/Modules/Notes/Domain/Note.cs`, with
its author, body, timestamps, validation and domain event) is in the
[stomme-create-module](../../.claude/skills/stomme-create-module/SKILL.md) skill. The handler that creates a
`Note` passes the pair from `NotesScope.Resolve(tenantContext, siteState)`. How the aggregate is
registered and stored is in [../persistence/module-persistence.md](../persistence/module-persistence.md).

### Work that has no request

A background job has no `HttpContext` and no alias. It must enter each site explicitly before it touches site data:
`ITenantManager.SetAliasAsync(tenantId, siteId)` (or `SetAliasAsync(Alias)`) sets `SiteState.Alias` and routes the scope
to that tenant. `ITenantContext` follows the alias when it is set later in the scope. The job rules - one scope per
site, the cache that is keyed by alias - are in [../services/jobs.md](../services/jobs.md).

### Adding a site or a tenant

The Host adds sites in **Admin > Site Management** (`/admin/sites`) > **Add Site**: a **Site Name**, a **Url** (the
alias; it is pre-filled with a virtual-folder alias, `{host}/sitename` - type a host name such as `shop.example.com`
for a site of its own), a **Site Template** (`Default Site Template` or `Empty Site Template`) and a **Database**. The
Database list offers the existing tenants (the first install's tenant is `Master`) and `<Create Database>`, which
creates a new tenant with its own database. A site added on an existing tenant shares that tenant's database and its
user accounts. On RavenDB a new tenant's database must live on the installation's own RavenDB server.

The same sites can come from configuration instead, for a host deployed unattended: `Installation:Sites` lists them
(name, aliases, and a tenant with its own connection string for a new tenant), and each start creates any that are
missing. See [More sites from configuration](../operations/install-run-upgrade.md#more-sites-from-configuration-installationsites).

An unknown host name never becomes an alias: add every host name a site answers to (`www.` and the bare name, a reverse
proxy's public name) as an alias, or requests to it get a 404.

## Rules and pitfalls

- **Check the site on every row.** The tenant filter keeps tenants apart; nothing keeps sites apart except your code.
  A handler that loads a row by id and does not compare `row.SiteId` with the request's site lets a user on site A read
  or change site B's row by guessing its id. Treat another site's row exactly like a missing one (answer 404, not 403),
  so ids of other sites cannot be probed.
- **Do not trust a site id or tenant id from the client.** Take them from `SiteState` and `ITenantContext` on the
  server. A `siteId` in a request body or query string is input to validate, never the scope to write to.
- **An aggregate without `ITenantOwned` is shared by every tenant** of the installation's module store. The host logs a
  warning naming it at startup. Make it tenant-owned.
- **No tenant is not "all tenants".** When `CurrentTenantId` is `null` outside a system scope, tenant-owned reads return
  nothing. If a job "sees no data", it has not entered a site.
- **A sign-in belongs to one site.** The sign-in cookie carries the site key it was issued for; on another site of the
  same tenant the user's roles are re-read for that site. Never cache "is administrator" across sites.
- **Host names decide the tenant.** `X-Forwarded-Host` is believed only from a proxy (see
  [security-posture.md](security-posture.md)). A request whose host name matches no alias gets a plain 404 and writes
  nothing (since 10.74.0; it used to become a new alias of a one-site installation); `/healthz` and `/readyz` answer for
  any host.
- **A site in a folder is its own OpenID Connect authority, at its folder** (since 10.76.0; before, it had none): issuer
  `https://example.com/shop`, endpoints `/shop/connect/*`, its own users, and its API accepts only its own tokens. See
  [authentication-options.md](authentication-options.md).
- **A site in a folder keeps the framework's cookies to itself** (since 10.75.0). The sign-in cookies, the antiforgery
  cookie and the release marker it sets are named `<name>-<folder>` and scoped to `Path=/<folder>`, and a same-named
  cookie another site on the host set at a wider path is never read there - so a sign-in at `example.com` is not a
  sign-in at `example.com/shop`, and the reverse. The culture cookie keeps its name and takes the folder's path
  (`Utilities.SitePath(alias.Path)`). A module that sets a cookie of its own scopes it the same way.
- **A folder site's interactive circuit connects under the folder** (since 10.75.3): its page carries
  `<html data-site-path="/shop">`, the framework's JS initializer points the circuit at `/shop/_blazor`, and the tenant
  middleware strips the folder before the hub. So the circuit resolves the folder site and carries the folder site's
  cookies: the interactive sign-in works, the page's interactive auth state is the folder site's user, and a sign-in at the
  root site never signs a folder page's circuit in. Before 10.75.3 the circuit connected at the host's `/_blazor`, which
  resolved the ROOT site: an interactive folder site's sign-in form answered 400, and a root sign-in reached the folder
  page's circuit. No site or top-level page may be named `_blazor`. The calls an interactive page's services make to the
  site's API from the server carry the folder site's cookies under its own names (since 10.75.2).

## Related

- [identity-and-permissions.md](identity-and-permissions.md) - users, roles, permission keys, checking access
- [security-posture.md](security-posture.md) - fail closed, secrets, egress
- [../persistence/module-persistence.md](../persistence/module-persistence.md) - storage-blind aggregates and `ITenantOwned`
- [../persistence/framework-data.md](../persistence/framework-data.md) - the framework's own data
- [../services/jobs.md](../services/jobs.md) - entering a site from a background job
- [../../.claude/skills/stomme-secure-a-module/SKILL.md](../../.claude/skills/stomme-secure-a-module/SKILL.md)
