#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
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
    /// BL-229 - Baserow DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for Baserow's REST API that refuses a call without the database token: a Baserow connection
    /// made over the API, its token vaulted, tested; a published pipeline creates a row by a field's name and one by its id (n8n's
    /// field_123, read by its name through the table's fields), reads the paid rows over two pages - filtered by the field's id,
    /// sorted by a name with a comma - Baserow's next page followed, no more than asked, updates two rows in Baserow's batch and deletes one; the token in
    /// neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class BaserowDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_creates_reads_updates_and_deletes_rows_with_its_token()
        {
            var token = "br" + Guid.NewGuid().ToString("N");
            using var baserow = new BaserowStandIn(token);
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
                    Name = "Kafé Baserow", ConnectorType = "baserow", Endpoint = $"http://127.0.0.1:{baserow.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["api-key"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kassa" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("add", "create_row", "table = 42\nfields = {\"Name\": \"Kafé Ada\", \"Total, SEK\": 99.5}"),
                        Call("add-by-id", "create_row", "table = 42\nfields = {\"field_101\": \"Kafé Bo\"}"),
                        Call("paid", "rows", "table = 42\nfilters = [{\"field\": \"Paid\", \"type\": \"boolean\", \"value\": \"1\"}]\norder_by = -\"Total, SEK\"\nmax = 5"),
                        Call("pay", "update_rows", "table = 42\nrows = [{\"id\": 7, \"Paid\": true}, {\"id\": 8, \"Paid\": true}]"),
                        Call("drop", "delete_row", "table = 42\nrow = 8"),
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
                var received = baserow.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "no token: " + request.Line));
                const string Query = "user_field_names=true&order_by=-%22Total%2C%20SEK%22&filter_type=AND&filter__field_105__boolean=1";
                Assert.Equal(new[]
                {
                    "GET /api/database/tables/all-tables/",
                    "POST /api/database/rows/table/42/?user_field_names=true {\"Name\":\"Kafé Ada\",\"Total, SEK\":99.5}",
                    "GET /api/database/fields/table/42/",
                    "POST /api/database/rows/table/42/?user_field_names=true {\"Name\":\"Kafé Bo\"}",
                    "GET /api/database/fields/table/42/",
                    $"GET /api/database/rows/table/42/?{Query}&size=5&page=1",
                    $"GET /api/database/rows/table/42/?{Query}&size=5&page=2",
                    "PATCH /api/database/rows/table/42/batch/?user_field_names=true {\"items\":[{\"id\":7,\"Paid\":true},{\"id\":8,\"Paid\":true}]}",
                    "DELETE /api/database/rows/table/42/8/",
                }, received.Select(request => request.Line));

                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal((7, "Kafé Ada"), (JsonNode.Parse(Outputs("add"))!["Result"]![0]!["id"]!.GetValue<int>(), JsonNode.Parse(Outputs("add"))!["Result"]![0]!["Name"]!.GetValue<string>()));
                Assert.Equal(5, JsonNode.Parse(Outputs("paid"))!["RecordCount"]!.GetValue<int>()); // both pages, no more than asked
                Assert.Equal(2, JsonNode.Parse(Outputs("pay"))!["RecordCount"]!.GetValue<int>());
                Assert.True(JsonNode.Parse(Outputs("drop"))!["Result"]![0]!["deleted"]!.GetValue<bool>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for Baserow's REST API that answers only a call carrying the database token.</summary>
        private sealed class BaserowStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _token;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public BaserowStandIn(string token)
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
                    var authorized = context.Request.Headers["Authorization"] == "Token " + _token;
                    lock (_gate)
                    {
                        _received.Add(($"{context.Request.HttpMethod} {raw}{(body.Length == 0 ? "" : " " + body)}", authorized));
                    }

                    string Rows(int from, int count) => "[" + string.Join(",", Enumerable.Range(from, count).Select(i => "{\"id\":" + i + ",\"order\":\"" + i + ".00000000000000000000\",\"Name\":\"Kafé " + i + "\",\"Total, SEK\":\"" + (1000 - i) + ".00\",\"Paid\":true}")) + "]";
                    var origin = $"http://127.0.0.1:{Port}";
                    var (status, answer) = !authorized ? (401, "{\"error\":\"ERROR_INVALID_TOKEN\",\"detail\":\"The provided token is invalid.\"}")
                        : (context.Request.HttpMethod, raw.Split('?')[0]) switch
                        {
                            ("GET", "/api/database/tables/all-tables/") => (200, "[{\"id\":42,\"name\":\"Kassa\",\"order\":1,\"database_id\":5}]"),
                            ("GET", "/api/database/fields/table/42/") => (200, "[{\"id\":101,\"name\":\"Name\",\"type\":\"text\",\"primary\":true},{\"id\":103,\"name\":\"Total, SEK\",\"type\":\"number\"},{\"id\":105,\"name\":\"Paid\",\"type\":\"boolean\"}]"),
                            ("POST", "/api/database/rows/table/42/") => (200, "{\"id\":" + (body.Contains("Ada", StringComparison.Ordinal) ? 7 : 8) + ",\"order\":\"1.00000000000000000000\"," + body.Trim('{', '}') + ",\"Paid\":false}"),
                            ("GET", "/api/database/rows/table/42/") when raw.Contains("&page=1", StringComparison.Ordinal) => (200, "{\"count\":300,\"next\":\"" + origin + "/api/database/rows/table/42/?page=2\",\"previous\":null,\"results\":" + Rows(1, 3) + "}"),
                            ("GET", "/api/database/rows/table/42/") when raw.Contains("&page=2", StringComparison.Ordinal) => (200, "{\"count\":300,\"next\":\"" + origin + "/api/database/rows/table/42/?page=3\",\"previous\":null,\"results\":" + Rows(4, 3) + "}"),
                            ("PATCH", "/api/database/rows/table/42/batch/") => (200, "{\"items\":[{\"id\":7,\"Paid\":true},{\"id\":8,\"Paid\":true}]}"),
                            ("DELETE", "/api/database/rows/table/42/8/") => (204, ""),
                            _ => (404, "{\"error\":\"ERROR_ROW_DOES_NOT_EXIST\",\"detail\":\"Not found.\"}"),
                        };
                    context.Response.StatusCode = status;
                    if (answer.Length > 0)
                    {
                        context.Response.ContentType = "application/json";
                        await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(answer));
                    }

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
