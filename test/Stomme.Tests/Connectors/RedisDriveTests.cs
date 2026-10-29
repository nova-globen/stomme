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
    /// Redis DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a real server (<see cref="RedisTestEndpoint"/>): a Redis connection made over the API - an address without a
    /// password, the password vaulted - refused while loopback is not allowlisted, then tested (the server named); a published
    /// pipeline sets an order's hash with a time to live, counts it, queues it and takes it off the queue, reads the hash back
    /// and deletes the keys; the password in neither the graph nor the run. Fails, never skips, without a server.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class RedisDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_keeps_an_order_in_redis()
        {
            var password = RedisTestEndpoint.Password; // no server: a failure that says how to get one
            var prefix = "stomme:drive:" + Guid.NewGuid().ToString("N")[..8] + ":";
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
                    Name = "Kafé cache", ConnectorType = "redis", Endpoint = $"{RedisTestEndpoint.Host}:{RedisTestEndpoint.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["username"] = "default", ["password"] = password },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var refused = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test before loopback is allowed");
                Assert.False(JsonNode.Parse(refused.Body)!["success"]!.GetValue<bool>(), refused.Body); // loopback is a private host: refused until allowlisted

                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/Connections/egress", new ConnectionEgressRequest { AllowedHosts = RedisTestEndpoint.Host }, "the Host allows the server")).Status);
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kafé cache" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("keep", "set", $"key = {prefix}order\nvalue = {{\"kund\": \"Kafé Ada\", \"summa\": 99.5}}\nttl = 600"),
                        Call("count", "incr", $"key = {prefix}orders\nby = 2"),
                        Call("queue", "push", $"list = {prefix}queue\nvalue = 1042\nend = tail"),
                        Call("take", "pop", $"list = {prefix}queue"),
                        Call("read", "get", $"key = {prefix}order"),
                        Call("drop", "delete", $"key = {prefix}order"),
                        Call("tidy", "delete", $"key = {prefix}orders"),
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
                JsonNode Result(string id) => JsonNode.Parse(JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>())!["Result"]![0]!;
                Assert.Equal(("hash", true), (Result("keep")["type"]!.GetValue<string>(), Result("keep")["set"]!.GetValue<bool>())); // a JSON object: a hash
                Assert.Equal(2, Result("count")["value"]!.GetValue<int>());
                Assert.Equal(1, Result("queue")["length"]!.GetValue<int>());
                Assert.Equal("1042", Result("take")["value"]!.GetValue<string>());
                var order = JsonNode.Parse(Result("read")["value"]!.GetValue<string>())!;
                Assert.Equal(("Kafé Ada", "99.5"), (order["kund"]!.GetValue<string>(), order["summa"]!.GetValue<string>()));
                Assert.True(Result("drop")["deleted"]!.GetValue<bool>());
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
