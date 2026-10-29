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
    /// Azure Cosmos DB DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction
    /// port, against Microsoft's Linux emulator (<see cref="CosmosDbTestEndpoint"/>): a Cosmos DB connection made over the API - the
    /// account's address, its database a setting, the key vaulted - refused while loopback is not allowlisted, then tested; a
    /// published pipeline makes a container partitioned by /till, adds two orders in two partitions, raises one's total, queries
    /// the big ones with a query written over several lines in the step's Query box and its parameter bound, and drops the
    /// container; the key in neither the graph nor the run. Fails, never skips, without the emulator.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class AzureCosmosDbDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_keeps_orders_in_a_container_and_queries_them()
        {
            var container = "orders" + Guid.NewGuid().ToString("N")[..8];
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
                    Name = "Kafé orders", ConnectorType = "azurecosmosdb", Endpoint = CosmosDbTestEndpoint.Endpoint, TimeoutSeconds = 30,
                    Settings = new() { ["database"] = CosmosDbTestEndpoint.Database },
                    Credentials = new() { ["api-key"] = CosmosDbTestEndpoint.Key },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var refused = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test before loopback is allowed");
                Assert.False(JsonNode.Parse(refused.Body)!["success"]!.GetValue<bool>(), refused.Body); // loopback is a private host: refused until allowlisted

                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/Connections/egress", new ConnectionEgressRequest { AllowedHosts = CosmosDbTestEndpoint.Host }, "the Host allows the emulator")).Status);
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kafé orders" }, "create");
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
                        Call("container", "container_create", $"container = {container}\npartition_key = /till"),
                        Call("first", "item_create", $"container = {container}\nitem = {{\"id\": \"1042\", \"till\": \"2\", \"total\": 35.5}}"),
                        Call("second", "item_create", $"container = {container}\nitem = {{\"id\": \"1043\", \"till\": \"1\", \"total\": 8}}"),
                        Call("raise", "item_update", $"container = {container}\nid = 1043\npartition = 1\nitem = {{\"total\": 12}}"),
                        Call("big", "query", $"container = {container}\nparameters = {{\"@min\": 10}}", "SELECT c.id, c.till, c.total FROM c\nWHERE c.total > @min"),
                        Call("drop", "container_delete", $"container = {container}"),
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
                Assert.Equal(container, Outputs("container")["Result"]![0]!["id"]!.GetValue<string>());
                Assert.Equal((12L, "1"), (Outputs("raise")["Result"]![0]!["total"]!.GetValue<long>(), Outputs("raise")["Result"]![0]!["till"]!.GetValue<string>())); // its other fields kept
                var big = Outputs("big")["Result"]!.AsArray().Select(order => order!["id"]!.GetValue<string>()).OrderBy(id => id);
                Assert.Equal(new[] { "1042", "1043" }, big); // across both partitions, from the Query box's two lines
                Assert.True(Outputs("drop")["Result"]![0]!["deleted"]!.GetValue<bool>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(CosmosDbTestEndpoint.Key, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(CosmosDbTestEndpoint.Key, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
