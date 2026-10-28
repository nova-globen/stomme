#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Stomme.Health;
using Stomme.Health.Persistence;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Tenancy;
using Stomme.Modules.Health.Application;
using Stomme.Modules.Health.Domain;
using Stomme.Modules.Health.Infrastructure;
using Stomme.Repository;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// The D30 bar for BL-83: install a site on a REAL store, run a real probe pass, and serve the pages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every other health suite is in memory, which proves the arithmetic and the state machine and nothing at
    /// all about whether the two aggregates round-trip on the store the install actually chose. This one runs on
    /// SQLite, PostgreSQL AND RavenDB, and it exercises the paths where the providers are known to diverge: a
    /// deterministic-id upsert (only Raven's Add is a blind upsert; EF raises a primary-key violation and the
    /// File store throws on an existing path), a range read over a UTC <c>DateTime</c>, and an enum-and-int
    /// filter.
    /// </para>
    /// <para>
    /// <b>It probes the TENANT scope, deliberately.</b> That is the scope the <c>store</c> probe lives in, so the
    /// probe being driven is one that really round-trips the module store — which on this path is the same store
    /// the health rows are being written to. The Site scope carries only the SMTP probe, and a test site has no
    /// mail server configured, so a Site-scope pass correctly runs nothing.
    /// </para>
    /// <para>
    /// <b>What it does NOT assert, and why:</b> the module control's own markup. BL-79 measured that a clean-arch
    /// Client module control emits nothing through the TestServer transport — the sibling Notifications module is
    /// absent the same way, while HtmlText's seam-module content renders on the same pipeline — so asserting it
    /// here would fail for a reason that has nothing to do with this module. The BL-54 dead-click class therefore
    /// remains un-excluded for this module's control and only a real browser can exclude it.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class InstalledSiteHealthDriveTests
    {
        private const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        public InstalledSiteHealthDriveTests(ITestOutputHelper output) => _output = output;

        public static TheoryData<string> Stores() => new() { SqliteGateStore.StoreName, PostgresGateStore.StoreName, RavenGateStore.StoreName };

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task An_installed_site_probes_rolls_up_and_serves_health_on_a_real_store(string storeName)
        {
            var store = LiftedRequiresGateTests.ResolveStore(storeName);
            Assert.True(store.IsReachable(), store.UnreachableMessage);

            using var scratch = new ScratchGateInstall(store, Host, colocateModuleAggregates: storeName == PostgresGateStore.StoreName,
                extraSettings: new System.Text.Json.Nodes.JsonObject { ["Health"] = new System.Text.Json.Nodes.JsonObject { ["Enabled"] = false } });
            // The probe JOB is deliberately left OFF. Health:Enabled gates the job only - the pass this drive
            // calls directly does not consult it - and letting the job run made three assertions here racy,
            // measured: it ticks the INSTALLATION scope too, so a second alert row appears at site 0 partway
            // through, and it accumulates into the same Minute row this test counts. Assertions that hold only
            // when a background job has not got there yet are not assertions. What the job itself does is covered
            // by HealthJobTenantScopeTests; what this drive is for is the STORE.
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                var alias = await GateDrive.AliasAsync(app, Host);

                // ---- the module's admin page exists on the installed site, and carries ITS module -------------
                var placed = await GateDrive.InTenantAsync(app, Host, async (sp, current) =>
                {
                    var pages = (await sp.GetRequiredService<IPageRepository>().GetPagesAsync(current.SiteId)).ToList();
                    var admin = pages.SingleOrDefault(page => page.Path == "admin/health");
                    Assert.NotNull(admin);

                    return (await sp.GetRequiredService<IPageModuleRepository>().GetPageModulesAsync(current.SiteId))
                        .Where(module => module.PageId == admin!.PageId)
                        .Select(module => module.Title)
                        .ToList();
                });

                _output.WriteLine($"[{storeName}] admin/health carries: {string.Join(", ", placed)}");
                Assert.Contains("Health", placed);

                // ---- a REAL probe pass, against the store the install chose ----------------------------------
                var tenantId = FrameworkTenantId.ToTenantIdOrNull(alias.TenantId)!;
                var pass = await GateDrive.InTenantAsync(app, Host, (sp, _) =>
                    sp.GetRequiredService<HealthProbePass>().RunAsync(tenantId, HealthProbeScope.Tenant, 0));

                _output.WriteLine($"[{storeName}] pass: probes={pass.ProbesRun} rows={pass.RowsWritten} worst={pass.Worst}");

                // The store probe really did run, and it found the store it just wrote to reachable.
                Assert.True(pass.ProbesRun >= 1, "the Tenant-scope pass ran no probe; the store probe should have run");
                Assert.True(pass.RowsWritten >= 1, "the pass wrote no rollup row");
                Assert.Equal(HealthState.Healthy, pass.Worst);

                // ---- the rows ROUND-TRIP on this provider ----------------------------------------------------
                var rows = await GateDrive.InTenantAsync(app, Host, (sp, _) =>
                    sp.GetRequiredService<IHealthRollupRepository>().ListAsync(HealthProbeScope.Tenant, 0, HealthRollupWindow.Minute));

                var minute = Assert.Single(rows);
                _output.WriteLine($"[{storeName}] minute row: subject={minute.Subject.Value} checks={minute.Checks} avail={minute.AvailabilityPercent} at={minute.PeriodStartUtc:o}");

                // The value object came back as a value object, and the instant came back as UTC - the two things
                // that differ per provider and that D102 measured going wrong.
                Assert.Equal("stomme:store", minute.Subject.Value);
                Assert.Equal(1, minute.Checks);
                Assert.Equal(100d, minute.AvailabilityPercent);

                // ---- a SECOND pass upserts rather than duplicating -------------------------------------------
                await GateDrive.InTenantAsync(app, Host, (sp, _) =>
                    sp.GetRequiredService<HealthProbePass>().RunAsync(tenantId, HealthProbeScope.Tenant, 0));

                var afterSecond = await GateDrive.InTenantAsync(app, Host, (sp, _) =>
                    sp.GetRequiredService<IHealthRollupRepository>().ListAsync(HealthProbeScope.Tenant, 0, HealthRollupWindow.Minute));

                _output.WriteLine($"[{storeName}] after a second pass: {afterSecond.Count} minute row(s), {afterSecond.Sum(row => row.Checks)} check(s)");

                // Same minute, same deterministic id: ONE row carrying BOTH checks. On EF a second Add here would
                // be a primary-key violation, which is exactly why the write is GetById-then-Add-or-Update.
                Assert.Single(afterSecond);
                Assert.Equal(2, afterSecond[0].Checks);

                // ---- the rollup pass folds the minutes into an hour, on this provider -------------------------
                var rollup = await GateDrive.InTenantAsync(app, Host, (sp, _) =>
                    sp.GetRequiredService<HealthRollupPass>().RunAsync(tenantId, HealthProbeScope.Tenant, 0));

                _output.WriteLine($"[{storeName}] rollup: hours={rollup.HoursWritten} days={rollup.DaysWritten} swept={rollup.MinutesSwept}");
                Assert.True(rollup.HoursWritten >= 1);

                var hours = await GateDrive.InTenantAsync(app, Host, (sp, _) =>
                    sp.GetRequiredService<IHealthRollupRepository>().ListAsync(HealthProbeScope.Tenant, 0, HealthRollupWindow.Hour));

                var hour = Assert.Single(hours);
                _output.WriteLine($"[{storeName}] hour row: checks={hour.Checks} avg={hour.AverageLatencyMs}");
                Assert.Equal(2, hour.Checks);

                // ---- the alert row round-trips too, and says nothing is outstanding ---------------------------
                var alerts = await GateDrive.InTenantAsync(app, Host, (sp, _) =>
                    sp.GetRequiredService<IHealthAlertStateRepository>().ListBySiteAsync(0));

                // Scoped to the subject this drive created, not "the only row on the install".
                var alert = Assert.Single(alerts, row => row.Subject.Value == "stomme:store");
                _output.WriteLine($"[{storeName}] alert row: state={alert.CurrentState} outstanding={alert.HasOutstandingAlert} raises={alert.AlertCount}");
                Assert.Equal(HealthState.Healthy, alert.CurrentState);
                Assert.False(alert.HasOutstandingAlert);
                Assert.Equal(0, alert.AlertCount);

                // ---- the page is SERVED, and it is the module's, not the installer's --------------------------
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);

                var served = await GateDrive.GetAsync(client, cookies, Host, "/admin/health", _output, "admin/health");
                _output.WriteLine($"[{storeName}] GET /admin/health -> {(int)served.Status}, {served.Body.Length} chars");

                Assert.Equal(HttpStatusCode.OK, served.Status);
                Assert.DoesNotContain("Installation Wizard", served.Body);
                Assert.DoesNotContain("Default Site Template", served.Body);
                Assert.Contains("<title>Health", served.Body);

                // ---- the anonymous endpoints answer on a REAL installed host ----------------------------------
                var live = await GateDrive.GetAsync(client, cookies, Host, HealthEndpoints.LivenessPath, _output, "healthz");
                _output.WriteLine($"[{storeName}] GET /healthz -> {(int)live.Status} '{live.Body.Trim()}'");
                Assert.Equal(HttpStatusCode.OK, live.Status);
                Assert.Equal("alive", live.Body.Trim());

                // Readiness reports the last JOB tick, so on a STARTED host it depends on whether the per-minute
                // job has got there yet - which is a race, not a property. The first version of this asserted a
                // flat 503 and went red on one store for exactly that reason: the job really had ticked. So what
                // is asserted is the CONTRACT - the endpoint agrees with the snapshot - which is race-free and is
                // the thing worth proving on a real install. The deterministic before-and-after states have their
                // own cover in HealthEndpointExposureTests, on a host whose jobs are not running.
                var snapshot = app.Services.GetRequiredService<HealthSnapshot>().Last;
                var ready = await GateDrive.GetAsync(client, cookies, Host, HealthEndpoints.ReadinessPath, _output, "readyz");
                _output.WriteLine($"[{storeName}] GET /readyz -> {(int)ready.Status} '{ready.Body.Trim()}'; snapshot={(snapshot is null ? "(none yet)" : $"{snapshot.Value.State} at {snapshot.Value.AtUtc:o}")}");

                if (snapshot is null)
                {
                    Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.Status);
                    Assert.Equal("not-ready", ready.Body.Trim());
                }
                else
                {
                    var expected = snapshot.Value.State == HealthState.Unhealthy
                        ? HttpStatusCode.ServiceUnavailable
                        : HttpStatusCode.OK;
                    Assert.Equal(expected, ready.Status);
                }

                // Either way it says ONE word and nothing about why - no probe name, no host, no exception text.
                Assert.DoesNotContain("<", ready.Body);
                Assert.True(ready.Body.Trim().Length < 32, $"readiness answered {ready.Body.Length} characters");

                // ---- and the gated API answers the operator over HTTP, from the rows above --------------------
                var dashboard = await GateDrive.GetAsync(client, cookies, Host, "/api/Health", _output, "api/Health");
                _output.WriteLine($"[{storeName}] GET /api/Health -> {(int)dashboard.Status}, {dashboard.Body.Length} chars");

                Assert.Equal(HttpStatusCode.OK, dashboard.Status);
                Assert.Contains("stomme:store", dashboard.Body);

                // ---- retention runs against the real store without touching anything live ---------------------
                // Resolved through the SEAM rather than by concrete type: that proves the module's pass is
                // actually registered where PurgeJob will find it, which is the integration worth asserting.
                var purged = await GateDrive.InTenantAsync(app, Host, (sp, _) =>
                {
                    var purger = sp.GetServices<IRetentionPurger>()
                        .Single(item => item.SettingKey == HealthSettings.RetentionKey);

                    Assert.Equal("Health Rollups", purger.DisplayName);
                    return purger.PurgeAsync(alias.SiteId, 30);
                });

                _output.WriteLine($"[{storeName}] retention purged {purged} row(s)");

                // Nothing written a moment ago is 30 days old, so a correct pass takes nothing.
                Assert.Equal(0, purged);

                // ---- the host logged nothing -----------------------------------------------------------------
                var errors = GateDrive.DrainCapturedErrors();
                _output.WriteLine($"[{storeName}] host errors: {(errors.Count == 0 ? "(none)" : string.Join(" || ", errors))}");
                Assert.Empty(errors);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
