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
    /// BL-229 - Metabase DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a real Metabase 0.63 (<c>scripts/metabase-test-server.ps1</c>'s throwaway on loopback): a connection made over the
    /// API with an API key, tested; a published pipeline listing the questions, reading the first one's result as rows by its
    /// number from the first step's records, saving its CSV into the run's folder and listing the databases; the key in neither the
    /// graph nor the run. Fails, never skips, without Metabase (D46).
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class MetabaseDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_reads_questions_their_results_and_databases()
        {
            var key = MetabaseTestEndpoint.ApiKey;
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

                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/Connections/egress", new ConnectionEgressRequest { AllowedHosts = MetabaseTestEndpoint.Host }, "the Host allows loopback")).Status);
                var made = await Send(HttpMethod.Post, "/api/Connections", new ConnectionRequest
                {
                    Name = "Kafé rapporter", ConnectorType = "metabase", Endpoint = MetabaseTestEndpoint.Endpoint, TimeoutSeconds = 60,
                    Credentials = new() { ["password"] = key },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kassans rapporter" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                var graph = new PipelineGraph
                {
                    Variables = { new PipelineGraphVariable { Id = "v-questions", Name = "questions" } },
                    Steps =
                    {
                        new() { Id = "list", Type = "Stomme.ExtractFromConnection", Name = "list", Inputs = { ["Connection"] = connection, ["Operation"] = "questions", ["Parameters"] = "max = 3" }, OutputVariables = { ["Result"] = "v-questions" } },
                        new() { Id = "rows", Type = "Stomme.ExtractFromConnection", Name = "rows", Inputs = { ["Connection"] = connection, ["Operation"] = "question_result" }, InputExpressions = { ["Parameters"] = "question = {{ questions[0].id }}\nmax = 5" } },
                        new() { Id = "csv", Type = "Stomme.ExtractFromConnection", Name = "csv", Inputs = { ["Connection"] = connection, ["Operation"] = "question_result" }, InputExpressions = { ["Parameters"] = "question = {{ questions[0].id }}\nformat = csv\nfile = rapporter/" } },
                        new() { Id = "databases", Type = "Stomme.ExtractFromConnection", Name = "databases", Inputs = { ["Connection"] = connection, ["Operation"] = "databases", ["Parameters"] = "" } },
                    },
                };
                for (var i = 1; i < graph.Steps.Count; i++)
                {
                    graph.Connections.Add(new PipelineGraphConnection { SourceId = graph.Steps[i - 1].Id, SourcePort = "Done", TargetId = graph.Steps[i].Id });
                }

                var saved = await Send(HttpMethod.Put, $"/api/Pipelines/{definitionId}/graph", graph, "save the graph");
                Assert.True(saved.Status == HttpStatusCode.OK, saved.Body);
                var published = await Send(HttpMethod.Post, $"/api/Pipelines/{definitionId}/publish", new { }, "publish");
                Assert.True(JsonNode.Parse(published.Body)!["succeeded"]!.GetValue<bool>(), published.Body);
                var started = await Send(HttpMethod.Post, $"/api/Pipelines/{definitionId}/runs", new StartPipelineRequest(), "run");
                var runId = JsonNode.Parse(started.Body)!["runId"]!.GetValue<string>();
                PipelineRunView? run = null;
                for (var attempt = 0; attempt < 900 && run?.IsFinished != true; attempt++)
                {
                    var read = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}", output, "the run");
                    run = read.Status == HttpStatusCode.OK ? JsonSerializer.Deserialize<PipelineRunView>(read.Body, Json) : null;
                    await Task.Delay(100);
                }

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.True(run?.SubStatus == "Finished", steps);
                JsonNode Outputs(string id) => JsonNode.Parse(JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>())!;
                var first = Outputs("list")["Result"]![0]!["id"]!.GetValue<long>();
                Assert.Equal(3, Outputs("list")["Result"]!.AsArray().Count);
                Assert.InRange(Outputs("rows")["Result"]!.AsArray().Count, 1, 5);
                Assert.Equal($"rapporter/question-{first}.csv", Outputs("csv")["Result"]![0]!["file"]!.GetValue<string>());
                Assert.True(Outputs("csv")["Result"]![0]!["size"]!.GetValue<long>() > 0);
                Assert.Contains(Outputs("databases")["Result"]!.AsArray(), database => database!["name"]!.GetValue<string>() == "Sample Database");

                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(key, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(key, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
