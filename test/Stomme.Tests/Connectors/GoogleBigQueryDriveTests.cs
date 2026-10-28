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
    /// BL-229 - Google BigQuery DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the
    /// extraction port, against a loopback stand-in for Google's token endpoint and the BigQuery API: an access token for the
    /// OAuth client's refresh token alone, BigQuery answering that token alone. A Google BigQuery connection made over the API,
    /// its secret and refresh token vaulted, tested; a published pipeline runs a query from the step's Query box with a named
    /// parameter bound, waits for its job and reads its rows over two pages no further than asked, typed by the schema, and
    /// inserts two rows; neither the secret nor the refresh token in the graph or the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class GoogleBigQueryDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_queries_and_inserts_rows_with_the_clients_refresh_token()
        {
            var secret = "bq-" + Guid.NewGuid().ToString("N");
            var refresh = "1//bq-" + Guid.NewGuid().ToString("N");
            using var google = new BigQueryStandIn("client-kafe", secret, refresh);
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
                    Name = "Kafé warehouse", ConnectorType = "googlebigquery", Endpoint = $"http://127.0.0.1:{google.Port}/bigquery/v2", TimeoutSeconds = 10,
                    Settings = new() { ["project"] = "kafe-analytics", ["token_url"] = $"http://127.0.0.1:{google.Port}/token" },
                    Credentials = new() { ["client-id"] = "client-kafe", ["client-secret"] = secret, ["refresh-token"] = refresh },
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
                        new() { Id = "orders", Type = "Stomme.ExtractFromConnection", Name = "Orders", Inputs = { ["Connection"] = connection, ["Operation"] = "query", ["Query"] = "SELECT name, total, seen\nFROM sales.orders\nWHERE city = @city", ["Parameters"] = "@city = Malmö\nlocation = EU\nmax = 150" } },
                        new() { Id = "insert", Type = "Stomme.ExtractFromConnection", Name = "Insert", Inputs = { ["Connection"] = connection, ["Operation"] = "insert_rows", ["Parameters"] = "dataset = sales\ntable = orders\nrows = [{\"name\": \"Kafé AB\", \"total\": 129.5}, {\"name\": \"Fika & bröd\", \"total\": 42}]" } },
                    },
                };
                graph.Connections.Add(new PipelineGraphConnection { SourceId = "orders", SourcePort = "Done", TargetId = "insert" });

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
                var received = google.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "refused: " + request.Line));
                Assert.Equal("POST /token", received[0].Line); // the refresh token for an access token - the rest with it
                var calls = received.Skip(1).Where(request => request.Line != "POST /token").Select(request => request.Line).ToList();
                Assert.Equal("GET /bigquery/v2/projects/kafe-analytics/datasets?maxResults=1", calls[0]);
                Assert.Equal("POST /bigquery/v2/projects/kafe-analytics/queries {\"query\":\"SELECT name, total, seen\\nFROM sales.orders\\nWHERE city = @city\",\"useLegacySql\":false,\"maxResults\":150,\"timeoutMs\":10000,\"location\":\"EU\",\"parameterMode\":\"NAMED\",\"queryParameters\":[{\"name\":\"city\",\"parameterType\":{\"type\":\"STRING\"},\"parameterValue\":{\"value\":\"Malmö\"}}]}", calls[1]);
                Assert.Equal(new[]
                {
                    "GET /bigquery/v2/projects/kafe-analytics/queries/job_kafe?timeoutMs=10000&maxResults=150&location=EU",
                    "GET /bigquery/v2/projects/kafe-analytics/queries/job_kafe?timeoutMs=10000&maxResults=150&location=EU&pageToken=p1",
                }, calls.Skip(2).Take(2));
                var insert = JsonNode.Parse(calls[4]["POST /bigquery/v2/projects/kafe-analytics/datasets/sales/tables/orders/insertAll ".Length..])!;
                Assert.Equal(("[{\"name\":\"Kafé AB\",\"total\":129.5},{\"name\":\"Fika & bröd\",\"total\":42}]", 2), (new JsonArray(insert["rows"]!.AsArray().Select(row => row!["json"]!.DeepClone()).ToArray()).ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), insert["rows"]!.AsArray().Select(row => row!["insertId"]!.GetValue<string>()).Distinct().Count()));
                Assert.Equal(5, calls.Count);

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal(150, JsonNode.Parse(Outputs("orders"))!["RecordCount"]!.GetValue<int>()); // both pages, no further than asked
                var first = JsonNode.Parse(Outputs("orders"))!["Result"]![0]!;
                Assert.Equal(("Kafé 1", 129.5, "2024-10-14T10:00:00Z"), (first["name"]!.GetValue<string>(), first["total"]!.GetValue<double>(), first["seen"]!.GetValue<string>()));
                Assert.Equal(2, JsonNode.Parse(Outputs("insert"))!["Result"]![0]!["inserted"]!.GetValue<long>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                foreach (var kept in new[] { secret, refresh })
                {
                    Assert.DoesNotContain(kept, steps, StringComparison.Ordinal);
                    Assert.DoesNotContain(kept, graphBody, StringComparison.Ordinal);
                }
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for Google's token endpoint and the BigQuery API: an access token for the client's refresh token alone, the API for that token alone.</summary>
        private sealed class BigQueryStandIn : IDisposable
        {
            private const string Token = "ya29.bq-drive";
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _clientId;
            private readonly string _secret;
            private readonly string _refresh;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public BigQueryStandIn(string clientId, string secret, string refresh)
            {
                _clientId = clientId;
                _secret = secret;
                _refresh = refresh;
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
                    var method = context.Request.HttpMethod;
                    var path = raw.Split('?')[0];
                    var issuing = method == "POST" && path == "/token";
                    var form = issuing ? body.Split('&').Select(pair => pair.Split('=', 2)).ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "") : null;
                    var authorized = issuing
                        ? form!.GetValueOrDefault("grant_type") == "refresh_token" && form.GetValueOrDefault("refresh_token") == _refresh && form.GetValueOrDefault("client_id") == _clientId && form.GetValueOrDefault("client_secret") == _secret
                        : context.Request.Headers["Authorization"] == "Bearer " + Token;
                    lock (_gate)
                    {
                        // The token request's form carries the secret and the refresh token: logged by its line alone.
                        _received.Add(($"{method} {raw}{(issuing || body.Length == 0 ? "" : " " + body)}", authorized));
                    }

                    const string Schema = "\"schema\":{\"fields\":[{\"name\":\"name\",\"type\":\"STRING\"},{\"name\":\"total\",\"type\":\"FLOAT\"},{\"name\":\"seen\",\"type\":\"TIMESTAMP\"}]}";
                    static string Rows(int first, int count) => "[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"f\":[{\"v\":\"Kafé " + i + "\"},{\"v\":\"129.5\"},{\"v\":\"1.7289E9\"}]}")) + "]";
                    var (status, answer) = !authorized ? (401, issuing ? "{\"error\":\"invalid_grant\"}" : "{\"error\":{\"code\":401,\"status\":\"UNAUTHENTICATED\",\"message\":\"Request had invalid authentication credentials.\"}}")
                        : (method, path) switch
                        {
                            ("POST", "/token") => (200, "{\"access_token\":\"" + Token + "\",\"expires_in\":3599,\"token_type\":\"Bearer\"}"),
                            ("GET", "/bigquery/v2/projects/kafe-analytics/datasets") => (200, "{\"datasets\":[{\"datasetReference\":{\"datasetId\":\"sales\",\"projectId\":\"kafe-analytics\"}}]}"),
                            ("POST", "/bigquery/v2/projects/kafe-analytics/queries") => (200, "{\"jobReference\":{\"projectId\":\"kafe-analytics\",\"jobId\":\"job_kafe\",\"location\":\"EU\"},\"jobComplete\":false}"),
                            ("GET", "/bigquery/v2/projects/kafe-analytics/queries/job_kafe") => (200, raw.Contains("pageToken=p1", StringComparison.Ordinal)
                                ? "{\"jobComplete\":true," + Schema + ",\"rows\":" + Rows(101, 100) + "}"
                                : "{\"jobComplete\":true," + Schema + ",\"rows\":" + Rows(1, 100) + ",\"pageToken\":\"p1\"}"),
                            ("POST", "/bigquery/v2/projects/kafe-analytics/datasets/sales/tables/orders/insertAll") => (200, "{\"kind\":\"bigquery#tableDataInsertAllResponse\"}"),
                            _ => (404, "{\"error\":{\"code\":404,\"status\":\"NOT_FOUND\",\"message\":\"Not found.\"}}"),
                        };
                    context.Response.StatusCode = status;
                    var bytes = Encoding.UTF8.GetBytes(answer);
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
