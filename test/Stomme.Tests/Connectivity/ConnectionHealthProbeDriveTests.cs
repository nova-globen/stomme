#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Health;
using Stomme.Health.Persistence;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Tenancy;
using Stomme.Modules.Connectivity.Health;
using Stomme.Modules.Health.Application;
using Stomme.Modules.Health.Domain;
using Stomme.Repository;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectivity
{
    /// <summary>
    /// BL-94 increment 11 (D110 Q3, D104) - the connection health probe on an installed host, on PostgreSQL and RavenDB:
    /// with <c>Health:Enabled</c> and <c>Connectivity:HealthProbes:Enabled</c> in the override file and a fixture connector
    /// composed into the host, two connections are created through the API, the real Site-scope pass runs, and Healthy
    /// and Failing appear on the connections (the API) and in the Health admin's subjects (the alert ledger and the
    /// dashboard query): the <c>connectivity:connections</c> subject is Unhealthy while one connection fails.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class ConnectionHealthProbeDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public static TheoryData<string> Stores() => new() { PostgresGateStore.StoreName, RavenGateStore.StoreName };

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task The_site_pass_tests_every_connection_and_the_verdicts_reach_the_connections_and_the_health_admin(string storeName)
        {
            var fixture = new ProbeConnector("fixture", (context, _) =>
                Task.FromResult(context.Endpoint.Contains("good", StringComparison.Ordinal) ? ConnectionTestResult.Ok() : ConnectionTestResult.Fail("refused")))
            { DisplayName = "Fixture", AuthType = AuthenticationType.None, EndpointKind = ConnectorEndpointKind.Url };
            using var scratch = await GateInstallTemplates.CloneAsync(LiftedRequiresGateTests.ResolveStore(storeName), Host, output,
                colocateModuleAggregates: storeName == PostgresGateStore.StoreName,
                extraSettings: new JsonObject
                {
                    ["Health"] = new JsonObject { ["Enabled"] = true },
                    ["Connectivity"] = new JsonObject { ["HealthProbes"] = new JsonObject { ["Enabled"] = true } },
                });
            var app = await GateDrive.InstallAsync(scratch, output, builder => builder.Services.AddSingleton<IConnector>(fixture), start: false);
            try
            {
                await GateDrive.StartNonJobHostedServicesAsync(app);
                var alias = await GateDrive.AliasAsync(app, Host);
                var tenantId = FrameworkTenantId.ToTenantIdOrNull(alias.TenantId)!;

                // The switch reached the contributor through the override file.
                Assert.Equal(1, await GateDrive.InTenantAsync(app, Host, (sp, _) =>
                    Task.FromResult(sp.GetServices<IHealthContributor>().Single(contributor => contributor.ContributorId == ConnectionHealthContributor.Id).Probes.Count)));

                // Two connections, one that will fail - this host serves no HTTP (the transport is not started, so the probe job cannot race the pass), so through the repository in the site's scope.
                var good = await CreateAsync(app, "Good API", "https://good.example.com");
                var bad = await CreateAsync(app, "Bad API", "https://bad.example.com");

                var pass = await GateDrive.InTenantAsync(app, Host, (sp, _) => sp.GetRequiredService<HealthProbePass>().RunAsync(tenantId, HealthProbeScope.Site, alias.SiteId));
                output.WriteLine($"[{storeName}][pass] ran={pass.ProbesRun} written={pass.RowsWritten} worst={pass.Worst}");
                Assert.Equal(HealthState.Unhealthy, pass.Worst);
                Assert.Equal(2, fixture.Calls);

                // ...on the connections...
                var connections = await GateDrive.InTenantAsync(app, Host, async (sp, _) => await sp.GetRequiredService<Stomme.Connectivity.Persistence.IConnectionRepository>().ListForSiteAsync(alias.SiteId));
                Assert.Equal(("Failing", "Healthy"), (connections.Single(c => c.Id == bad).Status.ToString(), connections.Single(c => c.Id == good).Status.ToString()));
                Assert.All(connections, connection => Assert.NotNull(connection.LastTestedUtc));

                // ...and in the Health admin: the alert ledger's subject and the dashboard's subjects.
                var subjects = await GateDrive.InTenantAsync(app, Host, async (sp, _) => await sp.GetRequiredService<IHealthAlertStateRepository>().ListBySiteAsync(alias.SiteId));
                var row = Assert.Single(subjects, subject => subject.Subject.Value == "connectivity:connections");
                Assert.Equal(HealthState.Unhealthy, row.CurrentState);
                var dashboard = await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                    await new GetHealthDashboardQueryHandler(sp.GetRequiredService<IHealthAlertStateRepository>(), sp.GetRequiredService<HealthSnapshot>(), sp.GetRequiredService<SiteState>(), sp.GetRequiredService<ICurrentUser>()).Handle(new(), default));
                Assert.Contains(dashboard.Subjects, subject => subject.State == HealthState.Unhealthy);

                // The failing connection fixed: the next pass turns the subject Healthy and the connection with it.
                await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    var repository = sp.GetRequiredService<Stomme.Connectivity.Persistence.IConnectionRepository>();
                    var connection = (await repository.GetByIdAsync(bad))!;
                    connection.Reconfigure(connection.Name, ConnectorEndpointKind.Url, "https://good-now.example.com", null, connection.CredentialFields, connection.TimeoutSeconds, DateTime.UtcNow);
                    await repository.UpdateAsync(connection);
                    await sp.GetRequiredService<Stomme.SharedKernel.Domain.IUnitOfWork>().SaveChangesAsync();
                    return 0;
                });
                var second = await GateDrive.InTenantAsync(app, Host, (sp, _) => sp.GetRequiredService<HealthProbePass>().RunAsync(tenantId, HealthProbeScope.Site, alias.SiteId));
                Assert.Equal(HealthState.Healthy, second.Worst);
                Assert.Equal(HealthState.Healthy, (await GateDrive.InTenantAsync(app, Host, async (sp, _) => await sp.GetRequiredService<IHealthAlertStateRepository>().ListBySiteAsync(alias.SiteId))).Single(subject => subject.Subject.Value == "connectivity:connections").CurrentState);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static Task<Guid> CreateAsync(WebApplication app, string name, string endpoint) =>
            GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
            {
                var connection = Stomme.Modules.Connectivity.Domain.ConnectionAggregate.Create(Guid.NewGuid(), FrameworkTenantId.ToTenantIdOrNull(alias.TenantId)!, alias.SiteId, name, "fixture",
                    ConnectorEndpointKind.Url, endpoint, null, null, 5, DateTime.UtcNow);
                await sp.GetRequiredService<Stomme.Connectivity.Persistence.IConnectionRepository>().AddAsync(connection);
                await sp.GetRequiredService<Stomme.SharedKernel.Domain.IUnitOfWork>().SaveChangesAsync();
                return connection.Id;
            });
    }
}
