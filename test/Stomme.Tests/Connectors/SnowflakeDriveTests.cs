#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
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
    /// Snowflake DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for an account's SQL API that answers one programmatic access token alone. A Snowflake
    /// connection made over the API, its token vaulted, tested; a published pipeline runs a query with a value bound - still
    /// running when first asked, its result in two parts, the second gzipped - no further than asked, inserts two rows and
    /// updates one by its key; the token in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class SnowflakeDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_queries_inserts_and_updates_with_the_accounts_token()
        {
            var token = "pat-" + Guid.NewGuid().ToString("N");
            using var account = new SnowflakeStandIn(token);
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

                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/Connections/egress", new ConnectionEgressRequest { AllowedHosts = "127.0.0.1" }, "the Host allows loopback")).Status);
                var made = await Send(HttpMethod.Post, "/api/Connections", new ConnectionRequest
                {
                    Name = "Kafé warehouse", ConnectorType = "snowflake", Endpoint = $"http://127.0.0.1:{account.Port}", TimeoutSeconds = 10,
                    Settings = new() { ["warehouse"] = "KASSA_WH", ["database"] = "SALES", ["schema"] = "PUBLIC" },
                    Credentials = new() { ["token"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kafé warehouse" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        new() { Id = "orders", Type = "Stomme.ExtractFromConnection", Name = "Orders", Inputs = { ["Connection"] = connection, ["Operation"] = "query", ["Query"] = "SELECT ID, NAME, TOTAL, DAY\nFROM ORDERS\nWHERE CITY = @city", ["Parameters"] = "@city = Malmö\nmax = 150" } },
                        new() { Id = "insert", Type = "Stomme.ExtractFromConnection", Name = "Insert", Inputs = { ["Connection"] = connection, ["Operation"] = "insert_rows", ["Parameters"] = "table = ORDERS\nrows = [{\"NAME\": \"Kafé AB\", \"TOTAL\": 129.5}, {\"NAME\": \"Fika & bröd\", \"TOTAL\": 42}]" } },
                        new() { Id = "update", Type = "Stomme.ExtractFromConnection", Name = "Update", Inputs = { ["Connection"] = connection, ["Operation"] = "update_rows", ["Parameters"] = "table = ORDERS\nrows = {\"ID\": 8, \"STATUS\": \"PAID\"}" } },
                    },
                };
                graph.Connections.Add(new PipelineGraphConnection { SourceId = "orders", SourcePort = "Done", TargetId = "insert" });
                graph.Connections.Add(new PipelineGraphConnection { SourceId = "insert", SourcePort = "Done", TargetId = "update" });

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

                Assert.Equal("Finished", run?.SubStatus);
                var received = account.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "refused: " + request.Line));
                Assert.Equal(new[]
                {
                    "POST SELECT CURRENT_USER(), CURRENT_ROLE(), CURRENT_WAREHOUSE() KASSA_WH/SALES/PUBLIC",
                    "POST SELECT ID, NAME, TOTAL, DAY\nFROM ORDERS\nWHERE CITY = ? KASSA_WH/SALES/PUBLIC {\"1\":{\"type\":\"TEXT\",\"value\":\"Malmö\"}}",
                    "GET /api/v2/statements/01b0-0001",
                    "GET /api/v2/statements/01b0-0001?partition=1",
                    "POST INSERT INTO ORDERS (NAME, TOTAL) SELECT $1, $2 FROM VALUES (?, ?), (?, ?) KASSA_WH/SALES/PUBLIC {\"1\":{\"type\":\"TEXT\",\"value\":\"Kafé AB\"},\"2\":{\"type\":\"REAL\",\"value\":\"129.5\"},\"3\":{\"type\":\"TEXT\",\"value\":\"Fika & bröd\"},\"4\":{\"type\":\"FIXED\",\"value\":\"42\"}}",
                    "POST UPDATE ORDERS SET STATUS = ? WHERE ID = ? KASSA_WH/SALES/PUBLIC {\"1\":{\"type\":\"TEXT\",\"value\":\"PAID\"},\"2\":{\"type\":\"FIXED\",\"value\":\"8\"}}",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                var orders = JsonNode.Parse(Outputs("orders"))!;
                Assert.Equal(150, orders["RecordCount"]!.GetValue<int>()); // both parts, no further than asked
                Assert.Equal((1L, "Order 1", "129.50", "2026-10-14"), (orders["Result"]![0]!["ID"]!.GetValue<long>(), orders["Result"]![0]!["NAME"]!.GetValue<string>(), orders["Result"]![0]!["TOTAL"]!.GetValue<string>(), orders["Result"]![0]!["DAY"]!.GetValue<string>()));
                Assert.Equal(2L, JsonNode.Parse(Outputs("insert"))!["Result"]![0]!["inserted"]!.GetValue<long>());
                Assert.Equal(1L, JsonNode.Parse(Outputs("update"))!["Result"]![0]!["updated"]!.GetValue<long>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for an account's SQL API: one programmatic access token let in, a query still running when first asked and its result in two parts, the second gzipped.</summary>
        private sealed class SnowflakeStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _token;
            private readonly List<(string Line, bool Authorized)> _received = new();
            private bool _asked;

            public SnowflakeStandIn(string token)
            {
                _token = token;
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Line, bool Authorized)> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
                }
            }

            private static string Rows(int first, int count) =>
                "[" + string.Join(",", Enumerable.Range(first, count).Select(i => "[\"" + i + "\",\"Order " + i + "\",\"129.50\",\"20740\"]")) + "]";

            private const string RowType = "[{\"name\":\"ID\",\"type\":\"fixed\",\"scale\":0},{\"name\":\"NAME\",\"type\":\"text\"},{\"name\":\"TOTAL\",\"type\":\"fixed\",\"scale\":2},{\"name\":\"DAY\",\"type\":\"date\"}]";

            private async Task ServeAsync()
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                    {
                        return;
                    }

                    string body;
                    using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                    {
                        body = await reader.ReadToEndAsync();
                    }

                    var raw = context.Request.RawUrl ?? "";
                    var method = context.Request.HttpMethod;
                    var authorized = context.Request.Headers["Authorization"] == "Bearer " + _token && context.Request.Headers["X-Snowflake-Authorization-Token-Type"] == "PROGRAMMATIC_ACCESS_TOKEN";
                    var sent = method == "POST" && body.Length > 0 ? JsonNode.Parse(body) : null;
                    var statement = sent?["statement"]?.GetValue<string>() ?? "";
                    lock (_gate)
                    {
                        _received.Add((method == "POST"
                            ? $"POST {statement} {sent?["warehouse"]}/{sent?["database"]}/{sent?["schema"]}{(sent?["bindings"] is { } bindings ? " " + bindings.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) : "")}"
                            : $"GET {raw}", authorized));
                    }

                    string Result(string rowType, string data, int partitions) =>
                        "{\"resultSetMetaData\":{\"format\":\"jsonv2\",\"rowType\":" + rowType + ",\"partitionInfo\":[" + string.Join(",", Enumerable.Repeat("{\"rowCount\":100}", partitions)) + "]},\"data\":" + data + ",\"statementHandle\":\"01b0-0001\"}";
                    var zipped = false;
                    var (status, answer) = !authorized ? (401, "{\"code\":\"394400\",\"message\":\"Programmatic access token is invalid.\"}")
                        : method == "POST" && statement.StartsWith("SELECT CURRENT_USER()", StringComparison.Ordinal) ? (200, Result("[]", "[[\"KAFE_LOADER\",\"LOADER\",\"KASSA_WH\"]]", 1))
                        : method == "POST" && statement.StartsWith("SELECT ID", StringComparison.Ordinal) ? (202, "{\"code\":\"333334\",\"message\":\"Asynchronous execution in progress.\",\"statementHandle\":\"01b0-0001\",\"statementStatusUrl\":\"/api/v2/statements/01b0-0001\"}")
                        : method == "GET" && raw == "/api/v2/statements/01b0-0001" ? (200, Result(RowType, Rows(1, 100), 2))
                        : method == "GET" && raw == "/api/v2/statements/01b0-0001?partition=1" ? (200, "{\"data\":" + Rows(101, 100) + "}")
                        : method == "POST" && statement.StartsWith("INSERT", StringComparison.Ordinal) ? (200, Result("[{\"name\":\"number of rows inserted\",\"type\":\"fixed\",\"scale\":0}]", "[[\"2\"]]", 1))
                        : method == "POST" && statement.StartsWith("UPDATE", StringComparison.Ordinal) ? (200, Result("[{\"name\":\"number of rows updated\",\"type\":\"fixed\",\"scale\":0}]", "[[\"1\",\"0\"]]", 1))
                        : (404, "{\"code\":\"390142\",\"message\":\"Unknown statement.\"}");
                    zipped = raw.EndsWith("?partition=1", StringComparison.Ordinal);
                    context.Response.StatusCode = status;
                    var bytes = Encoding.UTF8.GetBytes(answer);
                    if (zipped)
                    {
                        using var buffer = new MemoryStream();
                        using (var zip = new GZipStream(buffer, CompressionMode.Compress, leaveOpen: true))
                        {
                            zip.Write(bytes);
                        }

                        bytes = buffer.ToArray();
                        context.Response.AddHeader("Content-Encoding", "gzip");
                    }

                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            }

            public void Dispose()
            {
                _listener.Stop();
                _listener.Close();
            }
        }
    }
}
