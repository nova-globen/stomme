#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Server;
using Stomme.Shared;
using Stomme.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// A module's hub at <c>/hubs/&lt;name&gt;</c> answers under a site in a folder too - Lek's
    /// <c>/lek/hubs/fuse</c> - with the FOLDER's site resolved, because <c>hubs</c> is one of the routes the host strips a
    /// folder's prefix from (<see cref="Constants.ReservedRoutes"/>). A client builds the URL with
    /// <see cref="Utilities.SiteUrl"/>; the page's base is the host's root, so a relative URL would not carry the folder.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class FolderSiteHubTests
    {
        private const string Route = "/hubs/stomme-tests-site";

        private readonly ITestOutputHelper _output;

        public FolderSiteHubTests(ITestOutputHelper output) => _output = output;

        /// <summary>Answers with the alias the request was resolved to.</summary>
        public sealed class SiteProbeHub : Hub
        {
            public string Site() => (Context.GetHttpContext()?.Items[Constants.HttpContextAliasKey] as Alias)?.Name ?? "";
        }

        /// <summary>A module's endpoint contributor, mapping its hub where a module maps one: at <c>/hubs/&lt;name&gt;</c>.</summary>
        public sealed class SiteProbeContributor : IModuleEndpointContributor
        {
            public void MapEndpoints(IEndpointRouteBuilder endpoints, TransportOptions transports)
            {
                if (transports.SignalR.Enabled)
                {
                    endpoints.MapHub<SiteProbeHub>(Route);
                }
            }
        }

        [Fact]
        public void The_site_url_carries_the_folder_and_nothing_for_a_root_site()
        {
            Assert.Equal("/lek/hubs/fuse", Utilities.SiteUrl("lek", "/hubs/fuse"));
            Assert.Equal("/lek/hubs/fuse", Utilities.SiteUrl("/lek/", "hubs/fuse"));
            Assert.Equal("/hubs/fuse", Utilities.SiteUrl("", "/hubs/fuse"));
            Assert.Equal("/hubs/fuse", Utilities.SiteUrl(null!, "/hubs/fuse"));
            Assert.Contains("hubs", Constants.ReservedRoutes);
        }

        [Fact]
        public async Task A_module_hub_under_a_folder_site_resolves_the_folder_site_and_at_the_root_the_root()
        {
            using var scratch = new FolderSiteCookieTests.Scratch();
            using var env = new FolderSiteCookieTests.EnvironmentVariables(new Dictionary<string, string>
            {
                ["Installation__Sites__0__Name"] = "Svenska",
                ["Installation__Sites__0__Aliases"] = "localhost/sv",
                ["Transport__SignalR__Enabled"] = "true"
            });
            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder => builder.WebHost.UseTestServer());
            var install = await Task.Run(() => Program.InstallAsync(app));
            Assert.True(install.Success && string.IsNullOrEmpty(install.Message), $"the boot failed: {install.Message}");
            await app.StartAsync();
            try
            {
                Assert.Equal("localhost/sv", await SiteAsync(app, "http://localhost" + Utilities.SiteUrl("sv", Route)));
                Assert.Equal("localhost", await SiteAsync(app, "http://localhost" + Utilities.SiteUrl("", Route)));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private async Task<string> SiteAsync(WebApplication app, string url)
        {
            var connection = new HubConnectionBuilder()
                .WithUrl(url, options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => app.GetTestServer().CreateHandler();
                })
                .Build();
            await connection.StartAsync();
            try
            {
                var site = await connection.InvokeAsync<string>(nameof(SiteProbeHub.Site));
                _output.WriteLine($"{url} -> {site}");
                return site;
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}
