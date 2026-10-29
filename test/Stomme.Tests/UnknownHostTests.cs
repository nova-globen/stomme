#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// Only configured hosts are aliases: a request whose Host names no alias gets a plain 404 and writes nothing.
    /// </summary>
    /// <remarks>
    /// Aliases come from the install config (<c>Installation:DefaultAlias</c>) and the admin UI. The harvested routing lookup
    /// registered ANY unknown host as a new alias on an installation whose aliases all point at one site, so a request with
    /// a forged Host header wrote a row and was then served as that site (measured: <c>127.0.0.1:8080</c> and
    /// <c>unknown.example.com</c> became aliases). On an installation of several sites an unknown host got the
    /// "Site Not Configured" page with 200. Both now answer 404 with an empty body, before any page, API or asset is served.
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class UnknownHostTests
    {
        private const string HostA = "localhost";
        private const string HostB = "b.example.com";

        private static readonly string[] UnknownHosts = { "unknown.example.com", "127.0.0.1:8080", "127.0.0.1" };
        private static readonly string[] Paths = { "/", "/admin", "/api/alias", "/sitemap.xml", "/robots.txt" };

        private readonly ITestOutputHelper _output;

        public UnknownHostTests(ITestOutputHelper output) => _output = output;

        private Task<ScratchGateInstall> CloneAsync() =>
            GateInstallTemplates.CloneAsync(new SqliteGateStore(), HostA, _output, colocateModuleAggregates: false,
                extraSettings: new JsonObject { ["Health"] = new JsonObject { ["Enabled"] = false } });

        [Fact]
        public async Task One_site_an_unknown_host_gets_a_plain_404_and_registers_no_alias()
        {
            using var scratch = await CloneAsync();
            var app = await GateDrive.InstallAsync(scratch, _output, _ => { }, start: false);
            try
            {
                await app.StartAsync();
                var before = await AliasNamesAsync(app);
                _output.WriteLine($"[aliases before] {string.Join(", ", before)}");

                using var client = app.GetTestClient();
                await AssertUnknownHostsAre404Async(client);

                // The one exception: liveness answers for any host (HealthEndpointAnyHostTests has the rest of that contract).
                foreach (var host in UnknownHosts)
                {
                    var (healthStatus, healthBody) = await GetAsync(client, host, "/healthz");
                    Assert.Equal(HttpStatusCode.OK, healthStatus);
                    Assert.Equal("alive", healthBody.Trim());
                }

                var after = await AliasNamesAsync(app);
                _output.WriteLine($"[aliases after] {string.Join(", ", after)}");
                Assert.Equal(before, after);

                // The control: the configured host still renders its site.
                var (status, body) = await GetAsync(client, HostA, "/");
                Assert.True(status == HttpStatusCode.OK, $"the configured host answered {(int)status} '{GateDrive.Truncate(body)}'");
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task Two_sites_an_unknown_host_gets_a_plain_404_not_the_site_not_configured_page()
        {
            using var scratch = await CloneAsync();
            var app = await GateDrive.InstallAsync(scratch, _output, _ => { }, start: false);
            try
            {
                await app.StartAsync();
                await AddSecondSiteAsync(app);
                var before = await AliasNamesAsync(app);
                Assert.Contains(HostB, before);

                using var client = app.GetTestClient();
                await AssertUnknownHostsAre404Async(client);
                Assert.Equal(before, await AliasNamesAsync(app));

                foreach (var host in new[] { HostA, HostB })
                {
                    var (status, body) = await GetAsync(client, host, "/");
                    Assert.True(status == HttpStatusCode.OK, $"the configured host '{host}' answered {(int)status} '{GateDrive.Truncate(body)}'");
                }
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private async Task AssertUnknownHostsAre404Async(HttpClient client)
        {
            foreach (var host in UnknownHosts)
            {
                foreach (var path in Paths)
                {
                    var (status, body) = await GetAsync(client, host, path);
                    Assert.True(status == HttpStatusCode.NotFound, $"Host '{host}' GET {path} answered {(int)status} '{GateDrive.Truncate(body)}'");
                    Assert.Equal(string.Empty, body);
                }
            }
        }

        private async Task<(HttpStatusCode Status, string Body)> GetAsync(HttpClient client, string host, string path)
        {
            var response = await client.GetAsync(new Uri("http://" + host + path));
            var body = await response.Content.ReadAsStringAsync();
            _output.WriteLine($"[Host {host}] GET {path} {(int)response.StatusCode} '{GateDrive.Truncate(body.Trim())}'");
            return (response.StatusCode, body);
        }

        private static async Task<string[]> AliasNamesAsync(WebApplication app)
        {
            using var scope = app.Services.CreateScope();
            var sp = scope.ServiceProvider;
            // Past the alias cache, so a row written by a request is seen even if a cache entry outlived it.
            await sp.GetRequiredService<ICacheManager>().RemoveCacheAsync("Aliases");
            return (await sp.GetRequiredService<IAliasRepository>().GetAliasesAsync())
                .Select(alias => alias.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        }

        /// <summary>A second site on the same tenant with its own alias - the Host's "Add Site" call.</summary>
        private static Task AddSecondSiteAsync(WebApplication app) =>
            GateDrive.InTenantAsync(app, HostA, async (sp, alias) =>
            {
                var site = await sp.GetRequiredService<ISiteRepository>().AddSiteAsync(new Site
                {
                    Name = "Unknown Host Site B",
                    TenantId = alias.TenantId,
                    DefaultThemeType = Constants.DefaultTheme,
                    DefaultContainerType = Constants.DefaultContainer,
                    AdminContainerType = Constants.DefaultAdminContainer,
                    SiteTemplateType = Constants.DefaultSiteTemplate,
                    RenderMode = RenderModes.Static,
                    Runtime = Runtimes.Server,
                    Prerender = false,
                    Hybrid = false,
                    EnhancedNavigation = true,
                    CultureCode = "en",
                    AllowRegistration = false,
                    CaptureBrokenUrls = false,
                    VisitorTracking = false,
                    PwaIsEnabled = false
                });

                await sp.GetRequiredService<IAliasRepository>().AddAliasAsync(new Alias
                {
                    Name = HostB,
                    TenantId = alias.TenantId,
                    SiteId = site.SiteId,
                    IsDefault = true
                });
                return true;
            });
    }
}
