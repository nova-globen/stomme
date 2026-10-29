#nullable enable
using System;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Health;
using Stomme.Health.Persistence;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Tenancy;
using Stomme.Metering;
using Stomme.Metering.Persistence;
using Stomme.Models.Notifications;
using Stomme.Modules.Health.Application;
using Stomme.Modules.Health.Domain;
using Stomme.Modules.Metering.Application;
using Stomme.Modules.Metering.Domain;
using Stomme.Modules.Notifications.Application;
using Stomme.Modules.Notifications.Domain;
using Stomme.Modules.Notifications.Infrastructure.Channels;
using Stomme.Notifications;
using Stomme.Notifications.Persistence;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Stomme.Tenancy.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// What a page in ONE Interactive Server circuit shows after something else writes.
    /// </summary>
    /// <remarks>
    /// A circuit is one DI scope for as long as the page is open, so it is reproduced here as exactly that: the
    /// module's REAL query handler, built from ONE long-lived scope, asked twice - with the module's REAL writer
    /// (the usage fold, the dispatch job, an observed probe result) running in a scope of its own in between. The
    /// database's own answer is read through a third, fresh scope, so a case cannot pass because nothing changed.
    /// <para>
    /// Measured before the fix: all three pages STALE on SQLite and PostgreSQL, fresh on RavenDB, and the
    /// first write from the stale scope refused with a concurrency conflict. The module repositories no longer track
    /// (<c>EfRepository.TrackReads</c>), so every case now asserts FRESH on every provider. Remove
    /// <c>TrackReads = false</c> from <c>AddModuleAggregatePersistence</c> and the six EF cases go red.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class CircuitStaleReadMeasurementTests
    {
        private const string Host = "localhost";
        private const string Metric = "tests.circuit";

        private readonly ITestOutputHelper _output;

        public CircuitStaleReadMeasurementTests(ITestOutputHelper output) => _output = output;

        public static TheoryData<string> Stores() => new() { SqliteGateStore.StoreName, PostgresGateStore.StoreName, RavenGateStore.StoreName };

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task Metering_quotas_page(string storeName)
        {
            var (scratch, app) = await InstallAsync(storeName, new JsonObject { ["Metering"] = new JsonObject { ["Enabled"] = true } });
            try
            {
                var alias = await GateDrive.AliasAsync(app, Host);
                var tenantId = FrameworkTenantId.ToTenantIdOrNull(alias.TenantId)!;

                await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    await sp.GetRequiredService<IQuotaRepository>().AddAsync(QuotaAggregate.Define(
                        QuotaIds.For(tenantId, alias.SiteId, Metric, QuotaPeriod.Monthly), tenantId, alias.SiteId, Metric, QuotaPeriod.Monthly, 100, 0, DateTime.UtcNow));
                    return true;
                });

                await GateDrive.InTenantAsync(app, Host, async (circuit, _) =>
                {
                    var page = new ListQuotasQueryHandler(
                        circuit.GetRequiredService<IQuotaRepository>(), circuit.GetRequiredService<SiteState>(), circuit.GetRequiredService<IMeteringClock>());

                    var first = (await page.Handle(new(), default)).Single(quota => quota.Metric == Metric).Consumed;

                    // The REAL producer path, in a scope of its own: the fold moves the quota's counter.
                    var outcome = await GateDrive.InTenantAsync(app, Host, (elsewhere, _) =>
                        elsewhere.GetRequiredService<IUsageRecorder>().RecordAsync(new UsageEvent(Metric, 7, Guid.NewGuid(), "circuit")));
                    Assert.True(outcome.Recorded);

                    var truth = await GateDrive.InTenantAsync(app, Host, async (fresh, _) =>
                        (await fresh.GetRequiredService<IQuotaRepository>().ListBySiteAsync(alias.SiteId)).Single(quota => quota.Metric == Metric).Consumed);
                    var second = (await page.Handle(new(), default)).Single(quota => quota.Metric == Metric).Consumed;

                    Report(storeName, "metering/quotas Consumed", first, truth, second);
                    Assert.Equal(7, truth);

                    // The operator now clicks Suspend on the page they are looking at - a WRITE through the same scope.
                    var quotaId = QuotaIds.For(tenantId, alias.SiteId, Metric, QuotaPeriod.Monthly);
                    var suspend = new SetQuotaEnforcementCommandHandler(
                        circuit.GetRequiredService<IQuotaRepository>(), circuit.GetRequiredService<Stomme.SharedKernel.Domain.IUnitOfWork>(), circuit.GetRequiredService<SiteState>());
                    for (var attempt = 1; attempt <= 2; attempt++)
                    {
                        string result;
                        try
                        {
                            await suspend.Handle(new SetQuotaEnforcementCommand(quotaId, false), default);
                            result = "saved";
                        }
                        catch (Exception ex)
                        {
                            result = ex.GetType().Name;
                        }

                        var after = await GateDrive.InTenantAsync(app, Host, async (fresh, _) =>
                            (await fresh.GetRequiredService<IQuotaRepository>().ListBySiteAsync(alias.SiteId)).Single(quota => quota.Metric == Metric));
                        _output.WriteLine($"[BL-160][{storeName}] metering Suspend click #{attempt} from the same scope: {result}; database now Consumed={after.Consumed} Status={after.Status}");

                        // The FIRST click works: it is checked against the version just read, not one read minutes
                        // ago - and it does not take the job's counter back with it.
                        Assert.Equal("saved", result);
                        Assert.Equal(7, after.Consumed);
                        Assert.Equal(QuotaStatus.Suspended, after.Status);
                    }

                    return true;
                });
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
                scratch.Dispose();
            }
        }

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task Notifications_send_log_page(string storeName)
        {
            var (scratch, app) = await InstallAsync(storeName, settings: null);
            try
            {
                var hostUserId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);

                var id = await GateDrive.InTenantAsync(app, Host, async (sp, a) =>
                {
                    await sp.GetRequiredService<INotificationChannelRepository>().AddAsync(NotificationChannelAggregate.Create(
                        Guid.NewGuid(), TenantId.Default, a.SiteId, ChannelKind.InApp, "Inbox", new TransportSettings(ChannelKind.InApp, null, null, [])));
                    return await sp.GetRequiredService<INotificationDispatcher>().DispatchAsync(new NotificationDispatchRequest("generic.message",
                        [new DispatchRecipient(NotificationRecipientKind.User, hostUserId.ToString(CultureInfo.InvariantCulture))], [NotificationChannelKind.InApp],
                        [new DispatchParameter("subject", "circuit"), new DispatchParameter("body", "circuit")]));
                });

                await GateDrive.InTenantAsync(app, Host, async (circuit, _) =>
                {
                    var page = new ListNotificationRequestsQueryHandler(
                        circuit.GetRequiredService<INotificationRequestRepository>(), circuit.GetRequiredService<SiteState>());

                    var first = (await page.Handle(new(), default)).Single(item => item.Id == id).Status;

                    // The REAL dispatch job, exactly as HostedServiceBase runs it.
                    var notes = await Stomme.Tests.Notifications.InstalledNotificationDispatchJobTests.RunJobAsync(app);
                    _output.WriteLine($"[{storeName}] job: {notes}");

                    var truth = await GateDrive.InTenantAsync(app, Host, async (fresh, _) =>
                        (await fresh.GetRequiredService<INotificationRequestRepository>().GetByIdAsync(id))!.Status.ToString());
                    var second = (await page.Handle(new(), default)).Single(item => item.Id == id).Status;

                    Report(storeName, "notifications/log Status", first, truth, second);
                    Assert.Equal("Sent", truth);
                    return true;
                });
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
                scratch.Dispose();
            }
        }

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task Health_dashboard_page(string storeName)
        {
            var (scratch, app) = await InstallAsync(storeName, new JsonObject { ["Health"] = new JsonObject { ["Enabled"] = false } });
            try
            {
                var alias = await GateDrive.AliasAsync(app, Host);
                var tenantId = FrameworkTenantId.ToTenantIdOrNull(alias.TenantId)!;

                // A real pass writes the Tenant-scope store probe's alert row: Healthy.
                await GateDrive.InTenantAsync(app, Host, (sp, _) =>
                    sp.GetRequiredService<HealthProbePass>().RunAsync(tenantId, HealthProbeScope.Tenant, 0));

                await GateDrive.InTenantAsync(app, Host, async (circuit, _) =>
                {
                    var page = new GetHealthDashboardQueryHandler(
                        circuit.GetRequiredService<IHealthAlertStateRepository>(), circuit.GetRequiredService<HealthSnapshot>(),
                        circuit.GetRequiredService<SiteState>(), circuit.GetRequiredService<ICurrentUser>());

                    var first = (await page.Handle(new(), default)).Subjects.Single().State;

                    // What the probe pass does when the dependency breaks: load the row, observe, save - elsewhere.
                    await GateDrive.InTenantAsync(app, Host, async (elsewhere, _) =>
                    {
                        var rows = elsewhere.GetRequiredService<IHealthAlertStateRepository>();
                        var row = (await rows.ListBySiteAsync(0)).Single();
                        row.Observe(HealthState.Unhealthy, "measured", DateTime.UtcNow);
                        await rows.UpdateAsync(row);
                        await elsewhere.GetRequiredService<Stomme.SharedKernel.Domain.IUnitOfWork>().SaveChangesAsync();
                        return true;
                    });

                    var truth = await GateDrive.InTenantAsync(app, Host, async (fresh, _) =>
                        (await fresh.GetRequiredService<IHealthAlertStateRepository>().ListBySiteAsync(0)).Single().CurrentState);
                    var second = (await page.Handle(new(), default)).Subjects.Single().State;

                    Report(storeName, "health/dashboard State", first, truth, second);
                    Assert.Equal(HealthState.Unhealthy, truth);
                    return true;
                });
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
                scratch.Dispose();
            }
        }

        /// <summary>
        /// FeatureManagement is NOT on the storage-blind registration (its own context, the open generic), so the fix
        /// above does not reach it. Measured rather than assumed.
        /// </summary>
        [Fact]
        public async Task FeatureManagement_in_a_long_lived_scope()
        {
            var (scratch, app) = await InstallAsync(SqliteGateStore.StoreName, settings: null);
            try
            {
                const string key = Stomme.FeatureManagement.FeatureCatalog.VisitorTracking;

                await GateDrive.InTenantAsync(app, Host, async (circuit, _) =>
                {
                    var admin = circuit.GetRequiredService<Stomme.FeatureManagement.Application.IFeatureAdminService>();
                    var evaluator = circuit.GetRequiredService<Stomme.FeatureManagement.Application.IFeatureEvaluator>();

                    var listedFirst = (await admin.ListAsync()).Single(view => view.Key == key).Enabled;
                    var evaluatedFirst = await evaluator.IsEnabledAsync(key);

                    await GateDrive.InTenantAsync(app, Host, async (elsewhere, _) =>
                        await elsewhere.GetRequiredService<Stomme.FeatureManagement.Application.IFeatureAdminService>().ToggleAsync(key, !listedFirst));

                    var truth = await GateDrive.InTenantAsync(app, Host, async (fresh, _) =>
                        (await fresh.GetRequiredService<Stomme.FeatureManagement.Application.IFeatureAdminService>().ListAsync()).Single(view => view.Key == key).Enabled);
                    var listedSecond = (await admin.ListAsync()).Single(view => view.Key == key).Enabled;
                    var evaluatedSecond = await evaluator.IsEnabledAsync(key);

                    _output.WriteLine($"[BL-160][SQLite] featuremanagement admin list Enabled: first={listedFirst}; database now={truth}; re-read in the SAME scope={listedSecond} => {(listedSecond == truth ? "FRESH" : "STALE")}");
                    _output.WriteLine($"[BL-160][SQLite] featuremanagement evaluator: first={evaluatedFirst}; re-evaluated in the SAME scope={evaluatedSecond} => {(evaluatedSecond == truth ? "FRESH" : "STALE")}");
                    Assert.NotEqual(listedFirst, truth);

                    // Measured earlier. The EVALUATOR - the only part of this module a circuit consults - is fresh:
                    // it reads through the singleton flag cache, which the toggle invalidates. That is asserted.
                    Assert.Equal(truth, evaluatedSecond);

                    // The admin LIST through a long-lived scope was STALE when this was first measured (tracked reads, the
                    // module's own context). Nothing consumed it from a circuit - its one consumer is FeatureController,
                    // a scope per request - so it was latent, and the fix closed it before it could become live: the
                    // module's two repositories no longer track either.
                    Assert.Equal(truth, listedSecond);

                    // ...and a write from that scope lands first time, on the row as it is NOW.
                    await admin.ToggleAsync(key, listedFirst);
                    var restored = await GateDrive.InTenantAsync(app, Host, async (fresh, _) =>
                        (await fresh.GetRequiredService<Stomme.FeatureManagement.Application.IFeatureAdminService>().ListAsync()).Single(view => view.Key == key).Enabled);
                    Assert.Equal(listedFirst, restored);
                    return true;
                });
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
                scratch.Dispose();
            }
        }

        private void Report<TFirst, TTruth, TSecond>(string storeName, string what, TFirst first, TTruth truth, TSecond second)
        {
            var fresh = string.Equals(truth?.ToString(), second?.ToString(), StringComparison.Ordinal);
            _output.WriteLine(
                $"[BL-160][{storeName}] {what}: page first={first}; database now={truth}; page re-read in the SAME scope={second} => " +
                (fresh ? "FRESH" : "STALE"));

            // The write really happened - otherwise "fresh" would only mean "unchanged".
            Assert.NotEqual(first?.ToString(), truth?.ToString());
            Assert.True(fresh, $"[{storeName}] {what}: the page re-read {second} in the same scope while the database says {truth}");
        }

        // Settings go through appsettings.override.json, as a deployed install's do - never builder.Configuration, which
        // the host's merged configuration does not include (HostConfiguration).
        private async Task<(ScratchGateInstall Scratch, WebApplication App)> InstallAsync(string storeName, JsonObject? settings)
        {
            var store = LiftedRequiresGateTests.ResolveStore(storeName);
            Assert.True(store.IsReachable(), store.UnreachableMessage);

            var scratch = new ScratchGateInstall(store, Host, colocateModuleAggregates: storeName == PostgresGateStore.StoreName, extraSettings: settings);
            var app = await GateDrive.InstallAsync(scratch, _output, configure: null!, start: false);
            await GateDrive.StartNonJobHostedServicesAsync(app);
            return (scratch, app);
        }
    }
}
