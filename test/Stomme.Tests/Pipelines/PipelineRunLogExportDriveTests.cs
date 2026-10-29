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
using Stomme.DataExport.Persistence;
using Stomme.Export;
using Stomme.Infrastructure.Security;
using Stomme.Models;
using Stomme.Models.Pipelines;
using Stomme.Modules.DataExport.Domain;
using Stomme.Modules.DataExport.Execution;
using Stomme.Modules.Pipelines.Application;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// A pipeline run's execution log exported through the REAL export
    /// queue on SQLite, PostgreSQL and RavenDB - a pipeline created, published and run over HTTP, its log requested as CSV,
    /// rendered by the queue job (which has no request, so the source enters the site's engine tenant itself), downloaded and
    /// compared line for line with the run's log over the API. A run that is not the site's fails its job; a requester
    /// holding the export key but not <c>pipelines:view</c> is refused as if the source did not exist.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class PipelineRunLogExportDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Api = "/api/DataExport";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public static TheoryData<string> Stores() => new() { SqliteGateStore.StoreName, PostgresGateStore.StoreName, RavenGateStore.StoreName };

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task A_runs_log_exports_through_the_queue_line_for_line(string storeName)
        {
            var settings = new JsonObject { ["DataExport"] = new JsonObject { ["Enabled"] = true }, ["Pipelines"] = new JsonObject { ["Enabled"] = true } };
            using var scratch = await GateInstallTemplates.CloneAsync(LiftedRequiresGateTests.ResolveStore(storeName), Host, output,
                colocateModuleAggregates: storeName == PostgresGateStore.StoreName, extraSettings: settings);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;

                // The export keys are declared with no role (fail closed): Administrators and a role holding ONLY export:create receive it.
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

                // ---- a finished run ------------------------------------------------------------------------------------------
                var created = await Send(app, client, cookies, hostId, HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Exported log" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                await Send(app, client, cookies, hostId, HttpMethod.Post, $"/api/Pipelines/{definitionId}/publish", new { }, "publish");
                var started = await Send(app, client, cookies, hostId, HttpMethod.Post, $"/api/Pipelines/{definitionId}/runs", new StartPipelineRequest { CorrelationId = "export-log" }, "start");
                var runId = JsonNode.Parse(started.Body)!["runId"]!.GetValue<string>();
                // A run is dispatched, not awaited: it answers 404 until the engine has created it.
                PipelineRunView? run = null;
                for (var attempt = 0; attempt < 100 && run?.IsFinished != true; attempt++)
                {
                    var read = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}", output, "the run");
                    run = read.Status == HttpStatusCode.OK ? JsonSerializer.Deserialize<PipelineRunView>(read.Body, Json) : null;
                    await Task.Delay(100);
                }

                Assert.True(run?.IsFinished, $"the run did not finish: {run?.Status}");
                var lines = JsonSerializer.Deserialize<List<PipelineRunLogLine>>((await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/log", output, "the run's log")).Body, Json)!;
                Assert.NotEmpty(lines);

                // ---- the source is listed; its arguments are checked before a job exists -----------------------------------------
                Assert.Contains(PipelineRunLogExportSource.SourceKey, (await GateDrive.GetAsync(client, cookies, Host, $"{Api}/sources", output, "sources")).Body);
                var noRun = await Send(app, client, cookies, hostId, HttpMethod.Post, Api, new { SourceKey = PipelineRunLogExportSource.SourceKey, Format = "Csv", ArgumentsJson = """{"definitionId":"x"}""" }, "an argument the source does not know");
                Assert.Equal(HttpStatusCode.BadRequest, noRun.Status);

                var exported = await Created(app, client, cookies, hostId, new { SourceKey = PipelineRunLogExportSource.SourceKey, Format = "Csv", ArgumentsJson = $$"""{"runId":"{{runId}}"}""" }, "the run's log");
                var missing = await Created(app, client, cookies, hostId, new { SourceKey = PipelineRunLogExportSource.SourceKey, Format = "Csv", ArgumentsJson = """{"runId":"no-such-run"}""" }, "a run that is not the site's");

                // ---- a requester with the export key but not pipelines:view: the source does not exist for them ----------------
                using (var narrowClient = GateDrive.NewClient(app, Host))
                {
                    var narrowCookies = await GateDrive.LoginAsync(app, narrowClient, Host, "exp-narrow", "Narr0w!Export$2026", output);
                    var refused = await Send(app, narrowClient, narrowCookies, narrow.UserId, HttpMethod.Post, Api, new { SourceKey = PipelineRunLogExportSource.SourceKey, Format = "Csv", ArgumentsJson = $$"""{"runId":"{{runId}}"}""" }, "narrow: the run's log");
                    Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
                    Assert.Contains("does not exist", refused.Body, StringComparison.OrdinalIgnoreCase);
                }

                // ---- render through the queue job: it has no request, so the source enters the engine tenant itself ------------
                var log = await GateDrive.InTenantAsync(app, Host, (sp, _) => new ExportQueueJob(sp.GetRequiredService<IServiceScopeFactory>()).ExecuteJobAsync(sp));
                output.WriteLine($"[{storeName}][job] {log}");
                var outstanding = 2;
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
                var jobs = await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                    (await sp.GetRequiredService<IExportJobRepository>().ListBySiteAsync(siteId)).ToDictionary(job => job.Id, job => (job.Status, job.FailureCode)));
                Assert.Equal(ExportJobStatus.Ready, jobs[exported].Status);
                Assert.Equal(ExportJobStatus.Failed, jobs[missing].Status); // another site's run, or none, does not exist for the source

                // ---- the file: every line of the run, in order ----------------------------------------------------------------
                var csv = Encoding.UTF8.GetString(await Download(client, cookies, exported)).TrimStart('﻿');
                output.WriteLine($"[{storeName}][csv] {csv.Replace("\r\n", " | ")}");
                var rows = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                Assert.Equal("Sequence,Time (UTC),Step,Step type,Event,Message", rows[0]);
                Assert.Equal(lines.Count, rows.Length - 1);
                Assert.Equal(lines.Select(line => line.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)), rows.Skip(1).Select(row => row.Split(',')[0]));
                Assert.Contains(lines, line => !string.IsNullOrEmpty(line.EventName) && csv.Contains(line.EventName, StringComparison.Ordinal));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private Task<(HttpStatusCode Status, string Body)> Send(WebApplication app, HttpClient client, CookieContainer cookies, int userId, HttpMethod method, string path, object body, string label) =>
            GateDrive.SendJsonAsync(app, client, cookies, Host, userId, method, path, body, output, label);

        private async Task<Guid> Created(WebApplication app, HttpClient client, CookieContainer cookies, int userId, object body, string label)
        {
            var response = await Send(app, client, cookies, userId, HttpMethod.Post, Api, body, label);
            Assert.True(response.Status == HttpStatusCode.OK, $"{label}: {(int)response.Status} {response.Body}");
            return JsonSerializer.Deserialize<Guid>(response.Body);
        }

        private async Task<byte[]> Download(HttpClient client, CookieContainer cookies, Guid id)
        {
            var uri = new Uri(new Uri("http://" + Host), $"{Api}/{id}/download");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var header = cookies.GetCookieHeader(uri);
            if (!string.IsNullOrEmpty(header))
            {
                request.Headers.TryAddWithoutValidation("Cookie", header);
            }

            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadAsByteArrayAsync();
        }
    }
}
