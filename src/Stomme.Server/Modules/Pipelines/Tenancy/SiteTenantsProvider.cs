#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Tenancy;
using Stomme.Repository;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StommeTenantScopes = Stomme.Persistence.Abstractions.ITenantScopeFactory;

namespace Stomme.Modules.Pipelines.Tenancy
{
    /// <summary>
    /// The engine's tenants, listed from Stomme's sites: every non-deleted site of every Stomme tenant, each as
    /// an engine <see cref="Tenant"/> whose id is <see cref="EngineTenantIds.For"/> and whose name is the site's.
    /// The engine's tenant service asks this at start-up (and activates a per-tenant scope with the engine's
    /// recurring tasks for each), on <c>RefreshAsync</c>, and the bridge triggers a refresh when a request names a
    /// site the service does not know yet - so a site created after boot becomes an engine tenant on its first
    /// request. An uninstalled host has no tenants to list; the engine then runs as its default tenant.
    /// </summary>
    internal sealed class SiteTenantsProvider(IServiceScopeFactory scopes, ILogger<SiteTenantsProvider> logger) : ITenantsProvider
    {
        public async Task<IEnumerable<Tenant>> ListAsync(CancellationToken cancellationToken = default)
        {
            var result = new List<Tenant>();
            try
            {
                using var root = scopes.CreateScope();
                if (!root.ServiceProvider.GetRequiredService<IConfigManager>().IsInstalled())
                {
                    return result;
                }

                var tenants = (await root.ServiceProvider.GetRequiredService<ITenantRepository>().GetTenantsAsync()).ToList();
                foreach (var tenant in tenants)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var tenantId = FrameworkTenantId.ToTenantIdOrNull(tenant.TenantId);
                    if (tenantId is null)
                    {
                        continue;
                    }

                    // The same shape the per-tenant jobs use: the Stomme tenant scope, then a listing scope routed at
                    // the tenant, so the site repository reads that tenant's store.
                    using var tenantScope = root.ServiceProvider.GetRequiredService<StommeTenantScopes>().BeginScope(tenantId.Value);
                    using var listing = scopes.CreateScope();
                    listing.ServiceProvider.GetRequiredService<ITenantManager>().SetTenant(tenant);
                    var sites = await listing.ServiceProvider.GetRequiredService<ISiteRepository>().GetSitesAsync();
                    foreach (var site in sites.Where(site => !site.IsDeleted))
                    {
                        result.Add(new Tenant { Id = EngineTenantIds.For(tenant.TenantId, site.SiteId), Name = site.Name });
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The engine's tenant service treats an empty list as "the default tenant only"; a listing that fails
                // (a store not yet migrated, a database briefly away) must not take the engine down with it.
                logger.LogError(ex, "The engine's site tenants could not be listed; the engine keeps its current tenants.");
            }

            return result;
        }

        public async Task<Tenant?> FindAsync(TenantFilter filter, CancellationToken cancellationToken = default)
        {
            var tenants = await ListAsync(cancellationToken);
            return filter.Apply(tenants.AsQueryable()).FirstOrDefault();
        }
    }
}
