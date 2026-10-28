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
    /// BL-229 - Google Cloud Firestore DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the
    /// extraction port, against Google's Firestore emulator (<see cref="FirestoreTestEndpoint"/>) and a loopback stand-in for
    /// Google's token endpoint: a Firestore connection made over the API - its project a setting, the client's secret and refresh
    /// token vaulted - refused while loopback is not allowlisted, then tested; a published pipeline makes two orders, raises one's
    /// total by its update mask with its other fields kept, queries till 2's with a query written over several lines in the
    /// step's Query box, and deletes them; the secret and the refresh token in neither the graph nor the run. Fails, never skips,
    /// without the emulator (D46).
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class GoogleFirestoreDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_keeps_orders_in_a_collection_and_queries_them()
        {
            using var tokens = new GoogleTokenStandIn("kafe-client", "kafe-secret-drive", "kafe-refresh-drive", FirestoreTestEndpoint.Owner);
            var collection = "orders" + Guid.NewGuid().ToString("N")[..8];
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
                    Name = "Kafé Firestore", ConnectorType = "googlefirestore", Endpoint = FirestoreTestEndpoint.Endpoint, TimeoutSeconds = 30,
                    Settings = new() { ["token_url"] = tokens.Url, ["project"] = "kafe-kassa" },
                    Credentials = new() { ["client-id"] = "kafe-client", ["client-secret"] = "kafe-secret-drive", ["refresh-token"] = "kafe-refresh-drive" },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var refused = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test before loopback is allowed");
                Assert.False(JsonNode.Parse(refused.Body)!["success"]!.GetValue<bool>(), refused.Body); // loopback is a private host: refused until allowlisted

                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/Connections/egress", new ConnectionEgressRequest { AllowedHosts = FirestoreTestEndpoint.Host }, "the Host allows the emulator")).Status);
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
                        Call("first", "document_create", $"collection = {collection}\nid = 1042\nfields = {{\"till\": \"2\", \"total\": 35.5, \"paid\": false}}"),
                        Call("second", "document_create", $"collection = {collection}\nid = 1043\nfields = {{\"till\": \"1\", \"total\": 8}}"),
                        Call("raise", "document_upsert", $"collection = {collection}\nid = 1042\nfields = {{\"total\": 40}}"),
                        Call("twos", "query", "", $"{{\"from\": [{{\"collectionId\": \"{collection}\"}}],\n \"where\": {{\"fieldFilter\": {{\"field\": {{\"fieldPath\": \"till\"}}, \"op\": \"EQUAL\", \"value\": {{\"stringValue\": \"2\"}}}}}}}}"),
                        Call("drop-first", "document_delete", $"collection = {collection}\nid = 1042"),
                        Call("drop-second", "document_delete", $"collection = {collection}\nid = 1043"),
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
                var raised = Outputs("raise")["Result"]![0]!;
                Assert.Equal((40L, "2", false), (raised["total"]!.GetValue<long>(), raised["till"]!.GetValue<string>(), raised["paid"]!.GetValue<bool>())); // its other fields kept
                var twos = Outputs("twos")["Result"]!.AsArray();
                Assert.Equal("1042", Assert.Single(twos)!["id"]!.GetValue<string>()); // from the Query box's two lines
                Assert.True(Outputs("drop-second")["Result"]![0]!["deleted"]!.GetValue<bool>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                foreach (var secret in new[] { "kafe-secret-drive", "kafe-refresh-drive" })
                {
                    Assert.DoesNotContain(secret, steps, StringComparison.Ordinal);
                    Assert.DoesNotContain(secret, graphBody, StringComparison.Ordinal);
                }
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
