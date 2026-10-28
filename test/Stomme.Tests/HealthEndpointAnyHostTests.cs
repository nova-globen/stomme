#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Modules.Health.Infrastructure;
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
    /// The health endpoints answer for ANY Host header, and a probe never registers an alias.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A container healthcheck (<c>curl http://127.0.0.1:8080/healthz</c>) and an external monitor send a Host that names no
    /// site, and the public repository may not name a real one. Liveness must still answer <c>200 alive</c> on an
    /// installation of SEVERAL sites, where an unknown host resolves no alias at all.
    /// </para>
    /// <para>
    /// <b>The alias half.</b> On an installation whose aliases all point at ONE site, <c>AliasRepository.GetAliasAsync(url)</c>
    /// registers any unknown host as a new alias of that site - so before the fix every healthcheck with
    /// <c>Host: 127.0.0.1:8080</c> wrote an alias row <c>127.0.0.1:8080</c> (measured). Tenant resolution is now skipped
    /// for the health paths, which need no tenant: they answer from the process and from an in-memory snapshot.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class HealthEndpointAnyHostTests
    {
        private const string HostA = "localhost";
        private const string HostB = "b.example.com";

        private static readonly string[] ProbeHosts = { HostA, HostB, "unknown.example.com", "127.0.0.1:8080", "127.0.0.1" };

        private readonly ITestOutputHelper _output;

        public HealthEndpointAnyHostTests(ITestOutputHelper output) => _output = output;

        private Task<ScratchGateInstall> CloneWithProbingOffAsync() =>
            GateInstallTemplates.CloneAsync(new SqliteGateStore(), HostA, _output, colocateModuleAggregates: false,
                extraSettings: new JsonObject { ["Health"] = new JsonObject { ["Enabled"] = false } });

        [Fact]
        public async Task Two_sites_liveness_answers_200_alive_for_every_host_and_no_alias_is_created()
        {
            using var scratch = await CloneWithProbingOffAsync();
            var app = await GateDrive.InstallAsync(scratch, _output, _ => { }, start: false);
            try
            {
                await app.StartAsync();
                await AddSecondSiteAsync(app);

                var before = await AliasNamesAsync(app);
                _output.WriteLine($"[aliases before] {string.Join(", ", before)}");
                Assert.Contains(HostB, before);

                using var client = app.GetTestClient();
                foreach (var host in ProbeHosts)
                {
                    var (status, body) = await GetAsync(client, host, HealthEndpoints.LivenessPath);
                    Assert.True(status == HttpStatusCode.OK, $"/healthz with Host '{host}' answered {(int)status} '{GateDrive.Truncate(body)}'");
                    Assert.Equal("alive", body.Trim());

                    // Readiness is a verdict, not a liveness signal: with probing off it is 503 - but one word, for every host,
                    // never a page or a 404.
                    var (readyStatus, readyBody) = await GetAsync(client, host, HealthEndpoints.ReadinessPath);
                    Assert.True(readyStatus == HttpStatusCode.ServiceUnavailable, $"/readyz with Host '{host}' answered {(int)readyStatus} '{GateDrive.Truncate(readyBody)}'");
                    Assert.Equal("not-ready", readyBody.Trim());
                }

                var after = await AliasNamesAsync(app);
                _output.WriteLine($"[aliases after] {string.Join(", ", after)}");
                Assert.Equal(before, after);

                // Tenant resolution for a normal page is untouched: a known alias still resolves, an unknown host still does not.
                var (pageStatus, _) = await GetAsync(client, HostB, "/");
                Assert.Equal(HttpStatusCode.OK, pageStatus);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task One_site_a_healthcheck_from_an_unknown_host_registers_no_alias()
        {
            using var scratch = await CloneWithProbingOffAsync();
            var app = await GateDrive.InstallAsync(scratch, _output, _ => { }, start: false);
            try
            {
                await app.StartAsync();
                var before = await AliasNamesAsync(app);
                _output.WriteLine($"[aliases before] {string.Join(", ", before)}");

                using var client = app.GetTestClient();
                foreach (var host in new[] { "127.0.0.1:8080", "unknown.example.com" })
                {
                    var (status, body) = await GetAsync(client, host, HealthEndpoints.LivenessPath);
                    Assert.Equal(HttpStatusCode.OK, status);
                    Assert.Equal("alive", body.Trim());
                    await GetAsync(client, host, HealthEndpoints.ReadinessPath);
                }

                var after = await AliasNamesAsync(app);
                _output.WriteLine($"[aliases after] {string.Join(", ", after)}");
                Assert.Equal(before, after);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task With_the_https_redirect_on_liveness_still_answers_over_http_while_a_page_is_redirected()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), HostA, _output, colocateModuleAggregates: false,
                extraSettings: new JsonObject
                {
                    ["Health"] = new JsonObject { ["Enabled"] = false },
                    // A flat key: the override's top-level sections REPLACE the installer's, and "Installation" carries the
                    // install's alias and host account. JSON configuration reads "a:b" as the same key path as a nested object.
                    ["Installation:UseHttpsRedirection"] = true,
                    ["https_port"] = 8443,
                });
            var app = await GateDrive.InstallAsync(scratch, _output, _ => { }, start: false);
            try
            {
                await app.StartAsync();
                using var client = app.GetTestClient();

                var (status, body) = await GetAsync(client, "127.0.0.1:8080", HealthEndpoints.LivenessPath);
                Assert.Equal(HttpStatusCode.OK, status);
                Assert.Equal("alive", body.Trim());

                // The control: the redirect IS on, so a page over http is sent to https - the exemption is the health paths' alone.
                var (pageStatus, _) = await GetAsync(client, HostA, "/");
                Assert.True(pageStatus is HttpStatusCode.TemporaryRedirect or HttpStatusCode.Redirect or HttpStatusCode.PermanentRedirect,
                    $"a page over http answered {(int)pageStatus} with the https redirect on - the control did not engage, so the case proves nothing");
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private async Task<(HttpStatusCode Status, string Body)> GetAsync(HttpClient client, string host, string path)
        {
            var response = await client.GetAsync(new Uri("http://" + host + path));
            var body = await response.Content.ReadAsStringAsync();
            var location = response.Headers.Location is null ? "" : $" -> {response.Headers.Location}";
            _output.WriteLine($"[Host {host}] GET {path} {(int)response.StatusCode}{location} '{GateDrive.Truncate(body.Trim())}'");
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
                    Name = "Health Site B",
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
