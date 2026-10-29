using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// <c>api/Health</c> tells the page whether probing is ON (found by a browser drive): with
    /// <c>Health:Enabled = false</c> - what the shipped configuration carried while probing was incomplete, and what an operator
    /// can still set - the page promised a tick "every minute" that the job, logging "Health Probing Is Disabled", would never
    /// deliver.
    /// </summary>
    /// <remarks>
    /// Driven over HTTP on a real install, because the handler must read the MERGED configuration (the override file
    /// where an install keeps the switch), and only a real host composes that.
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class HealthDashboardSwitchTests
    {
        private const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        public HealthDashboardSwitchTests(ITestOutputHelper output) => _output = output;

        [Theory]
        [InlineData(null, true)]       // the shipped appsettings.json: Health:Enabled = true (the default)
        [InlineData(false, false)]     // switched OFF in the override file, where a deployed install keeps it
        public async Task The_dashboard_carries_whether_probing_is_ON(bool? enabled, bool expected)
        {
            var settings = enabled is null ? null : new JsonObject { ["Health"] = new JsonObject { ["Enabled"] = enabled.Value } };
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output, colocateModuleAggregates: false, extraSettings: settings);
            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);
                var response = await GateDrive.GetAsync(client, cookies, Host, "/api/Health", _output, $"host: api/Health (Health:Enabled={enabled?.ToString() ?? "shipped"})");

                Assert.Equal(HttpStatusCode.OK, response.Status);
                using var parsed = JsonDocument.Parse(response.Body);
                Assert.Equal(expected, parsed.RootElement.GetProperty("probingEnabled").GetBoolean());
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
