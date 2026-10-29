#nullable enable
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Server;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// M2 row 3 (ruling C33): a module's plain HTTP endpoints (<see cref="IModuleHttpEndpointContributor"/>) are mapped on
    /// every install - no transport switched on - with the request's site resolved, and not at all on a host that switched
    /// the module off (<c>module.&lt;id&gt;.enabled=false</c>, row 14). The broker's cookie-less API at
    /// <c>/fravashi/api/...</c> is the first such module.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class ModuleHttpEndpointTests
    {
        private const string Route = "/stomme-tests/http";

        private readonly ITestOutputHelper _output;

        public ModuleHttpEndpointTests(ITestOutputHelper output) => _output = output;

        /// <summary>A module's minimal API: answers with the site the request resolved to.</summary>
        public sealed class ProbeHttpEndpoints : IModuleHttpEndpointContributor
        {
            public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
                endpoints.MapGet(Route, (HttpContext context) => "site:" + (context.Items[Constants.HttpContextAliasKey] as Alias)?.Name);
        }

        [Fact]
        public async Task A_module_endpoint_answers_with_no_transport_on_and_not_when_the_host_switched_the_module_off()
        {
            Assert.Equal((HttpStatusCode.OK, "site:localhost"), await GetAsync(switchedOff: false));
            Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(switchedOff: true)).status);
        }

        private async Task<(HttpStatusCode status, string body)> GetAsync(bool switchedOff)
        {
            using var scratch = new FolderSiteCookieTests.Scratch();
            var variables = new Dictionary<string, string> { ["Transport__SignalR__Enabled"] = "false", ["Transport__Grpc__Enabled"] = "false" };
            if (switchedOff)
            {
                variables["FeatureManagement__module.stomme-tests.enabled"] = "false";
            }

            using var env = new FolderSiteCookieTests.EnvironmentVariables(variables);
            var app = await Program.BuildAppAsync(new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" }, builder => builder.WebHost.UseTestServer());
            var install = await Task.Run(() => Program.InstallAsync(app));
            Assert.True(install.Success && string.IsNullOrEmpty(install.Message), $"the boot failed: {install.Message}");
            await app.StartAsync();
            try
            {
                using var client = app.GetTestClient();
                var response = await client.GetAsync("http://localhost" + Route);
                var body = await response.Content.ReadAsStringAsync();
                _output.WriteLine($"switched off: {switchedOff}; GET {Route} -> {(int)response.StatusCode} {(body.Length > 80 ? body[..80] : body)}");
                return (response.StatusCode, body);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
