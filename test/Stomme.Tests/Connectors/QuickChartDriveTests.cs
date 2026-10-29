#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Models.Pipelines;
using Stomme.Tests.Pipelines;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// QuickChart DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against this machine's throwaway QuickChart 1.8 (scripts/quickchart-test-server.ps1), never quickchart.io - fail-not-skip
    /// without it: a QuickChart connection made over the API, with no credentials, and tested; a published pipeline drawing a
    /// bar chart into the run's folder and a line chart whose numbers are an expression over the first chart's record.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class QuickChartDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_draws_two_charts_into_its_folder()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false,
                extraSettings: new JsonObject { ["Pipelines"] = new JsonObject { ["Enabled"] = true } });
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                Task<(HttpStatusCode Status, string Body)> Send(HttpMethod method, string path, object body, string label) =>
                    GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, method, path, body, output, label);

                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/Connections/egress", new ConnectionEgressRequest { AllowedHosts = new Uri(QuickChartTestEndpoint.Endpoint).Host }, "the Host allows QuickChart")).Status);
                var made = await Send(HttpMethod.Post, "/api/Connections", new ConnectionRequest
                {
                    Name = "Kafé diagram", ConnectorType = "quickchart", Endpoint = QuickChartTestEndpoint.Endpoint, TimeoutSeconds = 60,
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body + " " + QuickChartTestEndpoint.Missing);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kassans diagram" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                var bar = new PipelineGraphStep { Id = "bar", Type = "Stomme.ExtractFromConnection", Name = "bar", Inputs = { ["Connection"] = connection, ["Operation"] = "chart", ["Parameters"] = "labels = Mån, Tis, Ons\ndata = 12, 19, 7\nlabel = Bullar\nfile = rapport/bullar.png" }, OutputVariables = { ["Result"] = "v-bar" } };
                var line = new PipelineGraphStep { Id = "line", Type = "Stomme.ExtractFromConnection", Name = "line", Inputs = { ["Connection"] = connection, ["Operation"] = "chart" }, InputExpressions = { ["Parameters"] = "type = line\ndata = {{ bars[0].width }}, {{ bars[0].height }}\nformat = svg\nfile = rapport/" }, OutputVariables = { ["Result"] = "v-line" } };
                var graph = new PipelineGraph
                {
                    Variables = { new PipelineGraphVariable { Id = "v-bar", Name = "bars" }, new PipelineGraphVariable { Id = "v-line", Name = "lines" } },
                    Steps = { bar, line },
                    Connections = { new PipelineGraphConnection { SourceId = "bar", SourcePort = "Done", TargetId = "line" } },
                };
                var saved = await Send(HttpMethod.Put, $"/api/Pipelines/{definitionId}/graph", graph, "save the graph");
                Assert.True(saved.Status == HttpStatusCode.OK, saved.Body);
                var published = await Send(HttpMethod.Post, $"/api/Pipelines/{definitionId}/publish", new { }, "publish");
                Assert.True(JsonNode.Parse(published.Body)!["succeeded"]!.GetValue<bool>(), published.Body);
                var started = await Send(HttpMethod.Post, $"/api/Pipelines/{definitionId}/runs", new StartPipelineRequest(), "run");
                var runId = JsonNode.Parse(started.Body)!["runId"]!.GetValue<string>();
                PipelineRunView? run = null;
                for (var attempt = 0; attempt < 600 && run?.IsFinished != true; attempt++)
                {
                    var read = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}", output, "the run");
                    run = read.Status == HttpStatusCode.OK ? JsonSerializer.Deserialize<PipelineRunView>(read.Body, Json) : null;
                    await Task.Delay(100);
                }

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.True(run?.SubStatus == "Finished", steps);
                JsonNode Outputs(string id) => JsonNode.Parse(JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>())!;
                var first = Outputs("bar")["Result"]![0]!;
                Assert.Equal(("rapport/bullar.png", "image/png"), (first["file"]!.GetValue<string>(), first["content_type"]!.GetValue<string>()));
                Assert.True(first["size"]!.GetValue<long>() > 1000);
                var second = Outputs("line")["Result"]![0]!;
                Assert.Equal(("rapport/chart.svg", "image/svg+xml"), (second["file"]!.GetValue<string>(), second["content_type"]!.GetValue<string>()));
                Assert.Contains("[500,300]", second["chart"]!.GetValue<string>(), StringComparison.Ordinal); // the first chart's size, through the expression
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
