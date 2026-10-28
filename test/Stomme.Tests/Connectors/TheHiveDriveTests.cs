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
    /// BL-229 - TheHive DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port, against
    /// this machine's throwaway TheHive 5.5 (scripts/thehive-test-server.ps1) - fail-not-skip without it (D46): a TheHive connection
    /// made over the API, its key vaulted, tested; a published pipeline raising an alert, putting an observable on it - the alert
    /// named by an expression over the first step's result - finding the alert by its source, and deleting it; the key in neither
    /// the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class TheHiveDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_raises_an_alert_puts_an_observable_on_it_finds_it_and_deletes_it()
        {
            var key = TheHiveTestEndpoint.ApiKey;
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

                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/Connections/egress", new ConnectionEgressRequest { AllowedHosts = new Uri(TheHiveTestEndpoint.Endpoint).Host }, "the Host allows TheHive")).Status);
                var made = await Send(HttpMethod.Post, "/api/Connections", new ConnectionRequest
                {
                    Name = "Kafé larm", ConnectorType = "thehive", Endpoint = TheHiveTestEndpoint.Endpoint, TimeoutSeconds = 60,
                    Credentials = new() { ["api-key"] = key },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kassans larm" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string operation) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation } };
                var open = Call("open", "alert_create");
                open.Inputs["Parameters"] = $"type = phishing\nsource = {marker}\nsource_ref = {marker}-1\ntitle = Phishing against the till\ndescription = Mails to the till staff\nseverity = medium";
                open.OutputVariables["Result"] = "v-case";
                var indicator = Call("indicator", "observable_create");
                indicator.InputExpressions["Parameters"] = $"on = alert\nid = {{{{ opened[0].id }}}}\ndata_type = domain\ndata = {marker}.example\nmessage = Seen in the till's logs";
                var find = Call("find", "alerts");
                find.Inputs["Parameters"] = $"source = {marker}";
                var close = Call("close", "alert_delete");
                close.InputExpressions["Parameters"] = "alert = {{ opened[0].id }}";
                var graph = new PipelineGraph { Variables = { new PipelineGraphVariable { Id = "v-case", Name = "opened" } }, Steps = { open, indicator, find, close } };
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
                var alertId = Outputs("open")["Result"]![0]!["id"]!.GetValue<string>();
                Assert.Equal(("Phishing against the till", 2), (Outputs("open")["Result"]![0]!["title"]!.GetValue<string>(), Outputs("open")["Result"]![0]!["severity"]!.GetValue<int>()));
                var observable = Outputs("indicator")["Result"]![0]!;
                Assert.Equal(("domain", $"{marker}.example", "Seen in the till's logs"), (observable["dataType"]!.GetValue<string>(), observable["data"]!.GetValue<string>(), observable["message"]!.GetValue<string>()));
                var found = Outputs("find")["Result"]!.AsArray().Single()!;
                Assert.Equal((alertId, 1), (found["id"]!.GetValue<string>(), found["observableCount"]!.GetValue<int>()));
                Assert.Equal((alertId, "alert_delete"), (Outputs("close")["Result"]![0]!["id"]!.GetValue<string>(), Outputs("close")["Result"]![0]!["done"]!.GetValue<string>()));
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
