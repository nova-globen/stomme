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
    /// Rundeck DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port, against
    /// this machine's throwaway Rundeck 6.2 (scripts/rundeck-test-server.ps1) - fail-not-skip without it: a Rundeck connection
    /// made over the API, its token vaulted, tested; a published pipeline finding the menu job in its project, running it - its job
    /// and its options expressions over the first step's result and the run's input - waited for, and reading its output; the
    /// token in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class RundeckDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_finds_a_job_runs_it_and_reads_its_output()
        {
            var token = RundeckTestEndpoint.Token;
            var marker = "dag-" + Guid.NewGuid().ToString("N")[..8];
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

                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/Connections/egress", new ConnectionEgressRequest { AllowedHosts = new Uri(RundeckTestEndpoint.Endpoint).Host }, "the Host allows Rundeck")).Status);
                var made = await Send(HttpMethod.Post, "/api/Connections", new ConnectionRequest
                {
                    Name = "Kafé drift", ConnectorType = "rundeck", Endpoint = RundeckTestEndpoint.Endpoint, TimeoutSeconds = 60,
                    Credentials = new() { ["api-key"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kassans meny" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string operation, string variable) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation }, OutputVariables = { ["Result"] = variable } };
                var find = Call("find", "jobs", "v-jobs");
                find.Inputs["Parameters"] = "project = kafe\ngroup = kassa\nname = Dagens meny";
                var run = Call("run", "run", "v-run");
                run.InputExpressions["Parameters"] = $"job = {{{{ jobs[0].id }}}}\noption.day = {marker}\noption.dish = fisk -day söndag\nwait = 60";
                var read = Call("read", "execution_output", "v-output");
                read.InputExpressions["Parameters"] = "execution = {{ runs[0].id }}";
                var graph = new PipelineGraph
                {
                    Variables = { new PipelineGraphVariable { Id = "v-jobs", Name = "jobs" }, new PipelineGraphVariable { Id = "v-run", Name = "runs" }, new PipelineGraphVariable { Id = "v-output", Name = "lines" } },
                    Steps = { find, run, read },
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
                PipelineRunView? view = null;
                for (var attempt = 0; attempt < 1200 && view?.IsFinished != true; attempt++)
                {
                    var answer = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}", output, "the run");
                    view = answer.Status == HttpStatusCode.OK ? JsonSerializer.Deserialize<PipelineRunView>(answer.Body, Json) : null;
                    await Task.Delay(100);
                }

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.True(view?.SubStatus == "Finished", steps);
                JsonNode Outputs(string id) => JsonNode.Parse(JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>())!;
                Assert.Equal(("succeeded", true), (Outputs("run")["Result"]![0]!["status"]!.GetValue<string>(), Outputs("run")["Result"]![0]!["finished"]!.GetValue<bool>()));
                Assert.Equal($"meny {marker} fisk -day söndag", Outputs("run")["Result"]![0]!["output"]!.GetValue<string>());
                Assert.Contains(Outputs("read")["Result"]!.AsArray(), line => line!["log"]!.GetValue<string>() == $"meny {marker} fisk -day söndag");
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
