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
    /// BL-229 - AMQP 1.0 DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against this machine's throwaway RabbitMQ 4.1 (scripts/rabbitmq-test-server.ps1) - fail-not-skip without it (D46): an AMQP
    /// connection made over the API, its password vaulted, tested; a published pipeline sending an order to a queue, receiving
    /// it back and sending its id on - an expression over the message received; the password in neither the graph nor the run.
    /// The queue is deleted afterwards.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class AmqpDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_sends_an_order_receives_it_and_sends_its_id_on()
        {
            var queue = "stomme-amqp-drive-" + Guid.NewGuid().ToString("N")[..8];
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

                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/Connections/egress", new ConnectionEgressRequest { AllowedHosts = RabbitMqTestEndpoint.Host }, "the Host allows the broker")).Status);
                var made = await Send(HttpMethod.Post, "/api/Connections", new ConnectionRequest
                {
                    Name = "Kassans kö", ConnectorType = "amqp", Endpoint = $"amqp://{RabbitMqTestEndpoint.Host}:{RabbitMqTestEndpoint.Port}", TimeoutSeconds = 30,
                    Credentials = new() { ["username"] = RabbitMqTestEndpoint.User, ["password"] = RabbitMqTestEndpoint.Password },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Ordrar genom kön" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string operation, string variable) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation }, OutputVariables = { ["Result"] = variable } };
                var order = Call("order", "send", "v-sent");
                order.Inputs["Parameters"] = $"to = {queue}\nmessage = {{\"order\": 7}}\ncontent_type = application/json\nmessage_id = o-7";
                var take = Call("take", "receive", "v-taken");
                take.Inputs["Parameters"] = $"from = /queues/{queue}\nwait = 10";
                var forward = Call("forward", "send", "v-forwarded");
                forward.InputExpressions["Parameters"] = $"to = /queues/{queue}\nmessage = handled {{{{ taken[0].message_id }}}}";
                var check = Call("check", "receive", "v-checked");
                check.Inputs["Parameters"] = $"from = /queues/{queue}\nwait = 10";
                var graph = new PipelineGraph
                {
                    Variables =
                    {
                        new PipelineGraphVariable { Id = "v-sent", Name = "sent" }, new PipelineGraphVariable { Id = "v-taken", Name = "taken" },
                        new PipelineGraphVariable { Id = "v-forwarded", Name = "forwarded" }, new PipelineGraphVariable { Id = "v-checked", Name = "checked" },
                    },
                    Steps = { order, take, forward, check },
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
                JsonNode Outputs(string id) => JsonNode.Parse(JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>())!;
                Assert.True(run?.SubStatus == "Finished", steps);
                Assert.True(Outputs("order")["Result"]![0]!["accepted"]!.GetValue<bool>());
                Assert.Equal(("{\"order\": 7}", "o-7"), (Outputs("take")["Result"]![0]!["body"]!.GetValue<string>(), Outputs("take")["Result"]![0]!["message_id"]!.GetValue<string>()));
                Assert.Equal("handled o-7", Assert.Single(Outputs("check")["Result"]!.AsArray())!["body"]!.GetValue<string>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(RabbitMqTestEndpoint.Password, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(RabbitMqTestEndpoint.Password, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
                var factory = new RabbitMQ.Client.ConnectionFactory { HostName = RabbitMqTestEndpoint.Host, Port = RabbitMqTestEndpoint.Port, UserName = RabbitMqTestEndpoint.User, Password = RabbitMqTestEndpoint.Password };
                await using var broker = await factory.CreateConnectionAsync();
                await using var channel = await broker.CreateChannelAsync();
                await channel.QueueDeleteAsync(queue, ifUnused: false, ifEmpty: false);
            }
        }
    }
}
