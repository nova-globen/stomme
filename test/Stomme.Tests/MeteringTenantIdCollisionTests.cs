#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Tenancy;
using Stomme.Metering;
using Stomme.Metering.Persistence;
using Stomme.Models;
using Stomme.Modules.Metering.Domain;
using Stomme.Persistence.Abstractions;
using Stomme.Repository;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-151: two tenants sharing ONE database and ONE site must not share a metering row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect this pins was shipped, and was found by writing the equivalent ids for BL-83.</b>
    /// <c>QuotaIds</c> hashed only (site, metric, period), and site ids are NOT unique across tenants when two
    /// tenants share a physical database — site-per-alias is a supported topology, and
    /// <c>MeteringJobTenantScopeTests</c> already constructs exactly it. Both tenants' quotas for that site
    /// therefore hashed to the SAME Guid. In one shared table that is a primary-key collision: on EF the second
    /// write throws, and where the write is the idempotent GetById-then-Update the tenant query filter hides the
    /// other tenant's row, so the second tenant reads a MISS and overwrites.
    /// </para>
    /// <para>
    /// That suite stayed green only because its first tenant never defined a quota. This one makes BOTH tenants
    /// define the same metric on the same site, which is the shape that actually collides.
    /// </para>
    /// <para>
    /// <b>Validated by mutation:</b> removing the tenant from the key turns this red.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class MeteringTenantIdCollisionTests
    {
        private const string Host = "localhost";
        private const string SecondHost = "second.localhost";
        private const string Metric = "tests.collision";

        private readonly ITestOutputHelper _output;

        public MeteringTenantIdCollisionTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task Both_tenants_define_the_same_quota_on_the_same_site_and_get_their_own_row()
        {
            // Through appsettings.override.json - where a deployed install keeps it - and NOT builder.Configuration: the host's
            // IConfiguration does not include the override, and the jobs read the merged root (HostConfiguration, BL-162).
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host, new System.Text.Json.Nodes.JsonObject { ["Metering"] = new System.Text.Json.Nodes.JsonObject { ["Enabled"] = true } });
            var app = await GateDrive.InstallAsync(scratch, _output, configure: null, start: false);

            try
            {
                await GateDrive.StartNonJobHostedServicesAsync(app);

                var firstAlias = await GateDrive.AliasAsync(app, Host);
                var siteId = firstAlias.SiteId;

                var (firstTenant, secondTenant) = await InScopeAsync(app, async sp =>
                {
                    var tenants = sp.GetRequiredService<ITenantRepository>();
                    var master = (await tenants.GetTenantsAsync()).Single(tenant => tenant.TenantId == firstAlias.TenantId);
                    var second = await tenants.AddTenantAsync(new Tenant
                    {
                        Name = "MeteringCollisionSecond",
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
                _output.WriteLine($"[tenants] first={firstTenant.TenantId} second={secondTenant.TenantId}, BOTH on site {siteId}");

                // The ids must differ before anything is written - the whole fix, stated first.
                var firstQuotaId = QuotaIds.For(firstId, siteId, Metric, QuotaPeriod.Monthly);
                var secondQuotaId = QuotaIds.For(secondId, siteId, Metric, QuotaPeriod.Monthly);
                _output.WriteLine($"[ids] first={firstQuotaId} second={secondQuotaId}");
                Assert.NotEqual(firstQuotaId, secondQuotaId);

                // ---- BOTH tenants define the SAME metric on the SAME site ----------------------------------
                await DefineAsync(app, firstId, siteId, limit: 10);
                await DefineAsync(app, secondId, siteId, limit: 99);

                // ---- and each reads back ITS OWN, with its own limit ----------------------------------------
                var first = await InTenantScopeAsync(app, firstId, sp =>
                    sp.GetRequiredService<IQuotaRepository>().GetAsync(firstId, siteId, Metric, QuotaPeriod.Monthly));

                var second = await InTenantScopeAsync(app, secondId, sp =>
                    sp.GetRequiredService<IQuotaRepository>().GetAsync(secondId, siteId, Metric, QuotaPeriod.Monthly));

                _output.WriteLine($"[quotas] first: id={first?.Id} limit={first?.Limit}; second: id={second?.Id} limit={second?.Limit}");

                Assert.NotNull(first);
                Assert.NotNull(second);
                Assert.NotEqual(first!.Id, second!.Id);
                Assert.Equal(10, first.Limit);
                Assert.Equal(99, second.Limit);

                // Each row is stamped with the tenant that wrote it.
                Assert.Equal(firstId, first.TenantId);
                Assert.Equal(secondId, second.TenantId);

                // ---- and the LEDGER ids separate too, for the same reason ----------------------------------
                var operationId = Guid.NewGuid();
                var firstLedger = QuotaIds.ForUsage(firstId, siteId, Metric, operationId);
                var secondLedger = QuotaIds.ForUsage(secondId, siteId, Metric, operationId);
                _output.WriteLine($"[ledger ids] first={firstLedger} second={secondLedger}");
                Assert.NotEqual(firstLedger, secondLedger);

                var firstAi = QuotaIds.ForAiUsage(firstId, siteId, operationId);
                var secondAi = QuotaIds.ForAiUsage(secondId, siteId, operationId);
                Assert.NotEqual(firstAi, secondAi);

                // And the AI ROLLUP id, which the first pass of this fix MISSED - it lives on the aggregate
                // rather than in QuotaIds, and was only found by sweeping every deterministic id in the tree.
                var day = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);
                var firstRollup = AiUsageRollupAggregate.IdFor(firstId, siteId, day, "openai", "gpt", "agent");
                var secondRollup = AiUsageRollupAggregate.IdFor(secondId, siteId, day, "openai", "gpt", "agent");
                _output.WriteLine($"[ai rollup ids] first={firstRollup} second={secondRollup}");
                Assert.NotEqual(firstRollup, secondRollup);

                // Each tenant sees exactly ONE quota for the site - not the other's, and not a merged row.
                var firstAll = await InTenantScopeAsync(app, firstId, sp =>
                    sp.GetRequiredService<IQuotaRepository>().ListBySiteAsync(siteId));
                var secondAll = await InTenantScopeAsync(app, secondId, sp =>
                    sp.GetRequiredService<IQuotaRepository>().ListBySiteAsync(siteId));

                _output.WriteLine($"[visible] tenant one: {firstAll.Count}; tenant two: {secondAll.Count}");
                Assert.Single(firstAll);
                Assert.Single(secondAll);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static Task<bool> DefineAsync(WebApplication app, Tenancy.Domain.TenantId tenantId, int siteId, long limit) =>
            InTenantScopeAsync(app, tenantId, async sp =>
            {
                await sp.GetRequiredService<IQuotaRepository>().AddAsync(QuotaAggregate.Define(
                    QuotaIds.For(tenantId, siteId, Metric, QuotaPeriod.Monthly),
                    tenantId, siteId, Metric, QuotaPeriod.Monthly, limit, 0, DateTime.UtcNow));

                await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                return true;
            });

        private static async Task<T> InScopeAsync<T>(WebApplication app, Func<IServiceProvider, Task<T>> work)
        {
            using var scope = app.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
            return await work(scope.ServiceProvider);
        }

        private static async Task<T> InTenantScopeAsync<T>(WebApplication app, Tenancy.Domain.TenantId tenantId, Func<IServiceProvider, Task<T>> work)
        {
            using var tenantScope = app.Services.GetRequiredService<ITenantScopeFactory>().BeginScope(tenantId.Value);
            using var scope = app.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
            return await work(scope.ServiceProvider);
        }
    }
}
