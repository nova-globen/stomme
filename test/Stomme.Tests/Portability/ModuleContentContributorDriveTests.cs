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
using Stomme.Infrastructure.Security;
using Stomme.Models;
using Stomme.Modules.HtmlText.Repository;
using Stomme.Modules.Portability.Contributions;
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
    /// BL-82 (refutation 10) - a placed module's content through its own <c>IPortable</c>, driven over HTTP
    /// with HtmlText on an installed site: exported without its private settings, imported back onto the module in the same
    /// place - Skip leaves it, Overwrite restores it - and refused before anything changes when the package would overwrite
    /// a private setting, or when the importer holds the import key but not Edit on the page.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class ModuleContentContributorDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        [Fact]
        public async Task Module_content_round_trips_and_page_Edit_and_private_settings_are_enforced()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false,
                extraSettings: new JsonObject { ["Portability"] = new JsonObject { ["Enabled"] = true } });
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);

                // An importer: holds portability:import through a role, and Edit on no page.
                var importers = await GateDrive.EnsureRoleAsync(app, Host, "Importers");
                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    await sp.GetRequiredService<IPermissionRepository>().AddPermissionAsync(new Permission
                    {
                        SiteId = alias.SiteId, EntityName = PortabilityPermissions.Resource, EntityId = PermissionRowAccessGuard.KeyEntityId,
                        PermissionName = "import", RoleId = importers.RoleId, IsAuthorized = true,
                    });
                    return true;
                });
                var importer = await GateDrive.AddUserAsync(app, Host, "bl82-importer", "Bl82!Import$2026", "bl82-importer@gate.test", "Importers");

                using var host = GateDrive.NewClient(app, Host);
                var hostCookies = await GateDrive.LoginAsync(app, host, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                using var importerClient = GateDrive.NewClient(app, Host);
                var importerCookies = await GateDrive.LoginAsync(app, importerClient, Host, "bl82-importer", "Bl82!Import$2026", output);

                // The home page's first HtmlText module, with a PRIVATE setting that must never travel.
                var moduleId = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var placed = (await sp.GetRequiredService<IPageModuleRepository>().GetPageModulesAsync(alias.SiteId))
                        .Where(pm => !pm.IsDeleted && pm.Module?.ModuleDefinitionName?.Contains("HtmlText", StringComparison.Ordinal) == true)
                        .OrderBy(pm => pm.PageModuleId).First();
                    await sp.GetRequiredService<ISettingRepository>().AddSettingAsync(new Setting
                    {
                        EntityName = EntityNames.Module, EntityId = placed.ModuleId, SettingName = "PortabilitySecret", SettingValue = "S3CRET-VALUE", IsPrivate = true,
                    });
                    return placed.ModuleId;
                });

                // ---- export, by the real job --------------------------------------------------------------------------------
                var queued = await GateDrive.SendJsonAsync(app, host, hostCookies, Host, hostId, HttpMethod.Post, "/api/Portability/config",
                    new PortabilityConfigRequest { EntityTypes = new List<string> { ModuleContentContributor.Type } }, output, "export module content");
                Assert.Equal(HttpStatusCode.OK, queued.Status);
                var packageId = JsonDocument.Parse(queued.Body).RootElement.GetProperty("id").GetGuid();
                await PortabilityDrive.BuildQueuedAsync(app, Host, output);
                var package = await GateDrive.GetAsync(host, hostCookies, Host, $"/api/Portability/{packageId}/download", output, "download");
                Assert.Equal(HttpStatusCode.OK, package.Status);

                var entities = JsonNode.Parse(package.Body)!["entities"]!.AsArray();
                Assert.Contains(entities, e => (string?)e!["entityType"] == ModuleContentContributor.Type && (string?)e["entityId"] == moduleId.ToString());
                Assert.DoesNotContain(entities, e => ((string?)e!["payload"] ?? "").Contains("admin/", StringComparison.Ordinal));
                Assert.DoesNotContain("S3CRET-VALUE", package.Body, StringComparison.Ordinal);

                // ---- change it, then Skip and Overwrite ---------------------------------------------------------------------
                await SetContentAsync(app, moduleId, "<p>CHANGED-BY-THE-DRIVE</p>");

                var skip = await ImportAsync(app, host, hostCookies, hostId, package.Body, ConflictStrategy.Skip, "import, Skip");
                Assert.True(skip.GetProperty("accepted").GetBoolean());
                Assert.Equal("Skipped", Line(skip, moduleId).GetProperty("result").GetString());
                Assert.Equal("<p>CHANGED-BY-THE-DRIVE</p>", await ContentAsync(app, moduleId));

                var overwrite = await ImportAsync(app, host, hostCookies, hostId, package.Body, ConflictStrategy.Overwrite, "import, Overwrite");
                Assert.True(overwrite.GetProperty("accepted").GetBoolean(), overwrite.ToString());
                Assert.Equal("Overwritten", Line(overwrite, moduleId).GetProperty("result").GetString());
                Assert.NotEqual("<p>CHANGED-BY-THE-DRIVE</p>", await ContentAsync(app, moduleId));

                // ---- refused before anything changes ------------------------------------------------------------------------
                await SetContentAsync(app, moduleId, "<p>CHANGED-AGAIN</p>");

                var noEdit = await ImportAsync(app, importerClient, importerCookies, importer.UserId, package.Body, ConflictStrategy.Overwrite, "an importer without page Edit");
                Assert.False(noEdit.GetProperty("accepted").GetBoolean());
                Assert.Contains(noEdit.GetProperty("refusals").EnumerateArray(), r => r.GetString()!.Contains("may not edit the page", StringComparison.Ordinal));

                var clash = await ImportAsync(app, host, hostCookies, hostId, WithSetting(package.Body, moduleId, "PortabilitySecret", "evil"), ConflictStrategy.Overwrite, "a package naming a private setting");
                Assert.False(clash.GetProperty("accepted").GetBoolean());
                Assert.Contains(clash.GetProperty("refusals").EnumerateArray(), r => r.GetString()!.Contains("private settings", StringComparison.Ordinal));

                Assert.Equal("<p>CHANGED-AGAIN</p>", await ContentAsync(app, moduleId));
                var secret = await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                    (await sp.GetRequiredService<ISettingRepository>().GetSettingAsync(EntityNames.Module, moduleId, "PortabilitySecret"))!.SettingValue);
                Assert.Equal("S3CRET-VALUE", secret);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static Task<bool> SetContentAsync(WebApplication app, int moduleId, string content) =>
            GateDrive.InTenantAsync(app, Host, async (sp, _) =>
            {
                await sp.GetRequiredService<IHtmlTextRepository>().AddHtmlTextAsync(new Stomme.Modules.HtmlText.Models.HtmlText { ModuleId = moduleId, Content = content });
                return true;
            });

        private static Task<string> ContentAsync(WebApplication app, int moduleId) =>
            GateDrive.InTenantAsync(app, Host, async (sp, _) => (await sp.GetRequiredService<IHtmlTextRepository>().GetHtmlTextAsync(moduleId)).Content);

        private static JsonElement Line(JsonElement report, int moduleId) =>
            report.GetProperty("lines").EnumerateArray().Single(line => line.GetProperty("entityType").GetString() == ModuleContentContributor.Type && line.GetProperty("entityId").GetString() == moduleId.ToString());

        /// <summary>The package with one module's exported settings naming <paramref name="name"/> - a hand-edited package.</summary>
        private static string WithSetting(string package, int moduleId, string name, string value)
        {
            var document = JsonNode.Parse(package)!;
            var entity = document["entities"]!.AsArray().Single(e => (string?)e!["entityType"] == ModuleContentContributor.Type && (string?)e["entityId"] == moduleId.ToString())!;
            var payload = JsonNode.Parse((string)entity["payload"]!)!;
            var content = JsonNode.Parse((string)payload["Content"]!)!;
            content["Settings"] ??= new JsonObject();
            content["Settings"]![name] = value;
            payload["Content"] = content.ToJsonString();
            entity["payload"] = payload.ToJsonString();
            return document.ToJsonString();
        }

        private static async Task<JsonElement> ImportAsync(WebApplication app, HttpClient client, System.Net.CookieContainer cookies, int userId, string body, ConflictStrategy strategy, string label)
        {
            var (token, antiforgery) = GateDrive.Antiforgery(app, userId);
            var uri = new Uri(new Uri("http://" + Host), $"/api/Portability/import?strategy={strategy}");
            using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.TryAddWithoutValidation("Cookie", cookies.GetCookieHeader(uri) + "; " + antiforgery);
            request.Headers.TryAddWithoutValidation(Constants.AntiForgeryTokenHeaderName, token);
            using var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"[{label}] {(int)response.StatusCode} {text}");
            return JsonDocument.Parse(text).RootElement.Clone();
        }
    }
}
