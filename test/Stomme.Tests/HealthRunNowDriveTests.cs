#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Infrastructure.Tenancy;
using Stomme.Modules.Health.Application;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// "Run probes now" over HTTP (BL-83 / D104 items 5 and 8): the only live probing besides the job - <c>health:manage</c>
    /// only, ONE in flight per site, through the job's own pass.
    /// </summary>
    /// <remarks>
    /// Run on SQLite, PostgreSQL and RavenDB, because the pass it triggers probes the install's own store. With the job
    /// switched off (<c>Health:Enabled = false</c>) the run is the ONLY thing that writes a row, so the dashboard's subjects
    /// after it are its own work - and run-now works with probing off, which is when an operator most wants it. PostgreSQL and RavenDB FAIL rather than skip (D46).
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class HealthRunNowDriveTests
    {
        private const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        public HealthRunNowDriveTests(ITestOutputHelper output) => _output = output;

        public static TheoryData<string> Stores() => new() { SqliteGateStore.StoreName, PostgresGateStore.StoreName, RavenGateStore.StoreName };

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task A_manager_runs_the_probes_now_and_a_second_run_in_flight_is_refused(string storeName)
        {
            var store = LiftedRequiresGateTests.ResolveStore(storeName);
            Assert.True(store.IsReachable(), store.UnreachableMessage);

            // The job switched OFF, so the run is the only thing that writes a row (the shipped default is ON).
            var jobOff = new System.Text.Json.Nodes.JsonObject { ["Health"] = new System.Text.Json.Nodes.JsonObject { ["Enabled"] = false } };
            using var scratch = new ScratchGateInstall(store, Host, colocateModuleAggregates: storeName == PostgresGateStore.StoreName, jobOff);
            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);

                // Nothing probed yet: the job is switched off.
                Assert.Empty(Subjects((await GateDrive.GetAsync(client, cookies, Host, "/api/Health", _output, $"[{storeName}] before")).Body));

                // ---- the run ----------------------------------------------------------------------------------------------
                var run = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, "/api/Health/run", new { }, _output, $"[{storeName}] run now");
                Assert.Equal(HttpStatusCode.OK, run.Status);
                using (var parsed = JsonDocument.Parse(run.Body))
                {
                    Assert.True(parsed.RootElement.GetProperty("probesRun").GetInt32() >= 2, run.Body);
                }

                var after = Subjects((await GateDrive.GetAsync(client, cookies, Host, "/api/Health", _output, $"[{storeName}] after")).Body);
                _output.WriteLine($"[{storeName}] subjects after the run: {string.Join(", ", after)}");
                Assert.Contains("store", after);                                     // the install's OWN store, probed
                Assert.Contains("cache", after);

                // ---- one in flight per site: a held gate answers 409, and releasing it lets the next run through ----------
                var alias = await GateDrive.AliasAsync(app, Host);
                var tenant = FrameworkTenantId.ToTenantIdOrNull(alias.TenantId)!.Value;
                var gate = app.Services.GetRequiredService<HealthRunNowGate>();
                Assert.True(gate.TryEnter(tenant, alias.SiteId));
                try
                {
                    var busy = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, "/api/Health/run", new { }, _output, $"[{storeName}] run while one is in flight");
                    Assert.Equal(HttpStatusCode.Conflict, busy.Status);
                }
                finally
                {
                    gate.Exit(tenant, alias.SiteId);
                }

                Assert.Equal(HttpStatusCode.OK, (await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, "/api/Health/run", new { }, _output, $"[{storeName}] run after release")).Status);

                // ---- and nobody else may probe ------------------------------------------------------------------------------
                var plain = await GateDrive.AddUserAsync(app, Host, "health-plain", "Pla1n!Health$2026", "health-plain@gate.test");
                var plainClient = GateDrive.NewClient(app, Host);
                var plainCookies = await GateDrive.LoginAsync(app, plainClient, Host, "health-plain", "Pla1n!Health$2026", _output);
                var refused = await GateDrive.SendJsonAsync(app, plainClient, plainCookies, Host, plain.UserId, HttpMethod.Post, "/api/Health/run", new { }, _output, $"[{storeName}] plain user");
                Assert.True(refused.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"plain user got {(int)refused.Status}");

                var anonymous = await GateDrive.SendJsonAsync(app, GateDrive.NewClient(app, Host), null, Host, null, HttpMethod.Post, "/api/Health/run", new { }, _output, $"[{storeName}] anonymous");
                Assert.True(anonymous.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"anonymous got {(int)anonymous.Status}");
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static string[] Subjects(string dashboardJson)
        {
            using var parsed = JsonDocument.Parse(dashboardJson);
            return parsed.RootElement.GetProperty("subjects").EnumerateArray()
                .Select(subject => subject.GetProperty("dependency").GetString() ?? string.Empty)
                .ToArray();
        }
    }
}
