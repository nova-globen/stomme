#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Models.Pipelines;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// The spreadsheet and ZIP steps on an installed SQLite host, in a pipeline authored, published and run over the API on the
    /// engine's worker: CSV parsed into records, written as a spreadsheet in the site's pipeline folder through the host's own
    /// renderer and cell formatter (the Formats module's, scoped), its folder zipped and unzipped into another (BL-95's ZIP
    /// residue, 2026-10-03), read back from there, validated at exactly its row count, logged. What the engine-level tests cannot
    /// see: the host's composition of the renderer registry, the formatter, the file root and the unzip caps from its own
    /// configuration root.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class SpreadsheetStepsDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_writes_a_spreadsheet_zips_and_unzips_it_and_reads_it_back_on_an_installed_host()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false,
                extraSettings: new JsonObject { ["Pipelines"] = new JsonObject { ["Enabled"] = true } });
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);

                var created = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Spreadsheet round trip" }, output, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        new PipelineGraphStep { Id = "parse", Type = "Stomme.ParseCsv", Name = "Parse", Inputs = { ["Text"] = "sku,qty\nA1,3\nB2,5\nC3,8\n" } },
                        new PipelineGraphStep { Id = "write", Type = "Stomme.WriteSpreadsheet", Name = "Write", Inputs = { ["Path"] = "reports/stock.xlsx", ["SheetName"] = "Stock" } },
                        new PipelineGraphStep { Id = "zip", Type = "Stomme.CreateZipArchive", Name = "Zip", Inputs = { ["Source"] = "reports", ["Destination"] = "archive/reports.zip" } },
                        new PipelineGraphStep { Id = "unzip", Type = "Stomme.ExtractZipArchive", Name = "Unzip", Inputs = { ["Path"] = "archive/reports.zip", ["Destination"] = "restored" } },
                        new PipelineGraphStep { Id = "read", Type = "Stomme.ReadSpreadsheet", Name = "Read", Inputs = { ["Path"] = "restored/stock.xlsx" } },
                        new PipelineGraphStep { Id = "check", Type = "Stomme.ValidateRecords", Name = "Check", Inputs = { ["Rules"] = "sku required\nqty required", ["MinRows"] = "3", ["MaxRows"] = "3", ["FailureMode"] = "failfast", ["OnFailure"] = "Continue" } },
                        new PipelineGraphStep { Id = "log", Type = "Stomme.LogMessage", Name = "Log", Inputs = { ["Message"] = "three rows came back from the spreadsheet", ["Level"] = "Warning" } },
                    },
                    Connections =
                    {
                        new PipelineGraphConnection { SourceId = "parse", SourcePort = "Done", TargetId = "write" },
                        new PipelineGraphConnection { SourceId = "write", SourcePort = "Done", TargetId = "zip" },
                        new PipelineGraphConnection { SourceId = "zip", SourcePort = "Done", TargetId = "unzip" },
                        new PipelineGraphConnection { SourceId = "unzip", SourcePort = "Done", TargetId = "read" },
                        new PipelineGraphConnection { SourceId = "read", SourcePort = "Done", TargetId = "check" },
                        new PipelineGraphConnection { SourceId = "check", SourcePort = "Passed", TargetId = "log" },
                    },
                };
                Assert.Equal(HttpStatusCode.OK, (await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Put, $"/api/Pipelines/{definitionId}/graph", graph, output, "save the graph")).Status);
                Assert.Equal(HttpStatusCode.OK, (await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"/api/Pipelines/{definitionId}/publish", new { }, output, "publish")).Status);
                var started = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"/api/Pipelines/{definitionId}/runs", new StartPipelineRequest(), output, "start");
                var runId = JsonNode.Parse(started.Body)!["runId"]!.GetValue<string>();

                PipelineRunView? run = null;
                for (var attempt = 0; attempt < 150 && run?.IsFinished != true; attempt++)
                {
                    var read = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}", output, "the run");
                    run = read.Status == HttpStatusCode.OK ? JsonSerializer.Deserialize<PipelineRunView>(read.Body, Json) : null;
                    await Task.Delay(100);
                }

                var log = JsonSerializer.Deserialize<List<PipelineRunLogLine>>((await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/log", output, "the run's log")).Body, Json)!;
                foreach (var line in log)
                {
                    output.WriteLine($"[log] {line.StepType} {line.EventName} {line.Message}");
                }

                Assert.True(run?.IsFinished, $"the run did not finish: {run?.Status} {run?.SubStatus}");
                Assert.DoesNotContain(log, line => line.EventName is "Failed" or "Faulted");
                Assert.Contains(log, line => line.StepType == "Stomme.WriteSpreadsheet" && line.EventName == "Completed");
                Assert.Contains(log, line => line.StepType == "Stomme.CreateZipArchive" && line.EventName == "Completed");
                Assert.Contains(log, line => line.StepType == "Stomme.ExtractZipArchive" && line.EventName == "Completed");
                Assert.Contains(log, line => line.StepType == "Stomme.LogMessage" && line.EventName == "Warning" && line.Message == "three rows came back from the spreadsheet");
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
