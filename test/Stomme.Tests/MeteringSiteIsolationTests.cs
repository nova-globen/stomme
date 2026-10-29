using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Modules.Metering.Domain;
using Stomme.Metering.Persistence;
using Stomme.Repository;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// D102 item 8, binding item 8: metering is metered per SITE, and a by-id handler cannot reach another site's
    /// quota - not even inside the same tenant, where the rows share one physical store.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The dangerous case is precisely the one a tenant filter does NOT catch: two sites of the SAME tenant. Their
    /// rows live in one database under one tenant id, so the only thing separating them is the `SiteId` the
    /// repository filters on and the `GetForCurrentSiteAsync` check every by-id handler goes through. A read that
    /// merely confirmed the id EXISTS would turn the id space into an oracle - which is why the refusal must be
    /// the SAME answer as an unknown id, and why that is asserted here rather than assumed.
    /// </para>
    /// <para>
    /// PostgreSQL and RavenDB - one relational, one document, both FAIL-NOT-SKIP when unreachable. SQLite is
    /// deliberately absent: the claim is about the query the provider runs.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class MeteringSiteIsolationTests
    {
        private const string HostA = "localhost";
        private const string HostB = "meteringb.localhost";
        private const string Metric = "tests.isolated";

        private readonly ITestOutputHelper _output;

        public MeteringSiteIsolationTests(ITestOutputHelper output) => _output = output;

        public static TheoryData<string> Stores() => new() { PostgresGateStore.StoreName, RavenGateStore.StoreName };

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task One_sites_operator_cannot_read_or_touch_another_sites_quota(string storeName)
        {
            var store = LiftedRequiresGateTests.ResolveStore(storeName);
            Assert.True(store.IsReachable(), store.UnreachableMessage);

            using var scratch = new ScratchGateInstall(store, HostA, colocateModuleAggregates: storeName == PostgresGateStore.StoreName);
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                var aliasA = await GateDrive.AliasAsync(app, HostA);
                var siteB = await CreateSecondSiteAsync(app);
                _output.WriteLine($"[sites] A={aliasA.SiteId} B={siteB.SiteId} in tenant {aliasA.TenantId}");

                // A quota and a ledger row on EACH site, under the same tenant.
                var quotaB = await GateDrive.InTenantAsync(app, HostA, async (sp, alias) =>
                {
                    var tenantId = sp.GetRequiredService<ITenantContext>().CurrentTenantId!;
                    var quotas = sp.GetRequiredService<IQuotaRepository>();
                    var ledger = sp.GetRequiredService<IUsageRecordRepository>();

                    foreach (var siteId in new[] { alias.SiteId, siteB.SiteId })
                    {
                        await quotas.AddAsync(QuotaAggregate.Define(
                            QuotaIds.For(tenantId, siteId, Metric, QuotaPeriod.Monthly), tenantId, siteId, Metric, QuotaPeriod.Monthly, 10, 0, DateTime.UtcNow));

                        var row = UsageRecordAggregate.Append(
                            QuotaIds.ForUsage(tenantId, siteId, Metric, Guid.NewGuid()), tenantId, siteId, Metric, siteId == alias.SiteId ? 1 : 9, null, "tests",
                            Guid.NewGuid(), DateTime.UtcNow);
                        row.MarkApplied();
                        await ledger.AddAsync(row);
                    }

                    await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                    return QuotaIds.For(tenantId, siteB.SiteId, Metric, QuotaPeriod.Monthly);
                });

                // ---- the repository half: site A's reads see only site A --------------------------------------
                var readFromA = await GateDrive.InTenantAsync(app, HostA, async (sp, alias) =>
                {
                    var quotas = sp.GetRequiredService<IQuotaRepository>();
                    return (
                        Listed: (await quotas.ListBySiteAsync(alias.SiteId)).Count,
                        Usage: (await sp.GetRequiredService<IUsageRecordRepository>().ListBySiteAsync(alias.SiteId)).Sum(row => row.Quantity),
                        ByIdForeign: await quotas.GetForCurrentSiteAsync(quotaB, alias.SiteId),
                        ByIdUnknown: await quotas.GetForCurrentSiteAsync(Guid.NewGuid(), alias.SiteId));
                });

                _output.WriteLine($"[{storeName}] site A sees {readFromA.Listed} quota(s), {readFromA.Usage} unit(s); " +
                                  $"foreign by-id => {(readFromA.ByIdForeign is null ? "null" : "LEAKED")}; unknown by-id => {(readFromA.ByIdUnknown is null ? "null" : "LEAKED")}");

                Assert.Equal(1, readFromA.Listed);
                Assert.Equal(1, readFromA.Usage);

                // The SAME answer for a foreign id and an unknown one: that identity is the isolation claim.
                Assert.Null(readFromA.ByIdForeign);
                Assert.Null(readFromA.ByIdUnknown);

                // ---- the HTTP half: an operator on site A is refused site B's quota by id ----------------------
                await GateDrive.EnsureRoleAsync(app, HostA, RoleNames.Admin);
                var admin = await GateDrive.AddUserAsync(app, HostA, "isoadmin", "Is0!Admin$2026", "isoadmin@gate.test", RoleNames.Admin);

                using var client = GateDrive.NewClient(app, HostA);
                var cookies = await GateDrive.LoginAsync(app, client, HostA, "isoadmin", "Is0!Admin$2026", _output);

                var foreign = await GateDrive.GetAsync(client, cookies, HostA, $"/api/Metering/quotas/{quotaB}", _output, "foreign");
                var unknown = await GateDrive.GetAsync(client, cookies, HostA, $"/api/Metering/quotas/{Guid.NewGuid()}", _output, "unknown");
                _output.WriteLine($"[{storeName}] GET foreign -> {(int)foreign.Status}; GET unknown -> {(int)unknown.Status}");

                Assert.Equal(HttpStatusCode.NotFound, foreign.Status);
                Assert.Equal(unknown.Status, foreign.Status);

                var reset = await GateDrive.SendJsonAsync(app, client, cookies, HostA, admin.UserId, HttpMethod.Post,
                    $"/api/Metering/quotas/{quotaB}/reset", null, _output, "reset-foreign");
                _output.WriteLine($"[{storeName}] POST reset on site B's quota -> {(int)reset.Status}");
                Assert.Equal(HttpStatusCode.BadRequest, reset.Status);

                // And site B's counter was not touched by the refused command.
                var untouched = await GateDrive.InTenantAsync(app, HostA, (sp, alias) =>
                    sp.GetRequiredService<IQuotaRepository>().GetByIdAsync(quotaB));
                _output.WriteLine($"[{storeName}] site B quota after the refusal: consumed={untouched!.Consumed} limit={untouched.Limit}");
                Assert.Equal(siteB.SiteId, untouched.SiteId);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A second site on the SAME tenant, through the call the Host's "Add Site" page makes.</summary>
        private static Task<Site> CreateSecondSiteAsync(WebApplication app) =>
            GateDrive.InTenantAsync(app, HostA, async (sp, alias) =>
            {
                var site = await sp.GetRequiredService<ISiteRepository>().AddSiteAsync(new Site
                {
                    Name = "Metering Site B",
                    TenantId = alias.TenantId,
                    DefaultThemeType = Constants.DefaultTheme,
                    DefaultContainerType = Constants.DefaultContainer,
                    AdminContainerType = Constants.DefaultAdminContainer,
                    SiteTemplateType = Constants.DefaultSiteTemplate,
                    RenderMode = RenderModes.Static,
                    Runtime = Runtimes.Server,
                    Prerender = false,
                    Hybrid = false,
                    EnhancedNavigation = true,
                    CultureCode = "en",
                    AllowRegistration = false,
                    CaptureBrokenUrls = false,
                    VisitorTracking = false,
                    PwaIsEnabled = false
                });

                await sp.GetRequiredService<IAliasRepository>().AddAliasAsync(new Alias
                {
                    Name = HostB,
                    TenantId = alias.TenantId,
                    SiteId = site.SiteId,
                    IsDefault = true
                });

                return site;
            });
    }
}
