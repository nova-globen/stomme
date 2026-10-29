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
    /// RabbitMQ DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a real RabbitMQ 4.1 (<see cref="RabbitMqTestEndpoint"/>): a RabbitMQ connection made over the API - an address
    /// that carries no credential, the password vaulted - refused while loopback is not allowlisted, then tested (the broker
    /// named); a published pipeline publishes two messages to a queue - one with its headers, one over several lines from the
    /// step's Query box - counts the queue, takes the messages and counts it again; the password in neither the graph nor the
    /// run. Fails, never skips, without a broker.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class RabbitMqDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_publishes_counts_and_takes_messages()
        {
            var password = RabbitMqTestEndpoint.Password; // no broker: a failure that says how to get one
            var queue = "kassa.orders." + Guid.NewGuid().ToString("N")[..8];
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
                    Name = "Kafé broker", ConnectorType = "rabbitmq", Endpoint = $"amqp://{RabbitMqTestEndpoint.Host}:{RabbitMqTestEndpoint.Port}/", TimeoutSeconds = 15,
                    Credentials = new() { ["username"] = RabbitMqTestEndpoint.User, ["password"] = password },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var refused = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test before loopback is allowed");
                Assert.False(JsonNode.Parse(refused.Body)!["success"]!.GetValue<bool>(), refused.Body); // loopback is a private host: refused until allowlisted

                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/Connections/egress", new ConnectionEgressRequest { AllowedHosts = RabbitMqTestEndpoint.Host }, "the Host allows the broker")).Status);
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kassa queue" }, "create");
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
                        Call("order", "publish", $"queue = {queue}\nmessage = {{\"nummer\": 1042, \"kund\": \"Kafé Ada\"}}\ncontent_type = application/json\nheaders = {{\"source\": \"kassa\"}}\ndurable = false"),
                        Call("note", "publish", $"queue = {queue}\ndurable = false", "Kassan stängd\nÅter 08:00"),
                        Call("count", "count", $"queue = {queue}"),
                        Call("take", "get", $"queue = {queue}\nmax = 10"),
                        Call("after", "count", $"queue = {queue}"),
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
                for (var attempt = 0; attempt < 150 && run?.IsFinished != true; attempt++)
                {
                    var read = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}", output, "the run");
                    run = read.Status == HttpStatusCode.OK ? JsonSerializer.Deserialize<PipelineRunView>(read.Body, Json) : null;
                    await Task.Delay(100);
                }

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.True(run?.SubStatus == "Finished", steps);
                JsonNode Outputs(string id) => JsonNode.Parse(JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>())!;
                Assert.True(Outputs("order")["Result"]![0]!["confirmed"]!.GetValue<bool>()); // the broker took it
                Assert.Equal(2, Outputs("count")["Result"]![0]!["messages"]!.GetValue<int>());
                var taken = Outputs("take")["Result"]!.AsArray();
                Assert.Equal(new[] { "{\"nummer\": 1042, \"kund\": \"Kafé Ada\"}", "Kassan stängd\nÅter 08:00" }, taken.Select(message => message!["body"]!.GetValue<string>()));
                Assert.Equal(("application/json", "{\"source\":\"kassa\"}"), (taken[0]!["content_type"]!.GetValue<string>(), taken[0]!["headers"]!.GetValue<string>()));
                Assert.Equal(0, Outputs("after")["Result"]![0]!["messages"]!.GetValue<int>()); // taken and acknowledged
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(password, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(password, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);

                // The queue the run declared, deleted so the throwaway broker does not gather a run's queues.
                var factory = Stomme.Modules.Connectors.RabbitMq.RabbitMqConnector.Build($"amqp://{RabbitMqTestEndpoint.Host}:{RabbitMqTestEndpoint.Port}/", new System.Collections.Generic.Dictionary<string, string> { ["username"] = RabbitMqTestEndpoint.User, ["password"] = password }, TimeSpan.FromSeconds(15), out _, out _);
                factory.HostName = RabbitMqTestEndpoint.Host;
                await using var connection = await factory.CreateConnectionAsync();
                await using var channel = await connection.CreateChannelAsync();
                await channel.QueueDeleteAsync(queue, ifUnused: false, ifEmpty: false);
            }
        }
    }
}
