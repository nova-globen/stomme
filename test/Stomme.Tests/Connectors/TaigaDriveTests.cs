#nullable enable
using System;
using System.Globalization;
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
    /// Taiga DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port, against
    /// this machine's throwaway Taiga 6.10 (scripts/taiga-test-server.ps1) - fail-not-skip without it: a Taiga connection made
    /// over the API, its password vaulted, tested; a published pipeline making an issue in a project of the test's own, closing it
    /// by its status's name - an expression over the first step's result - and finding it among the project's closed issues; the
    /// password in neither the graph nor the run. The project is deleted again afterwards.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class TaigaDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_makes_an_issue_closes_it_and_finds_it_closed()
        {
            var password = TaigaTestEndpoint.Password;
            var project = await TaigaTestEndpoint.MakeProjectAsync("Kafé drift " + Guid.NewGuid().ToString("N")[..8]);
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

                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/Connections/egress", new ConnectionEgressRequest { AllowedHosts = new Uri(TaigaTestEndpoint.Endpoint).Host }, "the Host allows Taiga")).Status);
                var made = await Send(HttpMethod.Post, "/api/Connections", new ConnectionRequest
                {
                    Name = "Kafé ärenden", ConnectorType = "taiga", Endpoint = TaigaTestEndpoint.Endpoint, TimeoutSeconds = 60,
                    Credentials = new() { ["username"] = TaigaTestEndpoint.Username, ["password"] = password },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kassans ärenden" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string operation, string variable) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation }, OutputVariables = { ["Result"] = variable } };
                var p = project.ToString(CultureInfo.InvariantCulture);
                var open = Call("open", "issue_create", "v-issue");
                open.Inputs["Parameters"] = $"project = {p}\nsubject = Kaffet tog slut\npriority = High\ntags = kaffe";
                var close = Call("close", "issue_update", "v-closed");
                close.InputExpressions["Parameters"] = "issue = {{ issues[0].id }}\nstatus = Closed";
                var find = Call("find", "issues", "v-found");
                find.Inputs["Parameters"] = $"project = {p}\nclosed = true\ntags = kaffe";
                var graph = new PipelineGraph
                {
                    Variables = { new PipelineGraphVariable { Id = "v-issue", Name = "issues" }, new PipelineGraphVariable { Id = "v-closed", Name = "closed" }, new PipelineGraphVariable { Id = "v-found", Name = "found" } },
                    Steps = { open, close, find },
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
                for (var attempt = 0; attempt < 600 && run?.IsFinished != true; attempt++)
                {
                    var read = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}", output, "the run");
                    run = read.Status == HttpStatusCode.OK ? JsonSerializer.Deserialize<PipelineRunView>(read.Body, Json) : null;
                    await Task.Delay(100);
                }

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.True(run?.SubStatus == "Finished", steps);
                JsonNode Outputs(string id) => JsonNode.Parse(JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>())!;
                var issue = Outputs("open")["Result"]![0]!["id"]!.GetValue<long>();
                Assert.Equal(("Kaffet tog slut", "New"), (Outputs("open")["Result"]![0]!["subject"]!.GetValue<string>(), Outputs("open")["Result"]![0]!["status_name"]!.GetValue<string>()));
                Assert.Equal("Closed", Outputs("close")["Result"]![0]!["status_name"]!.GetValue<string>());
                Assert.Equal(issue, Assert.Single(Outputs("find")["Result"]!.AsArray())!["id"]!.GetValue<long>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(password, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(password, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
                await TaigaTestEndpoint.DeleteProjectAsync(project);
            }
        }
    }
}
