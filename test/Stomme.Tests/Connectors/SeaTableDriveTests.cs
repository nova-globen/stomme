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
    /// SeaTable DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against this machine's throwaway SeaTable 6.2 (scripts/seatable-test-server.ps1) - fail-not-skip without it: a
    /// SeaTable connection made over the API, its token vaulted, tested; a published pipeline making an order, finding it by a
    /// search for its name and changing its quantity - each an expression over the step before; the token in neither the graph
    /// nor the run. The test's tables are deleted afterwards.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class SeaTableDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_makes_an_order_finds_it_and_changes_it()
        {
            var token = SeaTableTestEndpoint.Token;
            var marker = Guid.NewGuid().ToString("N")[..8];
            var orders = "Orders" + marker;
            await using var tables = await SeaTableTestEndpoint.TablesAsync(orders, "Customers" + marker);
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

                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/Connections/egress", new ConnectionEgressRequest { AllowedHosts = new Uri(SeaTableTestEndpoint.Endpoint).Host }, "the Host allows SeaTable")).Status);
                var made = await Send(HttpMethod.Post, "/api/Connections", new ConnectionRequest
                {
                    Name = "Kafé bas", ConnectorType = "seatable", Endpoint = SeaTableTestEndpoint.Endpoint, TimeoutSeconds = 60,
                    Credentials = new() { ["api-key"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Beställ kaffe" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string operation, string variable) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation }, OutputVariables = { ["Result"] = variable } };
                var order = Call("order", "row_create", "v-order");
                order.Inputs["Parameters"] = $"table = {orders}";
                order.Inputs["Query"] = $"{{\"Name\": \"O'Brien {marker}\", \"Qty\": 2, \"Kind\": \"Kaffe\"}}";
                var find = Call("find", "rows_search", "v-found");
                find.InputExpressions["Parameters"] = $"table = {orders}\ncolumn = Name\nterm = {{{{ orders[0].Name }}}}\nexact = true";
                var change = Call("change", "row_update", "v-changed");
                change.InputExpressions["Parameters"] = $"table = {orders}\nrow = {{{{ found[0]._id }}}}\nfields = {{\"Qty\": 5}}";
                var graph = new PipelineGraph
                {
                    Variables =
                    {
                        new PipelineGraphVariable { Id = "v-order", Name = "orders" }, new PipelineGraphVariable { Id = "v-found", Name = "found" },
                        new PipelineGraphVariable { Id = "v-changed", Name = "changed" },
                    },
                    Steps = { order, find, change },
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
                var id = Outputs("order")["Result"]![0]!["_id"]!.GetValue<string>();
                Assert.Equal(id, Assert.Single(Outputs("find")["Result"]!.AsArray())!["_id"]!.GetValue<string>());
                Assert.Equal((id, 5d), (Outputs("change")["Result"]![0]!["_id"]!.GetValue<string>(), Outputs("change")["Result"]![0]!["Qty"]!.GetValue<double>()));
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
