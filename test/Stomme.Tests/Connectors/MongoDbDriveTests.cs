#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Infrastructure.Connectivity;
using Stomme.Models.Pipelines;
using Stomme.Modules.Connectors.MongoDb;
using Stomme.Tests.Pipelines;
using MongoDB.Driver;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// MongoDB DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a real server (<see cref="MongoDbTestEndpoint"/>): a MongoDB connection made over the API - an address without a
    /// password, the password vaulted - refused while loopback is not allowlisted, then tested (the server named); a published
    /// pipeline inserts two orders, marks one paid by its number, finds the paid ones, sums each customer's and deletes them;
    /// the password in neither the graph nor the run. Fails, never skips, without a server.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class MongoDbDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_keeps_orders_in_mongodb()
        {
            var password = MongoDbTestEndpoint.Password; // no server: a failure that says how to get one
            var collection = "drive_" + Guid.NewGuid().ToString("N")[..8];
            var endpoint = $"mongodb://{MongoDbTestEndpoint.Host}:{MongoDbTestEndpoint.Port}/kassa_test?authSource=admin";
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
                    Name = "Kafé orders", ConnectorType = "mongodb", Endpoint = endpoint, TimeoutSeconds = 15,
                    Credentials = new() { ["username"] = MongoDbTestEndpoint.User, ["password"] = password },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var refused = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test before loopback is allowed");
                Assert.False(JsonNode.Parse(refused.Body)!["success"]!.GetValue<bool>(), refused.Body); // loopback is a private host: refused until allowlisted

                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/Connections/egress", new ConnectionEgressRequest { AllowedHosts = MongoDbTestEndpoint.Host }, "the Host allows the server")).Status);
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kafé orders" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("add", "insert", $"collection = {collection}\ndocuments = [{{\"nummer\": 1042, \"kund\": \"Kafé Ada\", \"summa\": 99.5}}, {{\"nummer\": 1043, \"kund\": \"Kafé Bo\", \"summa\": 12}}]"),
                        Call("pay", "update", $"collection = {collection}\nkey = nummer\ndocument = {{\"nummer\": 1042, \"status\": \"paid\"}}"),
                        Call("paid", "find", $"collection = {collection}\nquery = {{\"status\": \"paid\"}}\nprojection = {{\"_id\": 0}}"),
                        Call("totals", "aggregate", $"collection = {collection}\npipeline = [{{\"$group\": {{\"_id\": \"$kund\", \"summa\": {{\"$sum\": \"$summa\"}}}}}}, {{\"$sort\": {{\"_id\": 1}}}}]"),
                        Call("clear", "delete", $"collection = {collection}\nquery = {{}}\nall = true"),
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
                Assert.Equal(2, Outputs("add")["RecordCount"]!.GetValue<int>());
                Assert.Equal(1, Outputs("pay")["Result"]![0]!["modified"]!.GetValue<int>());
                var paid = Outputs("paid")["Result"]![0]!;
                Assert.Equal((1042, "Kafé Ada", "paid"), (paid["nummer"]!.GetValue<int>(), paid["kund"]!.GetValue<string>(), paid["status"]!.GetValue<string>()));
                Assert.Equal(new[] { ("Kafé Ada", 99.5), ("Kafé Bo", 12.0) }, Outputs("totals")["Result"]!.AsArray().Select(total => (total!["_id"]!.GetValue<string>(), total["summa"]!.GetValue<double>())));
                Assert.Equal(2, Outputs("clear")["Result"]![0]!["deleted"]!.GetValue<int>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(password, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(password, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
                var settings = await MongoDbConnector.SettingsAsync(MongoUrl.Create(endpoint), new System.Collections.Generic.Dictionary<string, string> { ["username"] = MongoDbTestEndpoint.User, ["password"] = password },
                    new HostConnectorEgress(new[] { MongoDbTestEndpoint.Host }), TimeSpan.FromSeconds(15), CancellationToken.None);
                using var mongo = new MongoClient(settings);
                await mongo.GetDatabase("kassa_test").DropCollectionAsync(collection);
            }
        }
    }
}
