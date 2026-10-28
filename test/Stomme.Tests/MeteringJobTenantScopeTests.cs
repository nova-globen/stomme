using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Tenancy;
using Stomme.Models;
using Stomme.Modules.Metering.Application;
using Stomme.Modules.Metering.Domain;
using Stomme.Modules.Metering.Infrastructure;
using Stomme.Metering.Persistence;
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
    /// D102 binding item 1: a module-seam job binds the TENANT itself, so it is correct no matter what scope its
    /// caller hands it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>CORRECTED 2026-09-20 - the original reason given here was already false when it was written.</b> It said
    /// the job must scope itself "because <c>HostedServiceBase</c> does not". The base class DOES: BL-107 landed in
    /// <c>ff19714</c> on 2026-09-17, before this module existed, and <c>RunTenantsAsync</c> now enters
    /// <c>ITenantScopeFactory.BeginScope</c> AND a fresh DI scope per tenant, with a per-tenant catch. That is
    /// pinned by its own gate, <c>HostedServiceBaseTenantIsolationTests</c>. Leaving the old sentence standing would
    /// have told the next reader the framework is broken when it is not.
    /// </para>
    /// <para>
    /// What this gate proves is therefore narrower and still worth having: the job does not TRUST its caller. It
    /// enters the tenant's ambient scope itself before creating any DI scope, so it lands the right tenant's rows
    /// even when invoked from somewhere that got the scope wrong - the shape the pre-BL-107 loop actually had, and
    /// the shape any future non-job caller could still have. The underlying hazard is real and unchanged: the seam's
    /// <c>StommeTenantContext</c> caches the first alias's tenant per DI scope, so a module-seam read in a
    /// mis-scoped scope is silently filtered as the WRONG tenant rather than failing.
    /// </para>
    /// <para>
    /// <b>The arrangement is the point.</b> Two tenants share one physical database, so the tenant FILTER is the
    /// only thing separating their rows - and the job is then run for tenant two while an ambient scope for tenant
    /// ONE is deliberately held open around it. If the job did not open its own scope, its module reads would be
    /// filtered as tenant one, the pending row would be invisible, and it would silently fold nothing. Measured in
    /// exactly that shape: the assertions below fail when <c>BeginScope</c> is removed from the job.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class MeteringJobTenantScopeTests
    {
        private const string Host = "localhost";
        private const string SecondHost = "second.localhost";
        private const string Metric = "tests.scoped";

        private readonly ITestOutputHelper _output;

        public MeteringJobTenantScopeTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task A_job_folds_the_second_tenants_row_even_while_the_first_tenants_scope_is_open()
        {
            // Through appsettings.override.json - where a deployed install keeps it - and NOT builder.Configuration: the host's
            // IConfiguration does not include the override, and the jobs read the merged root (HostConfiguration, BL-162).
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host, new System.Text.Json.Nodes.JsonObject { ["Metering"] = new System.Text.Json.Nodes.JsonObject { ["Enabled"] = true } });
            var app = await GateDrive.InstallAsync(scratch, _output, configure: null, start: false);

            try
            {
                var firstAlias = await GateDrive.AliasAsync(app, Host);
                var siteId = firstAlias.SiteId;

                // ---- a SECOND tenant over the SAME database, with an alias onto the same site ------------------
                var (firstTenant, secondTenant) = await InScopeAsync(app, async sp =>
                {
                    var tenants = sp.GetRequiredService<ITenantRepository>();
                    var all = (await tenants.GetTenantsAsync()).ToList();
                    var master = all.Single(tenant => tenant.TenantId == firstAlias.TenantId);
                    var second = await tenants.AddTenantAsync(new Tenant
                    {
                        Name = "MeteringSecond",
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

                // ---- tenant TWO gets a quota and a ledger row left Pending, dated past the grace period --------
                var ledgerId = QuotaIds.ForUsage(secondId, siteId, Metric, Guid.NewGuid());
                await InTenantScopeAsync(app, secondId, async sp =>
                {
                    var quotas = sp.GetRequiredService<IQuotaRepository>();
                    await quotas.AddAsync(QuotaAggregate.Define(
                        QuotaIds.For(secondId, siteId, Metric, QuotaPeriod.Monthly), secondId, siteId, Metric, QuotaPeriod.Monthly, 100, 0, DateTime.UtcNow));

                    await sp.GetRequiredService<IUsageRecordRepository>().AddAsync(UsageRecordAggregate.Append(
                        ledgerId, secondId, siteId, Metric, 4, null, "tests", Guid.NewGuid(),
                        DateTime.UtcNow.AddMinutes(-(MeteringFoldRecoveryJob.GraceMinutes + 10))));

                    await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                    return true;
                });

                // Tenant ONE cannot see it - which is what makes the assertion below meaningful rather than a
                // coincidence of the two tenants sharing a database.
                var visibleToFirst = await InTenantScopeAsync(app, firstId, async sp =>
                    (await sp.GetRequiredService<IUsageRecordRepository>().ListBySiteAsync(siteId)).Count);
                _output.WriteLine($"[isolation] rows visible to tenant one: {visibleToFirst}");
                Assert.Equal(0, visibleToFirst);

                // ---- run the job for tenant TWO while tenant ONE's ambient scope is held open ------------------
                var job = new MeteringFoldRecoveryJob(app.Services.GetRequiredService<IServiceScopeFactory>());
                string log;
                using (app.Services.GetRequiredService<ITenantScopeFactory>().BeginScope(firstId.Value))
                using (var jobScope = app.Services.GetRequiredService<IServiceScopeFactory>().CreateScope())
                {
                    // What HostedServiceBase does for each tenant of a run: set the tenant on the ONE scope it owns.
                    jobScope.ServiceProvider.GetRequiredService<ITenantManager>().SetTenant(secondTenant);
                    log = await job.ExecuteJobAsync(jobScope.ServiceProvider);
                }

                _output.WriteLine($"[job] {log}");

                var folded = await InTenantScopeAsync(app, secondId, async sp =>
                {
                    var row = await sp.GetRequiredService<IUsageRecordRepository>().GetByIdAsync(ledgerId);
                    var quota = await sp.GetRequiredService<IQuotaRepository>().GetAsync(secondId, siteId, Metric, QuotaPeriod.Monthly);
                    return (State: row?.FoldState, Consumed: quota?.Consumed);
                });

                _output.WriteLine($"[after] fold state={folded.State} consumed={folded.Consumed}");
                Assert.Equal(FoldState.Applied, folded.State);
                Assert.Equal(4, folded.Consumed);

                // And nothing landed under tenant one.
                var strayedToFirst = await InTenantScopeAsync(app, firstId, async sp =>
                    (await sp.GetRequiredService<IUsageRecordRepository>().ListBySiteAsync(siteId)).Count
                    + (await sp.GetRequiredService<IQuotaRepository>().ListBySiteAsync(siteId)).Count);
                _output.WriteLine($"[isolation] rows under tenant one after the run: {strayedToFirst}");
                Assert.Equal(0, strayedToFirst);
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
        /// The sanctioned order, and the only one that works: the TENANT scope is entered BEFORE the DI scope, so
        /// the scoped tenant context resolves inside it rather than caching whatever was ambient before.
        /// </summary>
        private static async Task<T> InTenantScopeAsync<T>(WebApplication app, Tenancy.Domain.TenantId tenantId, Func<IServiceProvider, Task<T>> work)
        {
            using var tenantScope = app.Services.GetRequiredService<ITenantScopeFactory>().BeginScope(tenantId.Value);
            using var scope = app.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
            return await work(scope.ServiceProvider);
        }
    }
}
