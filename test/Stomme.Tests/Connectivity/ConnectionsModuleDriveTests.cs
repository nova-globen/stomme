#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Models;
using Stomme.Modules.Connectivity.Domain;
using Stomme.Repository;
using Stomme.Security;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectivity
{
    /// <summary>
    /// BL-94 increment 7 (D110) - the Connectivity module on an installed host, over real HTTP, on SQLite, PostgreSQL and
    /// RavenDB, with a fixture connector composed into the host: the admin page exists from the module's own template and
    /// renders for the Host; the catalog lists the fixture with its scheme's credential fields (the predecessor platform's
    /// <c>ConnectorPlugins_AreDiscoveredByThePluginHost_AndRegisteredInTheCatalog</c>); a connection is created with a
    /// credential the response never carries; the host is RESTARTED and the secret still unprotects for a test; an edit
    /// rotates one credential field and keeps the other; a delete removes the row and every secret row; the refusals are
    /// the caller's input (the predecessor platform's ConnectivityApiTests by name in the comments); a registered user holds neither key;
    /// a user granted the keys may create. The browser click-through is the owner's (BL-94's DoD).
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class ConnectionsModuleDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public static TheoryData<string> Stores() => new() { SqliteGateStore.StoreName, PostgresGateStore.StoreName, RavenGateStore.StoreName };

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task The_page_renders_and_the_api_creates_tests_across_a_restart_edits_and_deletes_under_its_keys(string storeName)
        {
            var fixture = new ProbeConnector("fixture") { DisplayName = "Fixture API", AuthType = AuthenticationType.Basic, EndpointKind = ConnectorEndpointKind.Url };
            Action<WebApplicationBuilder> configure = builder => builder.Services.AddSingleton<IConnector>(fixture);
            using var scratch = await GateInstallTemplates.CloneAsync(LiftedRequiresGateTests.ResolveStore(storeName), Host, output, colocateModuleAggregates: storeName == PostgresGateStore.StoreName, extraSettings: null);

            Guid id;
            var first = await GateDrive.InstallAsync(scratch, output, configure);
            try
            {
                await GateDrive.AddUserAsync(first, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, GateDrive.PlainEmail);
                await GateDrive.EnsureRoleAsync(first, Host, GateDrive.GateRole);
                await GateDrive.AddUserAsync(first, Host, GateDrive.GateUsername, GateDrive.GatePassword, GateDrive.GateEmail, GateDrive.GateRole);

                // The admin page, from the module's own template (ConnectionsPage_IsServed_AndGatesAnonymousAccess: rendered, gated).
                Assert.NotNull(await GateDrive.InTenantAsync(first, Host, async (sp, alias) => await sp.GetRequiredService<IPageRepository>().GetPageAsync("admin/connections", alias.SiteId)));
                using var hostClient = GateDrive.NewClient(first, Host);
                var hostCookies = await GateDrive.LoginAsync(first, hostClient, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(first, Host, GateDrive.HostUsername);
                var page = await GateDrive.GetAsync(hostClient, hostCookies, Host, "/admin/connections", output, "the connections page");
                Assert.Equal(HttpStatusCode.OK, page.Status);
                Assert.Contains("Connections", page.Body);

                // ListConnectors_WithThePermission_ReturnsTheEnrichedCatalogue / _ProjectsTheDeclaredAuthSchemeCredentialFields
                var catalog = await GateDrive.GetAsync(hostClient, hostCookies, Host, "/api/Connections/connectors", output, "the catalog");
                Assert.Equal(HttpStatusCode.OK, catalog.Status);
                var connectors = JsonSerializer.Deserialize<List<ConnectorView>>(catalog.Body, Json)!;
                var listed = Assert.Single(connectors, connector => connector.ConnectorType == "fixture");
                Assert.Equal(("Fixture API", "Basic", "Url"), (listed.DisplayName, listed.AuthType, listed.EndpointKind));
                Assert.Contains(listed.AuthFields, field => field.Name == "username" && !field.Secret);
                Assert.Contains(listed.AuthFields, field => field.Name == "password" && field.Secret);
                Assert.Single(listed.Categories, category => category.Id == "uncategorized");
                Assert.StartsWith("<svg ", listed.Icon);

                // CreateConnection_WithCredentials_CreatesTheConnection - and the response carries the field NAMES, never the value.
                var created = await SendAsync(first, hostClient, hostCookies, hostId, HttpMethod.Post, "/api/Connections",
                    new ConnectionRequest { Name = "Prod API", ConnectorType = "fixture", Endpoint = "https://api.example.com/v1", Credentials = new() { ["username"] = "svc", ["password"] = "s3cret" }, TimeoutSeconds = 20 });
                Assert.Equal(HttpStatusCode.OK, created.Status);
                var view = JsonSerializer.Deserialize<ConnectionView>(created.Body, Json)!;
                id = view.Id;
                Assert.Equal(("Prod API", "Fixture API", "Untested", 20), (view.Name, view.ConnectorDisplayName, view.Status, view.TimeoutSeconds));
                Assert.Equal(new[] { "username", "password" }, view.CredentialFields);
                Assert.DoesNotContain("s3cret", created.Body);

                // CreateConnection_UnknownConnectorType_IsBadRequest / _MissingEndpoint_IsBadRequest / a credential in the URL / a duplicate name
                Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(first, hostClient, hostCookies, hostId, HttpMethod.Post, "/api/Connections", new ConnectionRequest { Name = "Nope", ConnectorType = "not-a-real-connector", Endpoint = "https://example.com" })).Status);
                Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(first, hostClient, hostCookies, hostId, HttpMethod.Post, "/api/Connections", new ConnectionRequest { Name = "No endpoint", ConnectorType = "fixture", Endpoint = "" })).Status);
                Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(first, hostClient, hostCookies, hostId, HttpMethod.Post, "/api/Connections", new ConnectionRequest { Name = "Leaky", ConnectorType = "fixture", Endpoint = "https://svc:hunter2@api.example.com" })).Status);
                Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(first, hostClient, hostCookies, hostId, HttpMethod.Post, "/api/Connections", new ConnectionRequest { Name = "prod api", ConnectorType = "fixture", Endpoint = "https://api.example.com" })).Status);

                // A test: the fixture receives the credential resolved from the site's secret store; the verdict is a category.
                var tested = await SendAsync(first, hostClient, hostCookies, hostId, HttpMethod.Post, $"/api/Connections/{id}/test", null);
                Assert.Equal(HttpStatusCode.OK, tested.Status);
                var verdict = JsonSerializer.Deserialize<ConnectionTestView>(tested.Body, Json)!;
                Assert.Equal((true, ConnectorOutcome.Succeeded, "Healthy"), (verdict.Success, verdict.Outcome, verdict.Status));
                Assert.Equal(("svc", "s3cret", "https://api.example.com/v1"), (fixture.LastContext?.Credentials["username"], fixture.LastContext?.Credentials["password"], fixture.LastContext?.Endpoint));
                Assert.Contains(await ListAsync(hostClient, hostCookies), connection => connection.Id == id && connection.Status == "Healthy");
            }
            finally
            {
                await GateDrive.ShutdownAsync(first);
            }

            // ---- the host restarts over the same install: the secret still unprotects, the connection is still there ----
            var second = await GateDrive.InstallAsync(scratch, output, configure);
            try
            {
                using var hostClient = GateDrive.NewClient(second, Host);
                var hostCookies = await GateDrive.LoginAsync(second, hostClient, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(second, Host, GateDrive.HostUsername);

                var again = await SendAsync(second, hostClient, hostCookies, hostId, HttpMethod.Post, $"/api/Connections/{id}/test", null);
                Assert.Equal(HttpStatusCode.OK, again.Status);
                Assert.Equal(ConnectorOutcome.Succeeded, JsonSerializer.Deserialize<ConnectionTestView>(again.Body, Json)!.Outcome);
                Assert.Equal("s3cret", fixture.LastContext?.Credentials["password"]); // read back through the restarted host's key ring

                // An edit rotating one credential field keeps the other; the endpoint and the timeout change; the verdict resets.
                var updated = await SendAsync(second, hostClient, hostCookies, hostId, HttpMethod.Put, $"/api/Connections/{id}",
                    new ConnectionRequest { Name = "Prod API", ConnectorType = "fixture", Endpoint = "https://api.example.com/v2", Credentials = new() { ["password"] = "rotated" }, TimeoutSeconds = 45 });
                Assert.Equal(HttpStatusCode.OK, updated.Status);
                var edited = JsonSerializer.Deserialize<ConnectionView>(updated.Body, Json)!;
                Assert.Equal(("https://api.example.com/v2", 45, "Untested"), (edited.Endpoint, edited.TimeoutSeconds, edited.Status));
                Assert.DoesNotContain("rotated", updated.Body);
                await SendAsync(second, hostClient, hostCookies, hostId, HttpMethod.Post, $"/api/Connections/{id}/test", null);
                Assert.Equal(("svc", "rotated"), (fixture.LastContext?.Credentials["username"], fixture.LastContext?.Credentials["password"]));

                // A delete removes the row and every secret row.
                Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(second, hostClient, hostCookies, hostId, HttpMethod.Delete, $"/api/Connections/{id}", null)).Status);
                Assert.Equal(HttpStatusCode.NotFound, (await GateDrive.GetAsync(hostClient, hostCookies, Host, $"/api/Connections/{id}", output, "after delete")).Status);
                Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(second, hostClient, hostCookies, hostId, HttpMethod.Post, $"/api/Connections/{id}/test", null)).Status);
                var leftover = await GateDrive.InTenantAsync(second, Host, async (sp, alias) =>
                {
                    var settings = sp.GetRequiredService<ISettingRepository>();
                    var present = new List<string>();
                    foreach (var field in new[] { "username", "password" })
                    {
                        if (await settings.GetSettingAsync(EntityNames.Site, alias.SiteId, ConnectionCredentialKeys.For(id, field)) is not null)
                        {
                            present.Add(field);
                        }
                    }

                    return present;
                });
                Assert.Empty(leftover);

                // The keys: a registered user holds neither (AnonymousRequest_IsRejected / ListConnectors_WithoutThePermission_IsForbidden /
                // CreateConnection_WithViewOnly_IsForbidden); a user granted both may create (CreateConnection_WithManage_CreatesAndReturnsIdAndAppearsInTheList).
                using var plainClient = GateDrive.NewClient(second, Host);
                var plainCookies = await GateDrive.LoginAsync(second, plainClient, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, output);
                var plainId = await GateDrive.UserIdAsync(second, Host, GateDrive.PlainUsername);
                Assert.Equal(HttpStatusCode.Forbidden, (await GateDrive.GetAsync(plainClient, plainCookies, Host, "/api/Connections", output, "list as a registered user")).Status);
                Assert.Equal(HttpStatusCode.Forbidden, (await GateDrive.GetAsync(plainClient, plainCookies, Host, "/api/Connections/connectors", output, "catalog as a registered user")).Status);
                Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(second, plainClient, plainCookies, plainId, HttpMethod.Post, "/api/Connections", new ConnectionRequest { Name = "Sneaky", ConnectorType = "fixture", Endpoint = "https://example.com" })).Status);

                using var editorClient = GateDrive.NewClient(second, Host);
                var editorCookies = await GateDrive.LoginAsync(second, editorClient, Host, GateDrive.GateUsername, GateDrive.GatePassword, output);
                var editorId = await GateDrive.UserIdAsync(second, Host, GateDrive.GateUsername);
                await GrantAsync(second, ConnectionPermissions.View);
                Assert.Equal(HttpStatusCode.OK, (await GateDrive.GetAsync(editorClient, editorCookies, Host, "/api/Connections", output, "list with view")).Status);
                Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(second, editorClient, editorCookies, editorId, HttpMethod.Post, "/api/Connections", new ConnectionRequest { Name = "Viewer", ConnectorType = "fixture", Endpoint = "https://example.com" })).Status);
                await GrantAsync(second, ConnectionPermissions.Manage);
                var granted = await SendAsync(second, editorClient, editorCookies, editorId, HttpMethod.Post, "/api/Connections", new ConnectionRequest { Name = "Editor-created connection", ConnectorType = "fixture", Endpoint = "https://api.example.com" });
                Assert.Equal(HttpStatusCode.OK, granted.Status);
                Assert.Contains(await ListAsync(editorClient, editorCookies), connection => connection.Name == "Editor-created connection" && !connection.HasCredentials);
            }
            finally
            {
                await GateDrive.ShutdownAsync(second);
            }
        }

        private async Task<List<ConnectionView>> ListAsync(HttpClient client, CookieContainer cookies)
        {
            var response = await GateDrive.GetAsync(client, cookies, Host, "/api/Connections", output, "list");
            Assert.Equal(HttpStatusCode.OK, response.Status);
            return JsonSerializer.Deserialize<List<ConnectionView>>(response.Body, Json)!;
        }

        private Task<(HttpStatusCode Status, string Body)> SendAsync(WebApplication app, HttpClient client, CookieContainer cookies, int actorUserId, HttpMethod method, string path, object? body) =>
            GateDrive.SendJsonAsync(app, client, cookies, Host, actorUserId, method, path, body, output, $"{method} {path}");

        private static Task<bool> GrantAsync(WebApplication app, string key)
        {
            var (resource, operation) = (ConnectionPermissions.Resource, key.Substring(key.IndexOf(':') + 1));
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
