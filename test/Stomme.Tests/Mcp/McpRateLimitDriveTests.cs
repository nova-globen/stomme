#nullable enable
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Mcp
{
    /// <summary>
    /// The MCP endpoint's rate limit (BL-80, D101 item 3), driven to a real 429 over HTTP.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The MCP policy exists only when the endpoint does and a limit is configured</b> - which is why the absence is
    /// asserted as well as the presence. The limiter MIDDLEWARE is the host's since BL-121 (the account endpoints'), and
    /// the first case also shows it counts an MCP request ONCE: exactly three served, not fewer.
    /// </para>
    /// <para>
    /// A low limit is configured rather than sending sixty requests: what is being driven is that the policy is WIRED
    /// to this endpoint, and sixty requests would assert the same thing sixty times more slowly.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class McpRateLimitDriveTests
    {
        private const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        public McpRateLimitDriveTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task A_caller_over_the_limit_is_answered_429_and_the_handler_never_runs()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output,
                colocateModuleAggregates: false, extraSettings: Limited(3));
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);

                // The first three are served; the fourth is refused by the LIMITER, before the endpoint.
                var statuses = new System.Collections.Generic.List<HttpStatusCode>();
                for (var i = 0; i < 5; i++)
                {
                    var response = await McpRpc.PostAsync(client, cookies, Host, McpRpc.Body("tools/list", null), _output, $"call {i + 1}");
                    statuses.Add(response.Status);
                }

                _output.WriteLine($"statuses: {string.Join(", ", statuses.Select(status => (int)status))}");
                Assert.Equal(3, statuses.Count(status => status == HttpStatusCode.OK));
                Assert.Equal(2, statuses.Count(status => status == HttpStatusCode.TooManyRequests));

                // A refusal says nothing about the partition or the remaining budget - that is other callers' traffic.
                var refused = await McpRpc.PostAsync(client, cookies, Host, McpRpc.Body("tools/list", null), _output, "refused");
                Assert.Equal(HttpStatusCode.TooManyRequests, refused.Status);
                Assert.DoesNotContain("mcp-server", refused.Body);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task With_RequestsPerMinute_zero_there_is_no_limiter_at_all()
        {
            // Zero means NO limiter - not a limiter permitting zero, which would refuse everything. The difference is
            // the whole reason the value is read rather than passed straight through.
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output,
                colocateModuleAggregates: false, extraSettings: Limited(0));
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);

                for (var i = 0; i < 8; i++)
                {
                    var response = await McpRpc.PostAsync(client, cookies, Host, McpRpc.Body("tools/list", null), _output, $"unlimited {i + 1}");
                    Assert.Equal(HttpStatusCode.OK, response.Status);
                }

                // ...and the MCP endpoint carries NO rate-limiting policy at all, so "no limiter" is ABSENCE and not a
                // generously configured one. (Since BL-121 the HOST always has a limiter - the account endpoints' - so this
                // is asked of the endpoint itself rather than of the host-wide options it used to be asked of.)
                var mcpEndpoints = app.Services.GetServices<Microsoft.AspNetCore.Routing.EndpointDataSource>()
                    .SelectMany(source => source.Endpoints)
                    .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
                    .Where(endpoint => endpoint.RoutePattern.RawText?.Contains("mcp", System.StringComparison.OrdinalIgnoreCase) == true)
                    .ToList();
                Assert.NotEmpty(mcpEndpoints);
                Assert.All(mcpEndpoints, endpoint =>
                    Assert.Null(endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>()));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static JsonObject Limited(int requestsPerMinute) => new()
        {
            ["Mcp"] = new JsonObject
            {
                ["Enabled"] = true,
                ["Server"] = new JsonObject { ["Enabled"] = true, ["RequestsPerMinute"] = requestsPerMinute },
            },
            ["Logging"] = new JsonObject { ["LogLevel"] = new JsonObject { ["Default"] = "Warning" } },
        };
    }
}
