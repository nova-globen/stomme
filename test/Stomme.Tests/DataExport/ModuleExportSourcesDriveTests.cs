#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Dashboards;
using Stomme.DataExport.Persistence;
using Stomme.Datasets;
using Stomme.Export;
using Stomme.Infrastructure.Security;
using Stomme.Models;
using Stomme.Modules.Dashboards.Infrastructure;
using Stomme.Modules.DataExport.Domain;
using Stomme.Modules.DataExport.Execution;
using Stomme.Modules.Datasets.Infrastructure;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.DataExport
{
    /// <summary>
    /// The three module export sources DRIVEN through the real export engine on installed hosts on
    /// SQLite and PostgreSQL: a dataset registered and a board built over HTTP, exports requested over <c>api/DataExport</c>,
    /// rendered by the queue job, downloaded and read. A projected dataset CSV, a Restricted dataset exported schema-only
    /// (the address never in the artifact), a grid widget as CSV, a whole board as XLSX with one sheet per grid widget and
    /// CSV refused for it; arguments the source does not know refused before a job exists; a requester holding the export
    /// key but not the data's refused as if the source did not exist.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class ModuleExportSourcesDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Api = "/api/DataExport";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public static TheoryData<string> Stores() => new() { SqliteGateStore.StoreName, PostgresGateStore.StoreName };

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task The_dataset_and_dashboard_sources_render_through_the_export_queue(string storeName)
        {
            var settings = new JsonObject { ["DataExport"] = new JsonObject { ["Enabled"] = true } };
            using var scratch = await GateInstallTemplates.CloneAsync(LiftedRequiresGateTests.ResolveStore(storeName), Host, output,
                colocateModuleAggregates: storeName == PostgresGateStore.StoreName, extraSettings: settings);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                var alias = await GateDrive.AliasAsync(app, Host);
                var siteId = alias.SiteId;

                // The export keys are declared with no role (fail closed): the Administrators receive export:create here, and a
                // second role holds ONLY export:create - the data keys it lacks are what the engine refuses on.
                var admins = await GateDrive.EnsureRoleAsync(app, Host, RoleNames.Admin);
                var exportersOnly = await GateDrive.EnsureRoleAsync(app, Host, "ExportersOnly");
                await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    var permissions = sp.GetRequiredService<IPermissionRepository>();
                    foreach (var roleId in new[] { admins.RoleId, exportersOnly.RoleId })
                    {
                        await permissions.AddPermissionAsync(new Permission
                        {
                            SiteId = siteId, EntityName = ExportPermissions.Resource, EntityId = PermissionRowAccessGuard.KeyEntityId,
                            PermissionName = "create", RoleId = roleId, IsAuthorized = true,
                        });
                    }

                    return true;
                });
                var narrow = await GateDrive.AddUserAsync(app, Host, "exp-narrow", "Narr0w!Export$2026", "exp-narrow@gate.test", "ExportersOnly");

                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);

                // ---- the data: an open dataset, one the classifier makes Restricted, and a board over both --------------------
                var sales = await Post<DatasetDescriptor>(app, client, cookies, hostId, "/api/Datasets", new RegisterDatasetRequest
                {
                    Name = "Sales", OriginKind = OriginKind.PipelineOutput, OriginReference = "pipeline-sales",
                    SampleJson = "[ { \"sku\": \"A1\", \"amount\": \"10\" }, { \"sku\": \"B2\", \"amount\": \"20\" } ]", Classification = DataClassification.Internal,
                }, "register Sales");
                var customers = await Post<DatasetDescriptor>(app, client, cookies, hostId, "/api/Datasets", new RegisterDatasetRequest
                {
                    Name = "Customers", OriginKind = OriginKind.Upload, OriginReference = "file-1",
                    SampleJson = "[ { \"name\": \"Ada\", \"contact\": \"ada@example.com\" } ]", Classification = DataClassification.Public,
                }, "register Customers");
                Assert.Equal(DataClassification.Restricted, customers.Classification);

                var board = await Post<DashboardView>(app, client, cookies, hostId, "/api/Dashboards", new CreateDashboardRequest { Name = "Operations" }, "create the board");
                board = await Post<DashboardView>(app, client, cookies, hostId, $"/api/Dashboards/{board.Id}/widgets",
                    new AddWidgetRequest { Title = "Sales grid", Kind = WidgetKind.Grid, DatasetId = sales.Id, Columns = new() { "sku", "amount" }, Column = 0, Row = 0, ColumnSpan = 6, RowSpan = 4 }, "sales grid");
                board = await Post<DashboardView>(app, client, cookies, hostId, $"/api/Dashboards/{board.Id}/widgets",
                    new AddWidgetRequest { Title = "Count", Kind = WidgetKind.Kpi, DatasetId = sales.Id, Column = 6, Row = 0, ColumnSpan = 3, RowSpan = 4 }, "kpi");
                board = await Post<DashboardView>(app, client, cookies, hostId, $"/api/Dashboards/{board.Id}/widgets",
                    new AddWidgetRequest { Title = "Contacts", Kind = WidgetKind.Grid, DatasetId = customers.Id, Columns = new() { "contact" }, Column = 0, Row = 4, ColumnSpan = 6, RowSpan = 4 }, "contacts grid");
                var salesGrid = board.Widgets.Single(widget => widget.Title == "Sales grid");

                // ---- the sources are listed for the host ---------------------------------------------------------------------
                var listed = await GateDrive.GetAsync(client, cookies, Host, $"{Api}/sources", output, "sources");
                Assert.Equal(HttpStatusCode.OK, listed.Status);
                foreach (var key in new[] { DatasetRowsExportSource.SourceKey, DashboardGridExportSource.SourceKey, DashboardReportExportSource.SourceKey })
                {
                    Assert.Contains(key, listed.Body);
                }

                // ---- requests: what is refused before a job exists, and what is accepted -----------------------------------------
                var csvReport = await Send(app, client, cookies, hostId, new { SourceKey = DashboardReportExportSource.SourceKey, Format = "Csv", ArgumentsJson = $$"""{"dashboardId":"{{board.Id}}"}""" }, "report as CSV");
                Assert.Equal(HttpStatusCode.BadRequest, csvReport.Status);
                Assert.Contains("several tables", csvReport.Body, StringComparison.OrdinalIgnoreCase);
                var smuggled = await Send(app, client, cookies, hostId, new { SourceKey = DatasetRowsExportSource.SourceKey, Format = "Csv", ArgumentsJson = $$"""{"datasetId":"{{sales.Id}}","siteId":2}""" }, "an argument the source does not know");
                Assert.Equal(HttpStatusCode.BadRequest, smuggled.Status);
                Assert.Contains("'siteId' is not an argument of this source", smuggled.Body, StringComparison.Ordinal);

                var projected = await Created(app, client, cookies, hostId, new { SourceKey = DatasetRowsExportSource.SourceKey, Format = "Csv", ArgumentsJson = $$"""{"datasetId":"{{sales.Id}}","columns":["amount","sku"]}""" }, "sales rows, projected");
                var masked = await Created(app, client, cookies, hostId, new { SourceKey = DatasetRowsExportSource.SourceKey, Format = "Csv", ArgumentsJson = $$"""{"datasetId":"{{customers.Id}}"}""" }, "restricted rows");
                var grid = await Created(app, client, cookies, hostId, new { SourceKey = DashboardGridExportSource.SourceKey, Format = "Csv", ArgumentsJson = $$"""{"dashboardId":"{{board.Id}}","widgetId":"{{salesGrid.Id}}"}""" }, "the sales grid");
                var report = await Created(app, client, cookies, hostId, new { SourceKey = DashboardReportExportSource.SourceKey, Format = "Xlsx", ArgumentsJson = $$"""{"dashboardId":"{{board.Id}}"}""" }, "the board as xlsx");

                // ---- a requester with the export key but not the data's: the source does not exist for them -------------------
                using (var narrowClient = GateDrive.NewClient(app, Host))
                {
                    var narrowCookies = await GateDrive.LoginAsync(app, narrowClient, Host, "exp-narrow", "Narr0w!Export$2026", output);
                    var refused = await Send(app, narrowClient, narrowCookies, narrow.UserId, new { SourceKey = DatasetRowsExportSource.SourceKey, Format = "Csv", ArgumentsJson = $$"""{"datasetId":"{{sales.Id}}"}""" }, "narrow: dataset rows");
                    Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
                    Assert.Contains("does not exist", refused.Body, StringComparison.OrdinalIgnoreCase);
                    var refusedBoard = await Send(app, narrowClient, narrowCookies, narrow.UserId, new { SourceKey = DashboardReportExportSource.SourceKey, Format = "Xlsx", ArgumentsJson = $$"""{"dashboardId":"{{board.Id}}"}""" }, "narrow: the board");
                    Assert.Equal(HttpStatusCode.BadRequest, refusedBoard.Status);
                }

                // ---- render: the manual pass and the live per-minute job may each claim some; the outcome is what counts ------
                var log = await GateDrive.InTenantAsync(app, Host, (sp, _) => new ExportQueueJob(sp.GetRequiredService<IServiceScopeFactory>()).ExecuteJobAsync(sp));
                output.WriteLine($"[{storeName}][job] {log}");
                var outstanding = 4;
                for (var attempt = 0; attempt < 240 && outstanding > 0; attempt++)
                {
                    outstanding = await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                        (await sp.GetRequiredService<IExportJobRepository>().ListBySiteAsync(siteId)).Count(job => job.Status is ExportJobStatus.Queued or ExportJobStatus.Rendering));
                    if (outstanding > 0)
                    {
                        await Task.Delay(500);
                    }
                }

                Assert.Equal(0, outstanding);
                var failures = await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                    (await sp.GetRequiredService<IExportJobRepository>().ListBySiteAsync(siteId)).Where(job => job.Status != ExportJobStatus.Ready).Select(job => $"{job.SourceKey}: {job.Status} {job.FailureCode}").ToList());
                Assert.True(failures.Count == 0, string.Join(" | ", failures));

                // ---- the artifacts -------------------------------------------------------------------------------------------
                var projectedText = Encoding.UTF8.GetString(await Download(client, cookies, projected, "download projected"));
                output.WriteLine($"[{storeName}][projected] {projectedText.Replace("\r\n", " | ")}");
                Assert.StartsWith("amount,sku\r\n", projectedText.TrimStart('﻿'));
                Assert.Contains("10,A1", projectedText);
                Assert.Contains("20,B2", projectedText);

                var maskedText = Encoding.UTF8.GetString(await Download(client, cookies, masked, "download restricted"));
                output.WriteLine($"[{storeName}][restricted] {maskedText.Replace("\r\n", " | ")}");
                Assert.Contains("contact", maskedText);
                Assert.DoesNotContain("@example.com", maskedText);
                Assert.DoesNotContain("Ada", maskedText);

                var gridText = Encoding.UTF8.GetString(await Download(client, cookies, grid, "download grid"));
                Assert.StartsWith("sku,amount\r\n", gridText.TrimStart('﻿'));
                Assert.Contains("A1,10", gridText);

                var workbook = await Download(client, cookies, report, "download report");
                using var zip = new ZipArchive(new MemoryStream(workbook), ZipArchiveMode.Read);
                var sheets = zip.Entries.Where(entry => entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal)).ToList();
                output.WriteLine($"[{storeName}][report] {string.Join(", ", sheets.Select(sheet => sheet.FullName))}");
                Assert.Equal(2, sheets.Count); // the two GRID widgets; the KPI is not a table
                var allSheets = string.Concat(sheets.Select(sheet => new StreamReader(sheet.Open()).ReadToEnd()));
                var sharedStrings = zip.GetEntry("xl/sharedStrings.xml") is { } strings ? new StreamReader(strings.Open()).ReadToEnd() : string.Empty;
                Assert.Contains("A1", allSheets + sharedStrings);
                Assert.DoesNotContain("@example.com", allSheets + sharedStrings);
                var workbookXml = new StreamReader(zip.GetEntry("xl/workbook.xml")!.Open()).ReadToEnd();
                Assert.Contains("Sales grid", workbookXml);
                Assert.Contains("Contacts", workbookXml);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private async Task<T> Post<T>(WebApplication app, HttpClient client, CookieContainer cookies, int userId, string path, object body, string label)
        {
            var response = await GateDrive.SendJsonAsync(app, client, cookies, Host, userId, HttpMethod.Post, path, body, output, label);
            Assert.True(response.Status == HttpStatusCode.OK, $"{label}: {(int)response.Status} {response.Body}");
            return JsonSerializer.Deserialize<T>(response.Body, Json)!;
        }

        private Task<(HttpStatusCode Status, string Body)> Send(WebApplication app, HttpClient client, CookieContainer cookies, int userId, object body, string label) =>
            GateDrive.SendJsonAsync(app, client, cookies, Host, userId, HttpMethod.Post, Api, body, output, label);

        private async Task<Guid> Created(WebApplication app, HttpClient client, CookieContainer cookies, int userId, object body, string label)
        {
            var response = await Send(app, client, cookies, userId, body, label);
            Assert.True(response.Status == HttpStatusCode.OK, $"{label}: {(int)response.Status} {response.Body}");
            return JsonSerializer.Deserialize<Guid>(response.Body);
        }

        private async Task<byte[]> Download(HttpClient client, CookieContainer cookies, Guid id, string label)
        {
            var uri = new Uri(new Uri("http://" + Host), $"{Api}/{id}/download");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var header = cookies.GetCookieHeader(uri);
            if (!string.IsNullOrEmpty(header))
            {
                request.Headers.TryAddWithoutValidation("Cookie", header);
            }

            using var response = await client.SendAsync(request);
            output.WriteLine($"[download] {label}: {(int)response.StatusCode} {response.Content.Headers.ContentType}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadAsByteArrayAsync();
        }
    }
}
