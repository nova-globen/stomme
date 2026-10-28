#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Formats;
using Stomme.Models;
using Stomme.Modules.Formats.Domain;
using Stomme.Repository;
using Stomme.Security;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Formats
{
    /// <summary>
    /// BL-94 increment 2 (D110) - the Formats module on an installed host, over real HTTP, on SQLite, PostgreSQL and
    /// RavenDB: the admin page exists from the module's own template and renders for the Host; the API lists the presets
    /// and the site's own definitions, creates, updates and deletes a definition (the predecessor platform's FormatApplicationTests and
    /// FormatsApiTests cases travel by name below); a save over a preset's id, a duplicate name, a blank pattern for a
    /// number and an unknown culture are refused as the caller's input; a preset cannot be deleted; a registered user
    /// holds neither key and is refused; a user granted formats:manage through the key's Permission row may save.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class FormatsModuleDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        public static TheoryData<string> Stores() => new() { SqliteGateStore.StoreName, PostgresGateStore.StoreName, RavenGateStore.StoreName };

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task The_page_renders_and_the_api_lists_saves_and_deletes_under_its_keys(string storeName)
        {
            using var scratch = await GateInstallTemplates.CloneAsync(LiftedRequiresGateTests.ResolveStore(storeName), Host, output, colocateModuleAggregates: storeName == PostgresGateStore.StoreName, extraSettings: null);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                await GateDrive.AddUserAsync(app, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, GateDrive.PlainEmail);
                await GateDrive.EnsureRoleAsync(app, Host, GateDrive.GateRole);
                var editor = await GateDrive.AddUserAsync(app, Host, GateDrive.GateUsername, GateDrive.GatePassword, GateDrive.GateEmail, GateDrive.GateRole);

                // The admin page, from the module's own template.
                Assert.NotNull(await GateDrive.InTenantAsync(app, Host, async (sp, alias) => await sp.GetRequiredService<IPageRepository>().GetPageAsync("admin/formats", alias.SiteId)));
                using var hostClient = GateDrive.NewClient(app, Host);
                var hostCookies = await GateDrive.LoginAsync(app, hostClient, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                var page = await GateDrive.GetAsync(hostClient, hostCookies, Host, "/admin/formats", output, "the formats page");
                Assert.Equal(HttpStatusCode.OK, page.Status);
                Assert.Contains("Formats", page.Body);

                // Service_List_MergesSystemAndTenant / Catalogue: the presets are listed, system-scoped.
                var presets = await ListAsync(hostClient, hostCookies, "?scope=System");
                Assert.True(presets.Count >= 30, $"{storeName}: {presets.Count} presets");
                Assert.All(presets, format => Assert.Equal(FormatScope.System, format.Scope));

                // Save_CreatesTenantFormat_ThenListIncludesIt
                var created = await SaveAsync(app, hostClient, hostCookies, hostId, new FormatDefinitionRequest { Name = "My date", Kind = FormatKind.Date, Pattern = "yyyy/MM/dd", Culture = "en-US" });
                Assert.Equal(HttpStatusCode.OK, created.Status);
                var mine = JsonSerializer.Deserialize<FormatDescriptor>(created.Body, Json)!;
                Assert.Equal((FormatScope.Tenant, "My date", "2026/06/23"), (mine.Scope, mine.Name, mine.Format(new DateTimeOffset(2026, 6, 23, 0, 0, 0, TimeSpan.Zero))));
                var tenantOnly = await ListAsync(hostClient, hostCookies, "?scope=Tenant");
                Assert.Contains(tenantOnly, format => format.Id == mine.Id && format.Name == "My date");
                var dates = await ListAsync(hostClient, hostCookies, "?kind=Date");
                Assert.Contains(dates, format => format.Scope == FormatScope.System);
                Assert.Contains(dates, format => format.Scope == FormatScope.Tenant && format.Name == "My date");
                Assert.All(dates, format => Assert.Equal(FormatKind.Date, format.Kind));

                // Service_Get_ResolvesSystemPreset, and a tenant definition by id
                var preset = presets.First(format => format.Name == "ISO 8601 date");
                var one = await GateDrive.GetAsync(hostClient, hostCookies, Host, $"/api/Formats/{preset.Id}", output, "a preset by id");
                Assert.Equal(preset, JsonSerializer.Deserialize<FormatDescriptor>(one.Body, Json));
                Assert.Equal(HttpStatusCode.OK, (await GateDrive.GetAsync(hostClient, hostCookies, Host, $"/api/Formats/{mine.Id}", output, "mine by id")).Status);
                Assert.Equal(HttpStatusCode.NotFound, (await GateDrive.GetAsync(hostClient, hostCookies, Host, $"/api/Formats/{Guid.NewGuid()}", output, "unknown id")).Status);

                // An update through the same call, with the id
                var updated = await SaveAsync(app, hostClient, hostCookies, hostId, new FormatDefinitionRequest { Id = mine.Id, Name = "My date", Kind = FormatKind.Date, Pattern = "dd/MM/yyyy", Culture = "en-GB", TimeZone = "Europe/London" });
                Assert.Equal(HttpStatusCode.OK, updated.Status);
                Assert.Equal(("dd/MM/yyyy", "en-GB", "Europe/London"), (JsonSerializer.Deserialize<FormatDescriptor>(updated.Body, Json)!.Pattern, JsonSerializer.Deserialize<FormatDescriptor>(updated.Body, Json)!.Culture, JsonSerializer.Deserialize<FormatDescriptor>(updated.Body, Json)!.TimeZone));

                // Save_DuplicateName_Throws (case-insensitively, D49) / Save_SystemPresetId_Refused / Save_BlankPatternForNumber_FailsValidation / Create_UnknownCulture_Throws
                Assert.Equal(HttpStatusCode.BadRequest, (await SaveAsync(app, hostClient, hostCookies, hostId, new FormatDefinitionRequest { Name = "my DATE", Kind = FormatKind.Number, Pattern = "N0", Culture = "en-US" })).Status);
                Assert.Equal(HttpStatusCode.BadRequest, (await SaveAsync(app, hostClient, hostCookies, hostId, new FormatDefinitionRequest { Id = preset.Id, Name = "Hack", Kind = FormatKind.Date, Pattern = "yyyy-MM-dd", Culture = "" })).Status);
                Assert.Equal(HttpStatusCode.BadRequest, (await SaveAsync(app, hostClient, hostCookies, hostId, new FormatDefinitionRequest { Name = "NoPattern", Kind = FormatKind.Number, Pattern = "", Culture = "en-US" })).Status);
                Assert.Equal(HttpStatusCode.BadRequest, (await SaveAsync(app, hostClient, hostCookies, hostId, new FormatDefinitionRequest { Name = "Bad culture", Kind = FormatKind.Number, Pattern = "N2", Culture = "zz-ZZ-not-real" })).Status);

                // Delete_SystemPreset_Refused / Delete_TenantFormat_Succeeds_WhenUnreferenced
                Assert.Equal(HttpStatusCode.BadRequest, (await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostId, HttpMethod.Delete, $"/api/Formats/{preset.Id}", null, output, "delete a preset")).Status);
                Assert.Equal(HttpStatusCode.NoContent, (await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostId, HttpMethod.Delete, $"/api/Formats/{mine.Id}", null, output, "delete mine")).Status);
                Assert.Equal(HttpStatusCode.NotFound, (await GateDrive.GetAsync(hostClient, hostCookies, Host, $"/api/Formats/{mine.Id}", output, "mine after delete")).Status);
                Assert.DoesNotContain(await ListAsync(hostClient, hostCookies, "?scope=Tenant"), format => format.Id == mine.Id);

                // The keys: a registered user holds neither; a user granted formats:manage may save (and view - the page's
                // list needs formats:view, granted too).
                using var plainClient = GateDrive.NewClient(app, Host);
                var plainCookies = await GateDrive.LoginAsync(app, plainClient, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, output);
                Assert.Equal(HttpStatusCode.Forbidden, (await GateDrive.GetAsync(plainClient, plainCookies, Host, "/api/Formats", output, "list as a registered user")).Status);
                var plainId = await GateDrive.UserIdAsync(app, Host, GateDrive.PlainUsername);
                Assert.Equal(HttpStatusCode.Forbidden, (await SaveAsync(app, plainClient, plainCookies, plainId, new FormatDefinitionRequest { Name = "Sneaky", Kind = FormatKind.Number, Pattern = "N0", Culture = "" })).Status);

                using var editorClient = GateDrive.NewClient(app, Host);
                var editorCookies = await GateDrive.LoginAsync(app, editorClient, Host, GateDrive.GateUsername, GateDrive.GatePassword, output);
                Assert.Equal(HttpStatusCode.Forbidden, (await GateDrive.GetAsync(editorClient, editorCookies, Host, "/api/Formats", output, "list before the grant")).Status);
                await GrantAsync(app, FormatPermissions.View);
                await GrantAsync(app, FormatPermissions.Manage);
                var granted = await SaveAsync(app, editorClient, editorCookies, editor.UserId, new FormatDefinitionRequest { Name = "Editor's number", Kind = FormatKind.Number, Pattern = "N1", Culture = "de-DE" });
                Assert.Equal(HttpStatusCode.OK, granted.Status);
                Assert.Contains(await ListAsync(editorClient, editorCookies, "?scope=Tenant"), format => format.Name == "Editor's number");
            }
            finally
            {
                await app.DisposeAsync();
            }
        }

        private async Task<List<FormatDescriptor>> ListAsync(HttpClient client, System.Net.CookieContainer cookies, string query)
        {
            var response = await GateDrive.GetAsync(client, cookies, Host, "/api/Formats" + query, output, "list" + query);
            Assert.Equal(HttpStatusCode.OK, response.Status);
            return JsonSerializer.Deserialize<List<FormatDescriptor>>(response.Body, Json)!;
        }

        private Task<(HttpStatusCode Status, string Body)> SaveAsync(WebApplication app, HttpClient client, System.Net.CookieContainer cookies, int actorUserId, FormatDefinitionRequest request) =>
            GateDrive.SendJsonAsync(app, client, cookies, Host, actorUserId, HttpMethod.Post, "/api/Formats", request, output, "save " + request.Name);

        private static Task<bool> GrantAsync(WebApplication app, string key)
        {
            var (resource, operation) = (FormatPermissions.Resource, key.Substring(key.IndexOf(':') + 1));
            return GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
            {
                var permissions = sp.GetRequiredService<IPermissionRepository>();
                var submitted = (await permissions.GetPermissionsAsync(alias.SiteId, resource, operation)).Where(row => row.EntityId == PermissionKeys.KeyEntityId).ToList();
                submitted.Add(new Permission(alias.SiteId, resource, PermissionKeys.KeyEntityId, operation, GateDrive.GateRole, null, true));
                await permissions.UpdatePermissionsAsync(alias.SiteId, resource, PermissionKeys.KeyEntityId, submitted);
                return true;
            });
        }
    }
}
