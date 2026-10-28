#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Connectivity.Persistence;
using Stomme.Health;
using Stomme.Modules.Connectivity.Application;
using Stomme.Modules.Connectivity.Domain;
using Stomme.Modules.Connectivity.Health;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Stomme.Tests.Connectivity
{
    /// <summary>
    /// BL-94 increment 11 (D110 Q3, D104) - the Connectivity health contribution over the module's real startup on the
    /// File store: with the switch OFF the contributor offers no probe and the fixture records ZERO calls; with it on,
    /// the one Site-scope probe tests every connection of the site it is asked about and no other site's, writes each
    /// verdict on its row, answers the worst with each connection's outcome in its data, and one connection's failure
    /// does not stop the next; two tenants are isolated and no row is re-stamped with another tenant.
    /// </summary>
    public sealed class ConnectionHealthProbeTests
    {
        private static readonly DateTime Now = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

        private static ProbeConnector Fixture() => new("fixture", (context, _) =>
            Task.FromResult(context.Endpoint.Contains("good", StringComparison.Ordinal) ? ConnectionTestResult.Ok() : ConnectionTestResult.Fail("refused")))
        { AuthType = AuthenticationType.None, EndpointKind = ConnectorEndpointKind.ConnectionString };

        private static Task<ConnectivityApplicationHarness> HarnessAsync(ProbeConnector fixture, bool enabled, Action<IServiceCollection>? configure = null) =>
            ConnectivityApplicationHarness.StartAsync(fixture, configure, extraConfiguration: new Dictionary<string, string?> { [ConnectivityHealthProbes.Key] = enabled ? "true" : "false" });

        private static async Task<Guid> SeedAsync(ConnectivityApplicationHarness harness, int siteId, string name, string endpoint, TenantId? tenant = null)
        {
            return await harness.InScopeAsync(async sp =>
            {
                var connection = ConnectionAggregate.Create(Guid.NewGuid(), tenant ?? TenantId.Default, siteId, name, "fixture", ConnectorEndpointKind.ConnectionString, endpoint, null, null, 5, Now);
                await sp.GetRequiredService<IConnectionRepository>().AddAsync(connection);
                await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
                return connection.Id;
            });
        }

        private static Task<(IReadOnlyList<IHealthProbe> Probes, HealthProbeResult? Result)> ProbeAsync(ConnectivityApplicationHarness harness, int siteId) =>
            harness.InScopeAsync(async sp =>
            {
                var contributor = sp.GetServices<IHealthContributor>().Single(candidate => candidate.ContributorId == ConnectionHealthContributor.Id);
                var probe = contributor.Probes.SingleOrDefault();
                return (contributor.Probes, probe is null ? null : await probe.CheckAsync(new HealthProbeContext(siteId)));
            });

        private static Task<IReadOnlyList<ConnectionAggregate>> RowsAsync(ConnectivityApplicationHarness harness, int siteId) =>
            harness.InScopeAsync(sp => sp.GetRequiredService<IConnectionRepository>().ListForSiteAsync(siteId));

        [Fact]
        public async Task With_the_switch_off_the_contributor_offers_no_probe_and_nothing_dials()
        {
            var fixture = Fixture();
            await using var harness = await HarnessAsync(fixture, enabled: false);
            await SeedAsync(harness, 1, "Prod", "host=good");

            var (probes, result) = await ProbeAsync(harness, 1);

            Assert.Empty(probes);
            Assert.Null(result);
            Assert.Equal(0, fixture.Calls);
            Assert.Equal(ConnectionStatus.Untested, (await RowsAsync(harness, 1)).Single().Status);
        }

        [Fact]
        public async Task With_the_switch_on_the_probe_tests_the_sites_connections_only_writes_each_verdict_and_answers_the_worst()
        {
            var fixture = Fixture();
            await using var harness = await HarnessAsync(fixture, enabled: true);
            await SeedAsync(harness, 1, "Prod", "host=good");
            await SeedAsync(harness, 1, "Broken", "host=bad");
            await SeedAsync(harness, 2, "Elsewhere", "host=good");

            var (probes, one) = await ProbeAsync(harness, 1);

            var probe = Assert.Single(probes);
            Assert.Equal((ConnectionHealthContributor.ProbeName, HealthProbeScope.Site), (probe.Name, probe.Scope));
            Assert.NotNull(one);
            Assert.Equal(HealthState.Unhealthy, one.State);
            Assert.Contains("1 of 2", one.Description);
            Assert.Contains("Broken", one.Description);
            Assert.Equal(new Dictionary<string, string> { ["Prod"] = "Succeeded", ["Broken"] = "Failed" }, one.Data);
            Assert.Equal(2, fixture.Calls); // site 2's connection was not dialled

            var rows = await RowsAsync(harness, 1);
            Assert.Equal((ConnectionStatus.Failing, ConnectionStatus.Healthy), (rows.Single(row => row.Name == "Broken").Status, rows.Single(row => row.Name == "Prod").Status));
            Assert.All(rows, row => Assert.Equal(Now, row.LastTestedUtc)); // the harness clock
            Assert.Equal(ConnectionStatus.Untested, (await RowsAsync(harness, 2)).Single().Status);

            var (_, two) = await ProbeAsync(harness, 2);
            Assert.Equal((HealthState.Healthy, 3), (two!.State, fixture.Calls));
            Assert.Equal(ConnectionStatus.Healthy, (await RowsAsync(harness, 2)).Single().Status);

            var (_, none) = await ProbeAsync(harness, 3);
            Assert.Equal((HealthState.Healthy, "No connections."), (none!.State, none.Description));
        }

        [Fact]
        public async Task A_connector_that_throws_is_a_verdict_not_an_exception_and_the_next_connection_is_still_tested()
        {
            var fixture = new ProbeConnector("fixture", (context, _) => context.Endpoint.Contains("boom", StringComparison.Ordinal)
                ? throw new InvalidOperationException("password=hunter2")
                : Task.FromResult(ConnectionTestResult.Ok()))
            { AuthType = AuthenticationType.None };
            await using var harness = await HarnessAsync(fixture, enabled: true);
            await SeedAsync(harness, 1, "A boom", "host=boom");
            await SeedAsync(harness, 1, "B fine", "host=fine");

            var (_, result) = await ProbeAsync(harness, 1);

            Assert.Equal(HealthState.Unhealthy, result!.State);
            Assert.Equal(new Dictionary<string, string> { ["A boom"] = "Faulted", ["B fine"] = "Succeeded" }, result.Data);
            Assert.DoesNotContain("hunter2", result.Description + string.Join(";", result.Data!.Values));
            Assert.Equal(2, fixture.Calls);
        }

        [Fact]
        public async Task Two_tenants_are_isolated_and_no_row_is_stamped_with_another_tenant()
        {
            var fixture = Fixture();
            var tenants = new SwitchableTenant(TenantId.Default);
            await using var harness = await HarnessAsync(fixture, enabled: true, services =>
            {
                services.RemoveAll<ITenantContext>();
                services.AddSingleton<ITenantContext>(tenants);
            });
            var second = TenantId.From(new Guid("00000000-0000-0000-0000-000000000002"));
            await SeedAsync(harness, 1, "Ours", "host=good");
            tenants.Switch(second);
            await SeedAsync(harness, 1, "Theirs", "host=bad", second);

            // Probed as the second tenant: only theirs is tested and marked; ours stays Untested.
            var (_, theirs) = await ProbeAsync(harness, 1);
            Assert.Equal(new Dictionary<string, string> { ["Theirs"] = "Failed" }, theirs!.Data);
            var theirRows = await RowsAsync(harness, 1);
            Assert.Equal((second, ConnectionStatus.Failing), (theirRows.Single().TenantId, theirRows.Single().Status));

            tenants.Switch(TenantId.Default);
            var ourRows = await RowsAsync(harness, 1);
            Assert.Equal((TenantId.Default, ConnectionStatus.Untested), (ourRows.Single().TenantId, ourRows.Single().Status));
            var (_, ours) = await ProbeAsync(harness, 1);
            Assert.Equal(new Dictionary<string, string> { ["Ours"] = "Succeeded" }, ours!.Data);
            Assert.Equal(2, fixture.Calls);
        }

        private sealed class SwitchableTenant(TenantId initial) : ITenantContext
        {
            public TenantId? CurrentTenantId { get; private set; } = initial;

            public bool IsSystemScope => false;

            public void Switch(TenantId tenant) => CurrentTenantId = tenant;
        }
    }
}
