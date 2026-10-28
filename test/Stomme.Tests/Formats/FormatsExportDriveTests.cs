#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.DataExport.Persistence;
using Stomme.Export;
using Stomme.Formats;
using Stomme.Infrastructure.Security;
using Stomme.Models;
using Stomme.Modules.DataExport.Domain;
using Stomme.Modules.DataExport.Execution;
using Stomme.Modules.Formats.Domain;
using Stomme.Repository;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Formats
{
    /// <summary>
    /// BL-94 increment 8 (D110) - one export whose columns name formats, driven end to end on an installed host on SQLite,
    /// PostgreSQL and RavenDB: a preset by id and two site definitions by name (saved through the Formats API), rendered
    /// by the real queue into a CSV the requester downloads. The date column reads as the preset renders it, the amount as
    /// the site's currency definition renders it, a formatted negative number is quoted (a formatter's output is a string,
    /// D108 refutation 11), and an unformatted negative number is not (a genuine scalar is exempt).
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class FormatsExportDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Api = "/api/DataExport";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public static TheoryData<string> Stores() => new() { SqliteGateStore.StoreName, PostgresGateStore.StoreName, RavenGateStore.StoreName };

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task An_export_renders_its_columns_through_the_sites_formats(string storeName)
        {
            var iso = SystemFormats.All.First(preset => preset.Name == "ISO 8601 date");
            var source = new FormattedSource(iso.Id.ToString());
            using var scratch = await GateInstallTemplates.CloneAsync(LiftedRequiresGateTests.ResolveStore(storeName), Host, output,
                colocateModuleAggregates: storeName == PostgresGateStore.StoreName,
                extraSettings: new JsonObject { ["DataExport"] = new JsonObject { ["Enabled"] = true } });
            var app = await GateDrive.InstallAsync(scratch, output, builder => builder.Services.AddSingleton<IExportSource>(source));
            try
            {
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;
                var exporters = await GateDrive.EnsureRoleAsync(app, Host, "Exporters");
                var requester = await GateDrive.AddUserAsync(app, Host, "fmt-requester", "Requ3ster!Pass$2026", "fmt-requester@gate.test", "Exporters");
                await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    var permissions = sp.GetRequiredService<IPermissionRepository>();
                    foreach (var operation in new[] { "create", "view", "rows" })
                    {
                        await permissions.AddPermissionAsync(new Permission
                        {
                            SiteId = siteId, EntityName = ExportPermissions.Resource, EntityId = PermissionRowAccessGuard.KeyEntityId,
                            PermissionName = operation, RoleId = exporters.RoleId, IsAuthorized = true,
                        });
                    }

                    return true;
                });

                // The site's two definitions, saved through the Formats API by the Host.
                using var hostClient = GateDrive.NewClient(app, Host);
                var hostCookies = await GateDrive.LoginAsync(app, hostClient, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                foreach (var request in new[]
                {
                    new FormatDefinitionRequest { Name = "Euro amount", Kind = FormatKind.Currency, Pattern = "C2", Culture = "de-DE" },
                    new FormatDefinitionRequest { Name = "Plain number", Kind = FormatKind.Number, Pattern = "N0", Culture = "en-US" },
                })
                {
                    var saved = await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostId, HttpMethod.Post, "/api/Formats", request, output, "save " + request.Name);
                    Assert.Equal(HttpStatusCode.OK, saved.Status);
                }

                // The export, requested by a holder of export:create and the source's own permission.
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, "fmt-requester", "Requ3ster!Pass$2026", output);
                var requested = await GateDrive.SendJsonAsync(app, client, cookies, Host, requester.UserId, HttpMethod.Post, Api, new { SourceKey = FormattedSource.KeyValue, Format = "Csv" }, output, "request the export");
                Assert.True(requested.Status == HttpStatusCode.OK, $"{(int)requested.Status} {requested.Body}");
                var id = JsonSerializer.Deserialize<Guid>(requested.Body, Json);

                // Rendered by the real queue (this host is fully started, so the live job may take it first - the outcome is what counts).
                output.WriteLine($"[{storeName}][job] " + await GateDrive.InTenantAsync(app, Host, (sp, _) => new ExportQueueJob(sp.GetRequiredService<IServiceScopeFactory>()).ExecuteJobAsync(sp)));
                var status = "";
                for (var attempt = 0; attempt < 240 && status is not ("Ready" or "Failed"); attempt++)
                {
                    await Task.Delay(500);
                    status = await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                        (await sp.GetRequiredService<IExportJobRepository>().ListBySiteAsync(siteId)).Single(job => job.Id == id).Status.ToString());
                }

                Assert.Equal("Ready", status);

                var uri = new Uri(new Uri("http://" + Host), $"{Api}/{id}/download");
                var download = new HttpRequestMessage(HttpMethod.Get, uri);
                download.Headers.TryAddWithoutValidation("Cookie", cookies.GetCookieHeader(uri));
                var response = await client.SendAsync(download);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var csv = await response.Content.ReadAsStringAsync();
                output.WriteLine($"[{storeName}][csv] " + csv.Replace("\r\n", "\\r\\n"));

                var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                Assert.Equal("When,Amount,Neg,Raw", lines[0]);
                Assert.Equal("2026-06-23,\"1.234,50 €\",'-5,-5", lines[1]);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>One row: a date under the ISO preset (by id), an amount under "Euro amount" and a negative under "Plain number" (by name), and the same negative unformatted.</summary>
        private sealed class FormattedSource(string isoPresetId) : IExportSource
        {
            public const string KeyValue = "formats.rows";

            public string Key => KeyValue;

            public string RequiredPermission => "export:rows";

            public TableCardinality TableCardinality => TableCardinality.Single;

            public string? Validate(ExportSourceContext context) => null;

            public Task<ITabularDocument> OpenAsync(ExportSourceContext context, CancellationToken ct = default) =>
                Task.FromResult<ITabularDocument>(new Document(isoPresetId));

            private sealed class Document(string isoPresetId) : ITabularDocument
            {
                public async IAsyncEnumerable<ITabularTable> ReadTablesAsync([EnumeratorCancellation] CancellationToken ct = default)
                {
                    yield return new Table(isoPresetId);
                    await Task.CompletedTask;
                }

                public ValueTask DisposeAsync() => ValueTask.CompletedTask;
            }

            private sealed class Table(string isoPresetId) : ITabularTable
            {
                public string Name => "Rows";

                public IReadOnlyList<ExportColumn> Columns { get; } = new[]
                {
                    new ExportColumn("When", ExportValueKind.DateTimeUtc, isoPresetId),
                    new ExportColumn("Amount", ExportValueKind.Decimal, "Euro amount"),
                    new ExportColumn("Neg", ExportValueKind.Decimal, "Plain number"),
                    new ExportColumn("Raw", ExportValueKind.Decimal),
                };

                public async IAsyncEnumerable<object?[]> ReadRowsAsync([EnumeratorCancellation] CancellationToken ct = default)
                {
                    yield return new object?[] { new DateTime(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc), 1234.5m, -5m, -5m };
                    await Task.CompletedTask;
                }
            }
        }
    }
}
