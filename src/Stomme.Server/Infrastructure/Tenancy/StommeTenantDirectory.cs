#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Repository;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;

namespace Stomme.Infrastructure.Tenancy
{
    /// <summary>
    /// The seam's <see cref="ITenantDirectory"/> over Stomme's master tenant store. Reads through
    /// <see cref="ITenantRepository"/>, which caches the tenant list, so the per-tenant startup work this
    /// exists for costs one query rather than one per tenant.
    /// </summary>
    public sealed class StommeTenantDirectory : ITenantDirectory
    {
        private readonly ITenantRepository _tenantRepository;
        private readonly IConfigManager _config;

        public StommeTenantDirectory(ITenantRepository tenantRepository, IConfigManager config)
        {
            _tenantRepository = tenantRepository;
            _config = config;
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<TenantId>> GetTenantIdsAsync()
        {
            // Gate on install state, the same IConfigManager.IsInstalled() gate every other boot path uses
            // (TenantMiddleware, HostedServiceBase). Pre-install there is no configured MasterDBContext, so
            // reaching through the tenant repository runs a query against an unconfigured context routed through
            // FusionCache, which logs a warn for the failing factory even though the only caller
            // (FeatureSeeder.ResolveTenantsAsync) expects and handles this boot state. Throw the same "master not
            // ready" signal directly — cheaply and WITHOUT the FusionCache noise — which FeatureSeeder catches and
            // defers on, seeding nothing pre-install. Returning an EMPTY list instead is read by the seeder as the
            // installed-but-no-tenants case and seeds TenantId.Default, turning flags ON before any install ran
            // (caught by FeatureSeederPreInstallTests / FeatureSeedingLifecycleTests). A not-yet-provisioned master's
            // own behaviour IS a throw, not an empty list — this reproduces it silently.
            if (!_config.IsInstalled())
            {
                throw new InvalidOperationException("The master store is not installed yet (pre-install boot).");
            }

            return (await _tenantRepository.GetTenantsAsync())
                // Ordered by the framework key so seeding is deterministic and the master tenant (1) goes
                // first. Non-positive keys are sentinels, not tenants (see FrameworkTenantId).
                .Where(tenant => tenant.TenantId > 0)
                .OrderBy(tenant => tenant.TenantId)
                .Select(tenant => FrameworkTenantId.ToTenantId(tenant.TenantId))
                .ToList();
        }
    }
}
