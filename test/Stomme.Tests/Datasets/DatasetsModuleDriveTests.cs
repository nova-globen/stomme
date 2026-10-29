#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Datasets;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Security;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Datasets
{
    /// <summary>
    /// The Datasets module on an installed host, over real HTTP, on SQLite, PostgreSQL and
    /// RavenDB: the admin page exists from the module's own template and renders for the Host; a PII sample registered
    /// as Public comes back Restricted with a masked summary and a refused preview; an open dataset previews its rows; a
    /// same-name different-origin registration is 400; a refresh and a delete land; a registered user is refused; a user
    /// granted view and manage but not preview may list and register and is refused the preview until it is granted
    /// The browser click-through is not covered by this suite.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class DatasetsModuleDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Api = "/api/Datasets";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public static TheoryData<string> Stores() => new() { SqliteGateStore.StoreName, PostgresGateStore.StoreName, RavenGateStore.StoreName };

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task The_page_renders_and_the_api_registers_classifies_previews_refreshes_and_deletes_under_its_keys(string storeName)
        {
            using var scratch = await GateInstallTemplates.CloneAsync(LiftedRequiresGateTests.ResolveStore(storeName), Host, output, colocateModuleAggregates: storeName == PostgresGateStore.StoreName, extraSettings: null);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                await GateDrive.AddUserAsync(app, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, GateDrive.PlainEmail);
                await GateDrive.EnsureRoleAsync(app, Host, GateDrive.GateRole);
                var editor = await GateDrive.AddUserAsync(app, Host, GateDrive.GateUsername, GateDrive.GatePassword, GateDrive.GateEmail, GateDrive.GateRole);

                // DatasetsPage_IsServed_AndGatesAnonymousAccess: the admin page, from the module's own template, rendered for the Host.
                Assert.NotNull(await GateDrive.InTenantAsync(app, Host, async (sp, alias) => await sp.GetRequiredService<IPageRepository>().GetPageAsync("admin/datasets", alias.SiteId)));
                using var hostClient = GateDrive.NewClient(app, Host);
                var hostCookies = await GateDrive.LoginAsync(app, hostClient, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                var page = await GateDrive.GetAsync(hostClient, hostCookies, Host, "/admin/datasets", output, "the datasets page");
                Assert.Equal(HttpStatusCode.OK, page.Status);
                Assert.Contains("Datasets", page.Body);

                // Host_RegistersTheDatasetRegistrar: the write port is composed.
                Assert.NotNull(await GateDrive.InTenantAsync(app, Host, (sp, _) => Task.FromResult(sp.GetRequiredService<IDatasetRegistrar>())));

                // The command classifies: a PII sample registered as Public comes back Restricted, masked, its preview refused.
                var customers = await SendAsync(app, hostClient, hostCookies, hostId, HttpMethod.Post, Api,
                    new RegisterDatasetRequest { Name = "Customers", OriginKind = OriginKind.Upload, OriginReference = "file-1", SampleJson = "[ { \"name\": \"Ada\", \"contact\": \"ada@example.com\" } ]", Classification = DataClassification.Public });
                Assert.Equal(HttpStatusCode.OK, customers.Status);
                var restricted = JsonSerializer.Deserialize<DatasetDescriptor>(customers.Body, Json)!;
                Assert.Equal((DataClassification.Restricted, true, 1), (restricted.Classification, restricted.IsSensitive, restricted.Summary.RowCountEstimate));
                Assert.All(restricted.Summary.Columns, column => Assert.Empty(column.Samples));
                Assert.DoesNotContain("ada@example.com", customers.Body);
                var refusedPreview = await GateDrive.GetAsync(hostClient, hostCookies, Host, $"{Api}/{restricted.Id}/preview", output, "preview a Restricted dataset");
                Assert.Equal(HttpStatusCode.OK, refusedPreview.Status);
                var refused = JsonSerializer.Deserialize<DatasetPreviewRows>(refusedPreview.Body, Json)!;
                Assert.Equal((false, "[]"), (refused.Allowed, refused.RowsJson));
                Assert.DoesNotContain("ada@example.com", refusedPreview.Body);

                // An open dataset previews its bounded rows.
                var sales = await SendAsync(app, hostClient, hostCookies, hostId, HttpMethod.Post, Api,
                    new RegisterDatasetRequest { Name = "Sales", OriginKind = OriginKind.PipelineOutput, OriginReference = "pipeline-1", SampleJson = "[ { \"sku\": \"A1\", \"amount\": \"10\" }, { \"sku\": \"B2\", \"amount\": \"20\" } ]" });
                Assert.Equal(HttpStatusCode.OK, sales.Status);
                var open = JsonSerializer.Deserialize<DatasetDescriptor>(sales.Body, Json)!;
                Assert.Equal((DataClassification.Internal, 2, "currency"), (open.Classification, open.Summary.RowCountEstimate, open.Schema.Columns.Single(column => column.Name == "amount").SemanticType));
                var preview = JsonSerializer.Deserialize<DatasetPreviewRows>((await GateDrive.GetAsync(hostClient, hostCookies, Host, $"{Api}/{open.Id}/preview", output, "preview an open dataset")).Body, Json)!;
                Assert.True(preview.Allowed);
                Assert.Contains("B2", preview.RowsJson);

                // The list and the get; an unknown id.
                var listed = await ListAsync(hostClient, hostCookies);
                Assert.Equal(new[] { "Customers", "Sales" }, listed.Select(dataset => dataset.Name));
                Assert.Equal(HttpStatusCode.OK, (await GateDrive.GetAsync(hostClient, hostCookies, Host, $"{Api}/{open.Id}", output, "get")).Status);
                Assert.Equal(HttpStatusCode.NotFound, (await GateDrive.GetAsync(hostClient, hostCookies, Host, $"{Api}/{Guid.NewGuid()}", output, "get unknown")).Status);
                Assert.Equal(HttpStatusCode.NotFound, (await GateDrive.GetAsync(hostClient, hostCookies, Host, $"{Api}/{Guid.NewGuid()}/preview", output, "preview unknown")).Status);

                // A same-name different-origin registration is the caller's input; a blank name is a validation problem.
                Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(app, hostClient, hostCookies, hostId, HttpMethod.Post, Api, new RegisterDatasetRequest { Name = "sales", OriginKind = OriginKind.Upload, OriginReference = "file-2", SampleJson = "[ { \"x\": \"1\" } ]" })).Status);
                Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(app, hostClient, hostCookies, hostId, HttpMethod.Post, Api, new RegisterDatasetRequest { Name = "", OriginKind = OriginKind.Upload, SampleJson = "[ { \"x\": \"1\" } ]" })).Status);

                // A refresh with fresh rows; the same origin registered again is the same row.
                var refreshed = await SendAsync(app, hostClient, hostCookies, hostId, HttpMethod.Post, $"{Api}/{open.Id}/refresh", new RefreshDatasetRequest { SampleJson = "[ { \"sku\": \"C3\", \"amount\": \"30\", \"note\": \"new\" } ]" });
                Assert.Equal(HttpStatusCode.OK, refreshed.Status);
                Assert.Equal(new[] { "sku", "amount", "note" }, JsonSerializer.Deserialize<DatasetDescriptor>(refreshed.Body, Json)!.Schema.Columns.Select(column => column.Name));
                var again = await SendAsync(app, hostClient, hostCookies, hostId, HttpMethod.Post, Api, new RegisterDatasetRequest { Name = "Sales", OriginKind = OriginKind.PipelineOutput, OriginReference = "PIPELINE-1", SampleJson = "[ { \"sku\": \"D4\", \"amount\": \"40\" } ]" });
                Assert.Equal(open.Id, JsonSerializer.Deserialize<DatasetDescriptor>(again.Body, Json)!.Id);
                Assert.Equal(2, (await ListAsync(hostClient, hostCookies)).Count);

                // A delete.
                Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(app, hostClient, hostCookies, hostId, HttpMethod.Delete, $"{Api}/{restricted.Id}", null)).Status);
                Assert.Equal(HttpStatusCode.NotFound, (await GateDrive.GetAsync(hostClient, hostCookies, Host, $"{Api}/{restricted.Id}", output, "after delete")).Status);

                // The keys: a registered user holds none (ListDatasets_RequiresAuthentication / RegisterDataset_RequiresAuthentication);
                // a user granted view and manage may list and register and is refused the preview until datasets:preview is granted.
                using var plainClient = GateDrive.NewClient(app, Host);
                var plainCookies = await GateDrive.LoginAsync(app, plainClient, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, output);
                var plainId = await GateDrive.UserIdAsync(app, Host, GateDrive.PlainUsername);
                Assert.Equal(HttpStatusCode.Forbidden, (await GateDrive.GetAsync(plainClient, plainCookies, Host, Api, output, "list as a registered user")).Status);
                Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(app, plainClient, plainCookies, plainId, HttpMethod.Post, Api, new RegisterDatasetRequest { Name = "Sneaky", OriginKind = OriginKind.Upload, SampleJson = "[]" })).Status);

                using var editorClient = GateDrive.NewClient(app, Host);
                var editorCookies = await GateDrive.LoginAsync(app, editorClient, Host, GateDrive.GateUsername, GateDrive.GatePassword, output);
                await GrantAsync(app, DatasetPermissions.View);
                await GrantAsync(app, DatasetPermissions.Manage);
                Assert.Equal(HttpStatusCode.OK, (await GateDrive.GetAsync(editorClient, editorCookies, Host, Api, output, "list with view")).Status);
                var editors = await SendAsync(app, editorClient, editorCookies, editor.UserId, HttpMethod.Post, Api, new RegisterDatasetRequest { Name = "Editor's rows", OriginKind = OriginKind.Upload, OriginReference = "file-e", SampleJson = "[ { \"n\": \"1\" } ]" });
                Assert.Equal(HttpStatusCode.OK, editors.Status);
                var editorsId = JsonSerializer.Deserialize<DatasetDescriptor>(editors.Body, Json)!.Id;
                Assert.Equal(HttpStatusCode.Forbidden, (await GateDrive.GetAsync(editorClient, editorCookies, Host, $"{Api}/{editorsId}/preview", output, "preview without the key")).Status);
                await GrantAsync(app, DatasetPermissions.Preview);
                Assert.Equal(HttpStatusCode.OK, (await GateDrive.GetAsync(editorClient, editorCookies, Host, $"{Api}/{editorsId}/preview", output, "preview with the key")).Status);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private async Task<List<DatasetDescriptor>> ListAsync(HttpClient client, CookieContainer cookies)
        {
            var response = await GateDrive.GetAsync(client, cookies, Host, Api, output, "list");
            Assert.Equal(HttpStatusCode.OK, response.Status);
            return JsonSerializer.Deserialize<List<DatasetDescriptor>>(response.Body, Json)!;
        }

        private Task<(HttpStatusCode Status, string Body)> SendAsync(WebApplication app, HttpClient client, CookieContainer cookies, int actorUserId, HttpMethod method, string path, object? body) =>
            GateDrive.SendJsonAsync(app, client, cookies, Host, actorUserId, method, path, body, output, $"{method} {path}");

        private static Task<bool> GrantAsync(WebApplication app, string key)
        {
            var (resource, operation) = (DatasetPermissions.Resource, key.Substring(key.IndexOf(':') + 1));
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
