#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Health;
using Stomme.Health.Persistence;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Tenancy;
using Stomme.Models;
using Stomme.Modules.Health.Application;
using Stomme.Modules.Health.Domain;
using Stomme.Persistence.Abstractions;
using Stomme.Repository;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// Two tenants, ONE physical database, ONE site id — the arrangement that made the deterministic ids collide.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the gate for the defect found while writing <c>HealthIds</c>.</b> Site-per-alias over a shared
    /// database is a supported topology, and site ids are not unique across tenants in it, so a key of
    /// (scope, site, subject) names ONE row for two tenants: a primary-key collision in one table, after which
    /// the tenant query filter hides the survivor from at least one of them. <c>HealthDomainTests</c> pins that
    /// the ids differ; this pins that the ROWS actually land and read back per tenant, against a real store.
    /// </para>
    /// <para>
    /// <b>The arrangement is the point.</b> Tenant one's ambient scope is deliberately held OPEN around a pass
    /// run for tenant two — the shape the job loop really had before its tenant scope was fixed, and the shape any future non-job caller
    /// could still have. If the pass did not enter the tenant's own scope, its reads would be filtered as tenant
    /// one and it would write and read the wrong tenant's rows silently.
    /// </para>
    /// <para>
    /// <b>The metering module has the same latent id defect and is tracked separately</b>; this suite is the shape
    /// its gate should take.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class HealthJobTenantScopeTests
    {
        private const string Host = "localhost";
        private const string SecondHost = "second.localhost";

        private readonly ITestOutputHelper _output;

        public HealthJobTenantScopeTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task Two_tenants_over_one_database_and_one_site_keep_their_own_health_rows()
        {
            // Through appsettings.override.json - where a deployed install keeps it - and NOT builder.Configuration: the host's
            // IConfiguration does not include the override, and the jobs read the merged root (HostConfiguration).
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host, new System.Text.Json.Nodes.JsonObject { ["Health"] = new System.Text.Json.Nodes.JsonObject { ["Enabled"] = true } });
            var app = await GateDrive.InstallAsync(scratch, _output, configure: null, start: false);

            try
            {
                await GateDrive.StartNonJobHostedServicesAsync(app);

                var firstAlias = await GateDrive.AliasAsync(app, Host);
                var siteId = firstAlias.SiteId;

                // ---- a SECOND tenant over the SAME database, aliased onto the SAME site --------------------
                var (firstTenant, secondTenant) = await InScopeAsync(app, async sp =>
                {
                    var tenants = sp.GetRequiredService<ITenantRepository>();
                    var all = (await tenants.GetTenantsAsync()).ToList();
                    var master = all.Single(tenant => tenant.TenantId == firstAlias.TenantId);
                    var second = await tenants.AddTenantAsync(new Tenant
                    {
                        Name = "HealthSecond",
                        DBConnectionString = master.DBConnectionString,
                        DBType = master.DBType,
                        Version = master.Version
                    });

                    await sp.GetRequiredService<IAliasRepository>().AddAliasAsync(new Alias
                    {
                        Name = SecondHost,
                        TenantId = second.TenantId,
                        SiteId = siteId,
                        IsDefault = true
                    });

                    return (master, second);
                });

                var firstId = FrameworkTenantId.ToTenantIdOrNull(firstTenant.TenantId)!;
                var secondId = FrameworkTenantId.ToTenantIdOrNull(secondTenant.TenantId)!;
                _output.WriteLine($"[tenants] first={firstTenant.TenantId} second={secondTenant.TenantId}, both on site {siteId}");

                // The ids MUST differ even though scope, site and subject are identical.
                var subject = HealthSubject.For("stomme", "store");
                var firstRowId = HealthIds.ForAlertState(firstId, HealthProbeScope.Tenant, 0, subject);
                var secondRowId = HealthIds.ForAlertState(secondId, HealthProbeScope.Tenant, 0, subject);
                _output.WriteLine($"[ids] first={firstRowId} second={secondRowId}");
                Assert.NotEqual(firstRowId, secondRowId);

                // ---- a pass for tenant ONE -----------------------------------------------------------------
                await InTenantScopeAsync(app, firstId, sp =>
                    sp.GetRequiredService<HealthProbePass>().RunAsync(firstId, HealthProbeScope.Tenant, 0));

                // ---- a pass for tenant TWO, while tenant ONE's ambient scope is HELD OPEN -------------------
                using (app.Services.GetRequiredService<ITenantScopeFactory>().BeginScope(firstId.Value))
                {
                    await InTenantScopeAsync(app, secondId, sp =>
                        sp.GetRequiredService<HealthProbePass>().RunAsync(secondId, HealthProbeScope.Tenant, 0));
                }

                // ---- each tenant sees exactly ONE row, and it is ITS OWN ------------------------------------
                var first = await InTenantScopeAsync(app, firstId, sp =>
                    sp.GetRequiredService<IHealthRollupRepository>().ListAsync(HealthProbeScope.Tenant, 0, HealthRollupWindow.Minute));

                var second = await InTenantScopeAsync(app, secondId, sp =>
                    sp.GetRequiredService<IHealthRollupRepository>().ListAsync(HealthProbeScope.Tenant, 0, HealthRollupWindow.Minute));

                _output.WriteLine($"[rows] tenant one: {first.Count}; tenant two: {second.Count}");

                var firstRow = Assert.Single(first);
                var secondRow = Assert.Single(second);

                // Different rows, each STAMPED with its own tenant - not one row shared, and not one hidden.
                Assert.NotEqual(firstRow.Id, secondRow.Id);
                Assert.Equal(firstId, firstRow.TenantId);
                Assert.Equal(secondId, secondRow.TenantId);
                Assert.Equal(1, firstRow.Checks);
                Assert.Equal(1, secondRow.Checks);

                // And the alert rows are separate too.
                var firstAlerts = await InTenantScopeAsync(app, firstId, sp =>
                    sp.GetRequiredService<IHealthAlertStateRepository>().ListBySiteAsync(0));
                var secondAlerts = await InTenantScopeAsync(app, secondId, sp =>
                    sp.GetRequiredService<IHealthAlertStateRepository>().ListBySiteAsync(0));

                _output.WriteLine($"[alerts] tenant one: {firstAlerts.Count}; tenant two: {secondAlerts.Count}");
                Assert.NotEqual(Assert.Single(firstAlerts).Id, Assert.Single(secondAlerts).Id);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static async Task<T> InScopeAsync<T>(WebApplication app, Func<IServiceProvider, Task<T>> work)
        {
            using var scope = app.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
            return await work(scope.ServiceProvider);
        }

        /// <summary>
        /// The sanctioned order: the TENANT scope is entered BEFORE the DI scope, so the scoped tenant context
        /// resolves inside it rather than caching whatever was ambient before.
        /// </summary>
        private static async Task<T> InTenantScopeAsync<T>(WebApplication app, Tenancy.Domain.TenantId tenantId, Func<IServiceProvider, Task<T>> work)
        {
            using var tenantScope = app.Services.GetRequiredService<ITenantScopeFactory>().BeginScope(tenantId.Value);
            using var scope = app.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
            return await work(scope.ServiceProvider);
        }
    }
}
