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
    /// Elastic Security DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction
    /// port, against this machine's throwaway Kibana 9.5 over its own Elasticsearch (scripts/kibana-test-server.ps1) -
    /// fail-not-skip without it: an Elastic Security connection made over the API, its password vaulted, tested; a published
    /// pipeline opening a case, commenting on it over two lines from the Query box and closing it - the case named by an
    /// expression over the first step's result - then deleting it; the password in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class ElasticSecurityDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_opens_a_case_comments_on_it_closes_and_deletes_it()
        {
            var password = KibanaTestEndpoint.Password;
            var marker = "kafe-" + Guid.NewGuid().ToString("N")[..10];
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

                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/Connections/egress", new ConnectionEgressRequest { AllowedHosts = new Uri(KibanaTestEndpoint.Endpoint).Host }, "the Host allows Kibana")).Status);
                var made = await Send(HttpMethod.Post, "/api/Connections", new ConnectionRequest
                {
                    Name = "Kafé SOC", ConnectorType = "elasticsecurity", Endpoint = KibanaTestEndpoint.Endpoint, TimeoutSeconds = 60,
                    Credentials = new() { ["username"] = KibanaTestEndpoint.User, ["password"] = password },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kassans incident" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string operation) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation } };
                var open = Call("open", "case_create");
                open.Inputs["Parameters"] = $"title = Phishing against the till {marker}\ndescription = Mails to the till staff\ntags = {marker}\nseverity = high";
                open.OutputVariables["Result"] = "v-case";
                var note = Call("note", "case_comment_add");
                note.InputExpressions["Parameters"] = "case = {{ opened[0].id }}";
                note.Inputs["Query"] = "Seen in the till logs\nat 08:00";
                var close = Call("close", "case_update");
                close.InputExpressions["Parameters"] = "case = {{ opened[0].id }}\nstatus = closed";
                var remove = Call("remove", "case_delete");
                remove.InputExpressions["Parameters"] = "case = {{ opened[0].id }}";
                var graph = new PipelineGraph { Variables = { new PipelineGraphVariable { Id = "v-case", Name = "opened" } }, Steps = { open, note, close, remove } };
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
                var opened = Outputs("open")["Result"]![0]!;
                var caseId = opened["id"]!.GetValue<string>();
                Assert.Equal(($"Phishing against the till {marker}", "high", "open"), (opened["title"]!.GetValue<string>(), opened["severity"]!.GetValue<string>(), opened["status"]!.GetValue<string>()));
                Assert.Equal("Seen in the till logs\nat 08:00", Outputs("note")["Result"]![0]!["comment"]!.GetValue<string>());
                Assert.Equal((caseId, "closed"), (Outputs("close")["Result"]![0]!["id"]!.GetValue<string>(), Outputs("close")["Result"]![0]!["status"]!.GetValue<string>()));
                Assert.Equal((caseId, true), (Outputs("remove")["Result"]![0]!["id"]!.GetValue<string>(), Outputs("remove")["Result"]![0]!["deleted"]!.GetValue<bool>()));
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(password, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(password, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
