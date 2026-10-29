#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Health;
using Stomme.Health.Persistence;
using Stomme.Modules.Health.Application;
using Stomme.Modules.Health.Domain;
using Stomme.Security.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tests.Help;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;
using SiteState = Stomme.Shared.SiteState;

namespace Stomme.Tests
{
    /// <summary>
    /// Site isolation for health: two sites in ONE tenant, driven against a real store, refuse each other's health
    /// rows - through the repository and through the handlers that read the site from the resolved alias.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The persistence seam's automatic isolation is TENANT-only, and the default install puts every site in one tenant,
    /// so site isolation is this module's own job - by the site in the deterministic id and by every read taking the
    /// caller's site. The TWO-TENANT case was already driven (<c>HealthJobTenantScopeTests</c>); this is the other half.
    /// </para>
    /// <para>
    /// <b>The acknowledge handler is run with site A's alias and site B's row id</b> - the IDOR its own remarks name: the ids
    /// are deterministic and therefore guessable, so "another site's alert" must answer exactly like "no such alert".
    /// PostgreSQL and RavenDB FAIL rather than skip.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class HealthSiteIsolationTests
    {
        private readonly ITestOutputHelper _output;

        public HealthSiteIsolationTests(ITestOutputHelper output) => _output = output;

        public static TheoryData<string> Stores() => new() { SqliteGateStore.StoreName, PostgresGateStore.StoreName, RavenGateStore.StoreName };

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task Two_sites_in_ONE_tenant_refuse_each_others_health_rows(string storeName)
        {
            var store = LiftedRequiresGateTests.ResolveStore(storeName);
            Assert.True(store.IsReachable(), store.UnreachableMessage);

            using var scratch = new ScratchGateInstall(store, HelpDrive.Host, colocateModuleAggregates: storeName == PostgresGateStore.StoreName);
            var app = await GateDrive.InstallAsync(scratch, _output, configure: null, start: false);
            try
            {
                var siteA = await HelpDrive.SiteAsync(app, HelpDrive.Host);
                // A second site of the SAME tenant. Module rows are keyed by the site id alone, so the site needs no page tree
                // for this - what matters is that the alias resolves to a different site in the same tenant.
                var siteB = siteA with { Alias = new Models.Alias { AliasId = siteA.Alias.AliasId + 1000, Name = "site-b.localhost", TenantId = siteA.Alias.TenantId, SiteId = siteA.Alias.SiteId + 1000, IsDefault = true } };
                var subject = HealthSubject.For("stomme", "store");
                var now = DateTime.UtcNow;

                var (idA, idB) = await HelpDrive.InSiteAsync(app, siteA, async sp =>
                {
                    var rows = sp.GetRequiredService<IHealthAlertStateRepository>();
                    var a = HealthAlertStateAggregate.For(siteA.TenantId, HealthProbeScope.Site, siteA.Alias.SiteId, subject, now);
                    var b = HealthAlertStateAggregate.For(siteA.TenantId, HealthProbeScope.Site, siteB.Alias.SiteId, subject, now);
                    await rows.AddAsync(a);
                    await rows.AddAsync(b);
                    await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                    return (a.Id, b.Id);
                });

                _output.WriteLine($"[{storeName}] site A {siteA.Alias.SiteId} row {idA}; site B {siteB.Alias.SiteId} row {idB}; one tenant {siteA.TenantId}");
                Assert.NotEqual(idA, idB);                                            // the site is part of the key

                await HelpDrive.InSiteAsync(app, siteA, async sp =>
                {
                    var rows = sp.GetRequiredService<IHealthAlertStateRepository>();

                    // ---- the repository: each read answers for the site it is given, and no other ----------------------
                    Assert.Equal(new[] { idA }, (await rows.ListBySiteAsync(siteA.Alias.SiteId)).Select(row => row.Id));
                    Assert.Equal(new[] { idB }, (await rows.ListBySiteAsync(siteB.Alias.SiteId)).Select(row => row.Id));
                    Assert.NotNull(await rows.GetForCurrentSiteAsync(idA, siteA.Alias.SiteId));
                    Assert.Null(await rows.GetForCurrentSiteAsync(idB, siteA.Alias.SiteId));   // B's id, asked as A
                    Assert.Null(await rows.GetForCurrentSiteAsync(Guid.NewGuid(), siteA.Alias.SiteId));

                    // ---- the handlers, which take the site from the RESOLVED alias -------------------------------------------
                    var siteState = sp.GetRequiredService<SiteState>();
                    Assert.Equal(siteA.Alias.SiteId, siteState.Alias!.SiteId);

                    var acknowledge = new AcknowledgeHealthAlertCommandHandler(rows, sp.GetRequiredService<IUnitOfWork>(), siteState);
                    var crossSite = await Assert.ThrowsAsync<InvalidOperationException>(() => acknowledge.Handle(new AcknowledgeHealthAlertCommand(idB), default));
                    var nowhere = await Assert.ThrowsAsync<InvalidOperationException>(() => acknowledge.Handle(new AcknowledgeHealthAlertCommand(Guid.NewGuid()), default));
                    Assert.Equal(nowhere.Message, crossSite.Message);                  // the id space is not an oracle

                    var dashboard = await new GetHealthDashboardQueryHandler(rows, sp.GetRequiredService<HealthSnapshot>(), siteState,
                        sp.GetRequiredService<ICurrentUser>()).Handle(new GetHealthDashboardQuery(), default);
                    Assert.Single(dashboard.Subjects, view => view.Scope == HealthProbeScope.Site);   // A's row, not B's
                    return true;
                });

                // ---- and the same from site B's side ----------------------------------------------------------------------
                await HelpDrive.InSiteAsync(app, siteB, async sp =>
                {
                    var rows = sp.GetRequiredService<IHealthAlertStateRepository>();
                    Assert.Null(await rows.GetForCurrentSiteAsync(idA, siteB.Alias.SiteId));
                    var dashboard = await new GetHealthDashboardQueryHandler(rows, sp.GetRequiredService<HealthSnapshot>(), sp.GetRequiredService<SiteState>(),
                        sp.GetRequiredService<ICurrentUser>()).Handle(new GetHealthDashboardQuery(), default);
                    Assert.Single(dashboard.Subjects, view => view.Scope == HealthProbeScope.Site);
                    return true;
                });
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
