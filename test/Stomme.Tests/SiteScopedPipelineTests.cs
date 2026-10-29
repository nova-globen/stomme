#nullable enable
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Extensions;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Server;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// A module scopes its middleware to its own site with <c>UseWhenSitePath</c> (or
    /// <c>UseWhenSite</c>), and a filter with <c>HttpContext.IsSitePath</c> - driven through the host's real pipeline: an
    /// <see cref="IServerPipelineStartup"/> at <see cref="ServerPipelineStage.BeforeRouting"/>, a root site and a site in the
    /// folder <c>sv</c>.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class SiteScopedPipelineTests
    {
        private const string Header = "X-Stomme-Tests-Site-Scoped";

        /// <summary>A module's pipeline hook that marks responses of the folder site <c>sv</c> only.</summary>
        public sealed class FolderOnlyHook : IServerPipelineStartup
        {
            public ServerPipelineStage Stage => ServerPipelineStage.BeforeRouting;

            public int Order => 0;

            public void Configure(IApplicationBuilder app, IWebHostEnvironment env) =>
                app.UseWhenSitePath("sv", branch => branch.Use(async (context, next) =>
                {
                    context.Response.Headers[Header] = context.IsSitePath("/sv/") ? "sv" : "wrong";
                    await next();
                }));
        }

        [Fact]
        public void The_site_path_match_ignores_slashes_and_case_and_a_root_site_is_the_empty_path()
        {
            var folder = new DefaultHttpContext();
            folder.Items[Constants.HttpContextAliasKey] = new Alias { Name = "example.com/SV" };
            Assert.True(folder.IsSitePath("sv"));
            Assert.True(folder.IsSitePath("/sv/"));
            Assert.False(folder.IsSitePath(""));

            var root = new DefaultHttpContext();
            root.Items[Constants.HttpContextAliasKey] = new Alias { Name = "example.com" };
            Assert.True(root.IsSitePath(""));
            Assert.False(root.IsSitePath("sv"));

            Assert.False(new DefaultHttpContext().IsSitePath("")); // no site resolved: matches nothing
        }

        [Fact]
        public async Task A_hook_scoped_to_the_folder_site_runs_there_and_not_at_the_root()
        {
            using var scratch = new FolderSiteCookieTests.Scratch();
            using var env = new FolderSiteCookieTests.EnvironmentVariables(new Dictionary<string, string>
            {
                ["Installation__Sites__0__Name"] = "Svenska",
                ["Installation__Sites__0__Aliases"] = "localhost/sv"
            });
            var app = await Program.BuildAppAsync(new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" }, builder => builder.WebHost.UseTestServer());
            var install = await Task.Run(() => Program.InstallAsync(app));
            Assert.True(install.Success && string.IsNullOrEmpty(install.Message), $"the boot failed: {install.Message}");
            await app.StartAsync();
            try
            {
                using var client = app.GetTestClient();
                var folder = await client.GetAsync("http://localhost/sv/api/User/authenticate");
                var root = await client.GetAsync("http://localhost/api/User/authenticate");
                Assert.Equal("sv", Value(folder));
                Assert.Null(Value(root));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static string? Value(HttpResponseMessage response) =>
            response.Headers.TryGetValues(Header, out var values) ? string.Join(",", values) : null;
    }
}
