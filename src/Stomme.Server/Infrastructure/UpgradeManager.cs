using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Stomme.Models;

namespace Stomme.Infrastructure
{
    public interface IUpgradeManager
    {
        void Upgrade(Tenant tenant, string version);
    }

    public class UpgradeManager : IUpgradeManager
    {
        /// <summary>
        /// Every version the switch below has a case for (BL-170). A backup taken before one of them cannot be restored after
        /// it - the step changed data that the restore would put back unchanged, and the step never runs again - so the
        /// restore's pre-flight refuses across it. <c>UpgradeStepVersionsTests</c> holds this list to the switch.
        /// </summary>
        public static readonly IReadOnlyList<string> StepVersions = System.Array.Empty<string>();

        private readonly IServiceScopeFactory _serviceScopeFactory;

        public UpgradeManager(IServiceScopeFactory serviceScopeFactory)
        {
            _serviceScopeFactory = serviceScopeFactory;
        }

        public void Upgrade(Tenant tenant, string version)
        {
            // core framework upgrade logic - executed for every tenant
            using (var scope = _serviceScopeFactory.CreateScope())
            {
                // set tenant
                var tenantManager = scope.ServiceProvider.GetRequiredService<ITenantManager>();
                tenantManager.SetTenant(tenant);

                // Version-specific upgrade steps are registered below as the framework evolves.
                // Stomme ships at its first version (10.2.1): a fresh install establishes the
                // current state directly from appsettings.json, the site templates, and the seed
                // path (SiteRepository), so there is no historical data/file migration to run.
                // Future releases add cases here, e.g.:
                //     case "10.3.0":
                //         Upgrade_10_3_0(tenant, scope);
                //         break;
                switch (version)
                {
                    default:
                        // No version-specific upgrade step registered yet (see comment above).
                        break;
                }
            }
        }
    }
}
