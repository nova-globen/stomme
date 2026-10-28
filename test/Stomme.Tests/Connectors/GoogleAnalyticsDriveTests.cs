#nullable enable
using System;
using System.Collections.Concurrent;
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
    /// BL-229 - Google Analytics DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction
    /// port, against a loopback stand-in for Google's token endpoint and the GA4 Data API that refuses a call without the token it
    /// issued: a Google Analytics connection made over the API with its property, the client's secret and refresh token vaulted,
    /// tested; a published pipeline listing the property's metrics and reading last calendar month's active users and sessions by
    /// country, the most sessions first, Sweden alone by a filter; the secret and the refresh token in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class GoogleAnalyticsDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private const string Metadata = "{\"dimensions\":[{\"apiName\":\"country\",\"uiName\":\"Country\"},{\"apiName\":\"date\",\"uiName\":\"Date\"}],\"metrics\":[{\"apiName\":\"activeUsers\",\"uiName\":\"Active users\",\"type\":\"TYPE_INTEGER\"},{\"apiName\":\"sessions\",\"uiName\":\"Sessions\",\"type\":\"TYPE_INTEGER\"}]}";

        /// <summary>A loopback stand-in for Google's token endpoint and the GA4 Data API: its token for one refresh grant, every call kept.</summary>
        private sealed class GoogleAnalyticsStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly string _form;

            public GoogleAnalyticsStandIn(string clientId, string secret, string refresh)
            {
                _form = $"grant_type=refresh_token&refresh_token={Uri.EscapeDataString(refresh)}&client_id={Uri.EscapeDataString(clientId)}&client_secret={Uri.EscapeDataString(secret)}";
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public ConcurrentQueue<(string Method, string Path, string Body)> Received { get; } = new();

            public void Dispose() => _listener.Close();

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

                    using var reader = new StreamReader(context.Request.InputStream);
                    var body = await reader.ReadToEndAsync();
                    var path = context.Request.RawUrl ?? "/";
                    var method = context.Request.HttpMethod;
                    Received.Enqueue((method, path, body));
                    var (status, answer) = (method, path) switch
                    {
                        ("POST", "/token") => body == _form ? (200, "{\"access_token\":\"analytics-drive-token\",\"expires_in\":3600}") : (400, "{\"error\":\"invalid_grant\"}"),
                        _ when context.Request.Headers["Authorization"] != "Bearer analytics-drive-token" => (401, "{\"error\":{\"code\":401,\"message\":\"Request had invalid authentication credentials.\",\"status\":\"UNAUTHENTICATED\"}}"),
                        ("GET", "/v1beta/properties/213025502/metadata") => (200, Metadata),
                        ("POST", "/v1beta/properties/213025502:runReport") => (200, Report(JsonNode.Parse(body)!)),
                        _ => (404, "{\"error\":{\"code\":404,\"message\":\"Not found.\",\"status\":\"NOT_FOUND\"}}"),
                    };
                    context.Response.StatusCode = status;
                    context.Response.ContentType = "application/json";
                    var bytes = Encoding.UTF8.GetBytes(answer);
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            }

            // Three countries' numbers, Sweden alone when the filter asks for it, the most sessions first.
            private static string Report(JsonNode request)
            {
                var rows = new[] { ("Sweden", 1200, 1800), ("Norway", 300, 420), ("Finland", 90, 100) }
                    .Where(row => request["dimensionFilter"]?["filter"]?["stringFilter"]?["value"]?.GetValue<string>() is not { } only || row.Item1 == only).ToList();
                return new JsonObject
                {
                    ["dimensionHeaders"] = new JsonArray(new JsonObject { ["name"] = "country" }),
                    ["metricHeaders"] = new JsonArray(new JsonObject { ["name"] = "activeUsers", ["type"] = "TYPE_INTEGER" }, new JsonObject { ["name"] = "sessions", ["type"] = "TYPE_INTEGER" }),
                    ["rows"] = new JsonArray(rows.Select(row => (JsonNode?)new JsonObject
                    {
                        ["dimensionValues"] = new JsonArray(new JsonObject { ["value"] = row.Item1 }),
                        ["metricValues"] = new JsonArray(new JsonObject { ["value"] = row.Item2.ToString(System.Globalization.CultureInfo.InvariantCulture) }, new JsonObject { ["value"] = row.Item3.ToString(System.Globalization.CultureInfo.InvariantCulture) }),
                    }).ToArray()),
                    ["rowCount"] = rows.Count,
                }.ToJsonString();
            }
        }

        [Fact]
        public async Task A_pipeline_reads_a_propertys_metrics_and_report_through_google_analytics()
        {
            using var google = new GoogleAnalyticsStandIn("kafe-client", "analytics-secret-drive", "analytics-refresh-drive");
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
                    Name = "Kafé besökare", ConnectorType = "googleanalytics", Endpoint = $"http://127.0.0.1:{google.Port}", TimeoutSeconds = 10,
                    Settings = new() { ["token_url"] = $"http://127.0.0.1:{google.Port}/token", ["property"] = "213025502" },
                    Credentials = new() { ["client-id"] = "kafe-client", ["client-secret"] = "analytics-secret-drive", ["refresh-token"] = "analytics-refresh-drive" },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kassans besökare" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("metrics", "metadata", "kind = metrics"),
                        Call("month", "report", "range = lastCalendarMonth\nmetrics = activeUsers, sessions\ndimensions = country\norder_by = -sessions"),
                        Call("sweden", "report", "start = 28daysAgo\nmetrics = activeUsers, sessions\ndimensions = country\ndimension_filter = {\"filter\": {\"fieldName\": \"country\", \"stringFilter\": {\"value\": \"Sweden\"}}}"),
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
                Assert.Equal(new[] { "activeUsers", "sessions" }, Outputs("metrics")["Result"]!.AsArray().Select(metric => metric!["apiName"]!.GetValue<string>()));
                var month = Outputs("month")["Result"]!.AsArray();
                Assert.Equal(new[] { "Sweden:1800", "Norway:420", "Finland:100" }, month.Select(row => row!["country"]!.GetValue<string>() + ":" + row["sessions"]!.GetValue<long>()));
                Assert.Equal(1200, month[0]!["activeUsers"]!.GetValue<long>());
                Assert.Equal("Sweden", Assert.Single(Outputs("sweden")["Result"]!.AsArray())!["country"]!.GetValue<string>());

                var reports = google.Received.Where(request => request.Path == "/v1beta/properties/213025502:runReport").Select(request => JsonNode.Parse(request.Body)!).ToList();
                Assert.Equal(2, reports.Count);
                var range = reports[0]["dateRanges"]![0]!;
                var (start, end) = (DateOnly.Parse(range["startDate"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture), DateOnly.Parse(range["endDate"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture));
                Assert.Equal((1, start.AddMonths(1).AddDays(-1)), (start.Day, end)); // a whole calendar month
                Assert.Equal("[{\"metric\":{\"metricName\":\"sessions\"},\"desc\":true}]", reports[0]["orderBys"]!.ToJsonString());
                Assert.Equal("[{\"startDate\":\"28daysAgo\",\"endDate\":\"today\"}]", reports[1]["dateRanges"]!.ToJsonString());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                foreach (var secret in new[] { "analytics-secret-drive", "analytics-refresh-drive" })
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
