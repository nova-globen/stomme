#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Modules.Portability.Execution;
using Stomme.Portability;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Portability
{
    /// <summary>
    /// Driven over HTTP on an installed site, per store: who may build,
    /// see, download and import what. A person gets their OWN data package without anyone's help and nobody else can
    /// download it; a configuration package goes back in with every entity skipped; an Administrator who is not a Host
    /// cannot request the Host's data; an import over the cap is refused before it is read.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public abstract class PortabilityHttpDriveTests
    {
        private const string Host = "localhost";
        private const int Cap = 65_536;

        private readonly ITestOutputHelper _output;

        protected PortabilityHttpDriveTests(ITestOutputHelper output) => _output = output;

        private protected abstract IGateStore Store();

        private static JsonObject Settings(bool enabled) => new()
        {
            ["Portability"] = new JsonObject { ["Enabled"] = enabled, ["MaxPackageBytes"] = Cap },
        };

        [Fact]
        public async Task Who_may_build_see_download_and_import_what()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(Store(), Host, _output, colocateModuleAggregates: false, extraSettings: Settings(enabled: true));
            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                using var host = GateDrive.NewClient(app, Host);
                var hostCookies = await GateDrive.LoginAsync(app, host, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);

                var plainUser = await GateDrive.AddUserAsync(app, Host, "bl82-plain", "Bl82!Plain$2026", "bl82-plain@gate.test");
                using var plain = GateDrive.NewClient(app, Host);
                var plainCookies = await GateDrive.LoginAsync(app, plain, Host, "bl82-plain", "Bl82!Plain$2026", _output);

                await GateDrive.AddUserAsync(app, Host, "bl82-other", "Bl82!Other$2026", "bl82-other@gate.test");
                var otherId = await GateDrive.UserIdAsync(app, Host, "bl82-other");
                using var other = GateDrive.NewClient(app, Host);
                var otherCookies = await GateDrive.LoginAsync(app, other, Host, "bl82-other", "Bl82!Other$2026", _output);

                await GateDrive.AddUserAsync(app, Host, "bl82-admin", "Bl82!Admin$2026", "bl82-admin@gate.test", RoleNames.Admin);
                var adminId = await GateDrive.UserIdAsync(app, Host, "bl82-admin");
                using var admin = GateDrive.NewClient(app, Host);
                var adminCookies = await GateDrive.LoginAsync(app, admin, Host, "bl82-admin", "Bl82!Admin$2026", _output);

                // ---- requests ----------------------------------------------------------------------------------------------
                var config = await GateDrive.SendJsonAsync(app, host, hostCookies, Host, hostId, HttpMethod.Post, "/api/Portability/config",
                    new PortabilityConfigRequest { EntityTypes = new List<string> { "profile-definition" } }, _output, "host builds configuration");
                Assert.Equal(HttpStatusCode.OK, config.Status);
                var configId = Id(config.Body);

                var mine = await GateDrive.SendJsonAsync(app, plain, plainCookies, Host, plainUser.UserId, HttpMethod.Post, "/api/Portability/mine", new { }, _output, "a person asks for their own data");
                Assert.Equal(HttpStatusCode.OK, mine.Status);
                var mineId = Id(mine.Body);

                var plainConfig = await GateDrive.SendJsonAsync(app, plain, plainCookies, Host, plainUser.UserId, HttpMethod.Post, "/api/Portability/config",
                    new PortabilityConfigRequest { EntityTypes = new List<string> { "profile-definition" } }, _output, "a person tries a configuration build");
                Assert.True(plainConfig.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"a plain user got {(int)plainConfig.Status}");

                // The stock host holds only GLOBAL roles, so it is not a person "on this site" at all (refutation 11 - answered
                // "no such person"). Give it a SITE role, and it is - and then only a Host may ask for its data. (Without keeping the
                // stamp, the grant would sign the host out: a role change rotates the security stamp by design.)
                var hostsFirst = await GateDrive.SendJsonAsync(app, admin, adminCookies, Host, adminId, HttpMethod.Post, "/api/Portability/subject",
                    new PortabilitySubjectRequest { Username = GateDrive.HostUsername }, _output, "an administrator asks for the host's data (global roles only)");
                Assert.Equal(HttpStatusCode.BadRequest, hostsFirst.Status);
                Assert.Contains("No such person", hostsFirst.Body, StringComparison.Ordinal);
                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var registered = (await sp.GetRequiredService<IRoleRepository>().GetRolesAsync(alias.SiteId)).Single(r => r.Name == RoleNames.Registered);
                    await sp.GetRequiredService<IUserRoleRepository>().AddUserRoleAsync(new Stomme.Models.UserRole { UserId = hostId, RoleId = registered.RoleId, IgnoreSecurityStamp = true });
                    return true;
                });
                var hostsData = await GateDrive.SendJsonAsync(app, admin, adminCookies, Host, adminId, HttpMethod.Post, "/api/Portability/subject",
                    new PortabilitySubjectRequest { Username = GateDrive.HostUsername }, _output, "an administrator asks for the host's data");
                Assert.Equal(HttpStatusCode.BadRequest, hostsData.Status);
                Assert.Contains("Only a host", hostsData.Body, StringComparison.Ordinal);
                var hostAsksForHost = await GateDrive.SendJsonAsync(app, host, hostCookies, Host, hostId, HttpMethod.Post, "/api/Portability/subject",
                    new PortabilitySubjectRequest { Username = GateDrive.HostUsername }, _output, "the host asks for the host's data");
                Assert.Equal(HttpStatusCode.OK, hostAsksForHost.Status);

                var othersData = await GateDrive.SendJsonAsync(app, admin, adminCookies, Host, adminId, HttpMethod.Post, "/api/Portability/subject",
                    new PortabilitySubjectRequest { Username = "bl82-other" }, _output, "an administrator asks for a person's data");
                Assert.Equal(HttpStatusCode.OK, othersData.Status);
                var othersId = Id(othersData.Body);

                // ---- built by the real job ---------------------------------------------------------------------------------
                await PortabilityDrive.BuildQueuedAsync(app, Host, _output);

                // ---- seeing ------------------------------------------------------------------------------------------------
                var plainList = await GateDrive.GetAsync(plain, plainCookies, Host, "/api/Portability", _output, "a person lists");
                Assert.Equal(HttpStatusCode.OK, plainList.Status);
                Assert.Equal(new[] { mineId }, Ids(plainList.Body));

                var hostList = await GateDrive.GetAsync(host, hostCookies, Host, "/api/Portability", _output, "the host lists");
                Assert.Contains(configId, Ids(hostList.Body));
                Assert.Contains(mineId, Ids(hostList.Body));

                // ---- downloading -------------------------------------------------------------------------------------------
                var own = await GateDrive.GetAsync(plain, plainCookies, Host, $"/api/Portability/{mineId}/download", _output, "a person downloads their own");
                Assert.Equal(HttpStatusCode.OK, own.Status);
                Assert.Contains("bl82-plain@gate.test", own.Body, StringComparison.Ordinal);

                var notTheirs = await GateDrive.GetAsync(other, otherCookies, Host, $"/api/Portability/{mineId}/download", _output, "another person downloads it");
                Assert.Equal(HttpStatusCode.NotFound, notTheirs.Status);
                using (var anonymous = GateDrive.NewClient(app, Host))
                {
                    var anon = await GateDrive.GetAsync(anonymous, null, Host, $"/api/Portability/{mineId}/download", _output, "anonymous downloads it");
                    Assert.True(anon.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound, $"anonymous got {(int)anon.Status}");
                }

                // The subject of an administrator's request may download their own copy too.
                var subjectCopy = await GateDrive.GetAsync(other, otherCookies, Host, $"/api/Portability/{othersId}/download", _output, "the subject downloads");
                Assert.Equal(HttpStatusCode.OK, subjectCopy.Status);

                var package = await GateDrive.GetAsync(host, hostCookies, Host, $"/api/Portability/{configId}/download", _output, "the host downloads configuration");
                Assert.Equal(HttpStatusCode.OK, package.Status);
                Assert.Contains("\"profile-definition\"", package.Body, StringComparison.Ordinal);

                // ---- importing ---------------------------------------------------------------------------------------------
                var back = await PostRawAsync(app, host, hostCookies, hostId, "/api/Portability/import?strategy=Skip", package.Body, "the host imports it back");
                Assert.Equal(HttpStatusCode.OK, back.Status);
                var report = JsonDocument.Parse(back.Body).RootElement;
                Assert.True(report.GetProperty("accepted").GetBoolean(), back.Body);
                Assert.All(report.GetProperty("lines").EnumerateArray(), line => Assert.Equal("Skipped", line.GetProperty("result").GetString()));

                var tooBig = await PostRawAsync(app, host, hostCookies, hostId, "/api/Portability/import?strategy=Skip", new string(' ', Cap + 10), "an import over the cap");
                Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.Status);

                var plainImport = await PostRawAsync(app, plain, plainCookies, plainUser.UserId, "/api/Portability/import?strategy=Skip", package.Body, "a person imports");
                Assert.True(plainImport.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"a plain user got {(int)plainImport.Status}");

                // ---- the page ----------------------------------------------------------------------------------------------
                Assert.Equal(HttpStatusCode.OK, (await GateDrive.GetAsync(host, hostCookies, Host, "/admin/portability", _output, "the admin page")).Status);
                _ = otherId;
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private async Task<(HttpStatusCode Status, string Body)> PostRawAsync(WebApplication app, HttpClient client, System.Net.CookieContainer cookies, int userId, string path, string body, string label)
        {
            var (token, antiforgery) = GateDrive.Antiforgery(app, userId);
            var uri = new Uri(new Uri("http://" + Host), path);
            using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.TryAddWithoutValidation("Cookie", cookies.GetCookieHeader(uri) + "; " + antiforgery);
            request.Headers.TryAddWithoutValidation(Constants.AntiForgeryTokenHeaderName, token);
            using var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            _output.WriteLine($"[{label}] POST {path} -> {(int)response.StatusCode} {GateDrive.Truncate(text)}");
            return (response.StatusCode, text);
        }

        private static Guid Id(string json) => JsonDocument.Parse(json).RootElement.GetProperty("id").GetGuid();

        private static List<Guid> Ids(string json) => JsonDocument.Parse(json).RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
    }

    public sealed class SqlitePortabilityHttpDriveTests(ITestOutputHelper output) : PortabilityHttpDriveTests(output)
    {
        private protected override IGateStore Store() => new SqliteGateStore();
    }

    [Trait("Category", "ExternalDatabase")]
    public sealed class PostgresPortabilityHttpDriveTests(ITestOutputHelper output) : PortabilityHttpDriveTests(output)
    {
        private protected override IGateStore Store() => new PostgresGateStore();
    }

    [Trait("Category", "ExternalDatabase")]
    public sealed class RavenPortabilityHttpDriveTests(ITestOutputHelper output) : PortabilityHttpDriveTests(output)
    {
        private protected override IGateStore Store() => new RavenGateStore();
    }

    /// <summary>Switched off, portability has no surface: every route 404, no admin page.</summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class PortabilityDisabledSurfaceTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        [Fact]
        public async Task Switched_off_every_route_is_404_and_there_is_no_page()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false,
                extraSettings: new JsonObject { ["Portability"] = new JsonObject { ["Enabled"] = false } });
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                using var host = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, host, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);

                Assert.Equal(HttpStatusCode.NotFound, (await GateDrive.GetAsync(host, cookies, Host, "/api/Portability", output, "list")).Status);
                Assert.Equal(HttpStatusCode.NotFound, (await GateDrive.GetAsync(host, cookies, Host, $"/api/Portability/{Guid.NewGuid()}/download", output, "download")).Status);
                // A POST no controller matches falls through to the host's page fallback, which answers every unknown POST the
                // same way (400) - so "no surface" for a POST is: exactly the answer a route that never existed gets.
                var unknown = await GateDrive.SendJsonAsync(app, host, cookies, Host, hostId, HttpMethod.Post, "/api/NoSuchSurface/mine", new { }, output, "a route that never existed");
                var mine = await GateDrive.SendJsonAsync(app, host, cookies, Host, hostId, HttpMethod.Post, "/api/Portability/mine", new { }, output, "mine");
                Assert.NotEqual(HttpStatusCode.OK, mine.Status);
                Assert.Equal(unknown.Status, mine.Status);

                var page = await GateDrive.InTenantAsync(app, Host, async (sp, alias) => await sp.GetRequiredService<IPageRepository>().GetPageAsync("admin/portability", alias.SiteId));
                Assert.Null(page);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
