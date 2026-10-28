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
    /// BL-229 - Home Assistant DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction
    /// port, against a real Home Assistant 2026.9 (<see cref="HomeAssistantTestEndpoint"/>): a Home Assistant connection made over
    /// the API - its address, the access token vaulted - refused while loopback is not allowlisted, then tested; a published
    /// pipeline sets a sensor's state, reads it, switches an input_boolean by a service, fires an event and renders a template
    /// written over several lines in the step's Query box; the token in neither the graph nor the run. Fails, never skips, without
    /// a server (D46).
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class HomeAssistantDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_sets_a_state_switches_an_entity_and_renders_a_template()
        {
            var token = HomeAssistantTestEndpoint.Token; // no server: a failure that says how to get one
            var sensor = "sensor.kassa_queue_" + Guid.NewGuid().ToString("N")[..8];
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

                var made = await Send(HttpMethod.Post, "/api/Connections", new ConnectionRequest
                {
                    Name = "Kafé hemma", ConnectorType = "homeassistant", Endpoint = HomeAssistantTestEndpoint.Endpoint, TimeoutSeconds = 30,
                    Credentials = new() { ["token"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var refused = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test before loopback is allowed");
                Assert.False(JsonNode.Parse(refused.Body)!["success"]!.GetValue<bool>(), refused.Body); // loopback is a private host: refused until allowlisted

                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/Connections/egress", new ConnectionEgressRequest { AllowedHosts = HomeAssistantTestEndpoint.Host }, "the Host allows the server")).Status);
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kassa at home" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string operation, string parameters, string? query = null)
                {
                    var step = new PipelineGraphStep { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                    if (query is not null)
                    {
                        step.Inputs["Query"] = query;
                    }

                    return step;
                }

                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("queue", "set_state", $"entity = {sensor}\nstate = 7\nattributes = {{\"unit_of_measurement\": \"people\"}}"),
                        Call("read", "state", $"entity = {sensor}"),
                        Call("toggle", "call_service", "domain = input_boolean\nservice = toggle\ndata = {\"entity_id\": \"input_boolean.kassa_open\"}"),
                        Call("opened", "fire_event", "event = kassa_opened\ndata = {\"till\": 2}"),
                        Call("say", "render_template", "", $"Queue: {{{{ states('{sensor}') }}}}\nKassa: {{{{ states('input_boolean.kassa_open') }}}}"),
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
                for (var attempt = 0; attempt < 300 && run?.IsFinished != true; attempt++)
                {
                    var read = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}", output, "the run");
                    run = read.Status == HttpStatusCode.OK ? JsonSerializer.Deserialize<PipelineRunView>(read.Body, Json) : null;
                    await Task.Delay(100);
                }

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.True(run?.SubStatus == "Finished", steps);
                JsonNode Outputs(string id) => JsonNode.Parse(JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>())!;
                Assert.Equal((sensor, "7"), (Outputs("read")["Result"]![0]!["entity_id"]!.GetValue<string>(), Outputs("read")["Result"]![0]!["state"]!.GetValue<string>()));
                var kassa = Outputs("toggle")["Result"]![0]!;
                Assert.Equal("input_boolean.kassa_open", kassa["entity_id"]!.GetValue<string>());
                Assert.Equal("Event kassa_opened fired.", Outputs("opened")["Result"]![0]!["message"]!.GetValue<string>());
                Assert.Equal($"Queue: 7\nKassa: {kassa["state"]!.GetValue<string>()}", Outputs("say")["Result"]![0]!["renderedTemplate"]!.GetValue<string>()); // the Query box's two lines, rendered
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
