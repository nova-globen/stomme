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
    /// MySQL DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a real MySQL 8.4 (<see cref="MySqlTestEndpoint"/>): a MySQL connection made over the API - a connection string
    /// that carries no credential, the password vaulted - refused while loopback is not allowlisted, then tested (the server
    /// named); a published pipeline makes a table, adds a row with its values bound from the step's Parameters, reads it back
    /// and drops the table; the password in neither the graph nor the run. Fails, never skips, without a server.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class MySqlDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_writes_and_reads_a_mysql_table_with_its_values_bound()
        {
            var password = MySqlTestEndpoint.Password; // no server: a failure that says how to get one
            var table = "kassa_" + Guid.NewGuid().ToString("N")[..8];
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
                    Name = "Kafé kassa", ConnectorType = "mysql", Endpoint = $"Server={MySqlTestEndpoint.Host};Port={MySqlTestEndpoint.Port};Database=kassa", TimeoutSeconds = 15,
                    Credentials = new() { ["username"] = MySqlTestEndpoint.User, ["password"] = password },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var refused = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test before loopback is allowed");
                Assert.False(JsonNode.Parse(refused.Body)!["success"]!.GetValue<bool>(), refused.Body); // loopback is a private host: refused until allowlisted

                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/Connections/egress", new ConnectionEgressRequest { AllowedHosts = MySqlTestEndpoint.Host }, "the Host allows the server")).Status);
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kafé kassa" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Query(string id, string sql, string? values = null)
                {
                    var step = new PipelineGraphStep { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = "query", ["Query"] = sql } };
                    if (values is not null)
                    {
                        step.Inputs["Parameters"] = values;
                    }

                    return step;
                }

                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Query("make", $"CREATE TABLE {table} (id INT AUTO_INCREMENT PRIMARY KEY, kund VARCHAR(60), summa DECIMAL(8,2))"),
                        Query("add", $"INSERT INTO {table} (kund, summa) VALUES (@kund, @summa)", "@kund = Kafé Ada'; DROP TABLE x; --\n@summa = 99.50"),
                        Query("read", $"SELECT id, kund, summa FROM {table} ORDER BY id LIMIT @n", "@n = 5"),
                        Query("drop", $"DROP TABLE {table}"),
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
                Assert.Equal((1, 1), (Outputs("add")["Result"]![0]!["affected_rows"]!.GetValue<int>(), Outputs("add")["Result"]![0]!["last_insert_id"]!.GetValue<int>())); // a write answers what it changed
                var row = Outputs("read")["Result"]![0]!;
                Assert.Equal((1, "Kafé Ada'; DROP TABLE x; --", 99.50m), (row["id"]!.GetValue<int>(), row["kund"]!.GetValue<string>(), row["summa"]!.GetValue<decimal>())); // the value the text it is
                Assert.Equal(1, Outputs("read")["RecordCount"]!.GetValue<int>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(password, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(password, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
                await Contexts.DrainAsync(new Stomme.Modules.Connectors.MySql.MySqlDatabaseConnector(), Contexts.Extract(new Stomme.Infrastructure.Connectivity.HostConnectorEgress(new[] { MySqlTestEndpoint.Host }), $"Server={MySqlTestEndpoint.Host};Port={MySqlTestEndpoint.Port};Database=kassa", "query",
                    new System.Collections.Generic.Dictionary<string, string> { ["query"] = $"DROP TABLE IF EXISTS {table}" }, credentials: new System.Collections.Generic.Dictionary<string, string> { ["username"] = MySqlTestEndpoint.User, ["password"] = password }, timeoutSeconds: 15));
            }
        }
    }
}
