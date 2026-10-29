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
    /// Elasticsearch DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction
    /// port, against a loopback stand-in for an Elasticsearch's REST API that refuses a call without the user's name and
    /// password: an Elasticsearch connection made over the API, its password vaulted, tested; a published pipeline creates an
    /// index, indexes a document with an accent, searches over two pages - no more than asked - gets, updates and counts, deletes
    /// the document and reads the indices; the password in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class ElasticsearchDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_creates_an_index_indexes_searches_gets_updates_counts_and_deletes_with_its_password()
        {
            var password = "es" + Guid.NewGuid().ToString("N");
            using var elastic = new ElasticStandIn("elastic:" + password);
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
                    Name = "Shop search", ConnectorType = "elasticsearch", Endpoint = $"http://127.0.0.1:{elastic.Port}/", TimeoutSeconds = 10,
                    Credentials = new() { ["username"] = "elastic", ["password"] = password },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Search" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("index", "Index", "create_index", "index = posts\nmappings = {\"properties\": {\"title\": {\"type\": \"text\"}}}"),
                        Call("create", "Create", "create", "index = posts\nfields = {\"title\": \"Café news\", \"views\": 3}\nrefresh = wait_for"),
                        Call("search", "Search", "search", "index = posts\nquery = {\"query\": {\"match\": {\"title\": \"café\"}}}\nsort = views:desc\nmax = 120"),
                        Call("get", "Get", "get", "index = posts\nid = Xy12"),
                        Call("update", "Update", "update", "index = posts\nid = Xy12\nfields = {\"views\": 4}"),
                        Call("count", "Count", "count", "index = posts"),
                        Call("delete", "Delete", "delete", "index = posts\nid = Xy12"),
                        Call("indices", "Indices", "indices", ""),
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

                Assert.Equal("Finished", run?.SubStatus);
                var received = elastic.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "no password: " + request.Line));
                Assert.Equal(new[]
                {
                    "GET /",
                    "PUT /posts {\"mappings\":{\"properties\":{\"title\":{\"type\":\"text\"}}}}",
                    "POST /posts/_doc?refresh=wait_for {\"title\":\"Café news\",\"views\":3}",
                    "POST /posts/_search?sort=views%3Adesc {\"query\":{\"match\":{\"title\":\"café\"}},\"from\":0,\"size\":100}",
                    "POST /posts/_search?sort=views%3Adesc {\"query\":{\"match\":{\"title\":\"café\"}},\"from\":100,\"size\":100}",
                    "GET /posts/_doc/Xy12",
                    "POST /posts/_update/Xy12 {\"doc\":{\"views\":4}}",
                    "POST /posts/_count {}",
                    "DELETE /posts/_doc/Xy12",
                    "GET /_cat/indices?format=json&bytes=b&s=index",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal("Xy12", JsonNode.Parse(Outputs("create"))!["Result"]![0]!["_id"]!.GetValue<string>());
                Assert.Equal(120, JsonNode.Parse(Outputs("search"))!["RecordCount"]!.GetValue<int>()); // both pages, no more than asked
                Assert.Equal("Café news", JsonNode.Parse(Outputs("get"))!["Result"]![0]!["title"]!.GetValue<string>());
                Assert.Equal(180, JsonNode.Parse(Outputs("count"))!["Result"]![0]!["count"]!.GetValue<long>());
                Assert.Equal("posts", JsonNode.Parse(Outputs("indices"))!["Result"]![0]!["index"]!.GetValue<string>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(password, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(password, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for an Elasticsearch's REST API that answers only a call carrying the user's name and password.</summary>
        private sealed class ElasticStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _basic;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public ElasticStandIn(string userAndPassword)
            {
                _basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(userAndPassword));
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
                    var authorized = context.Request.Headers["Authorization"] == _basic;
                    lock (_gate)
                    {
                        _received.Add(($"{method} {raw}{(body.Length == 0 ? "" : " " + body)}", authorized));
                    }

                    string Hits(int from, int count) => "{\"hits\":{\"total\":{\"value\":180,\"relation\":\"eq\"},\"hits\":[" + string.Join(",", Enumerable.Range(from, count).Select(i => "{\"_index\":\"posts\",\"_id\":\"p" + i + "\",\"_source\":{\"title\":\"Café " + i + "\",\"views\":" + i + "}}")) + "]}}";
                    var path = raw.Split('?')[0];
                    var (status, answer) = !authorized ? (401, "{\"error\":{\"type\":\"security_exception\",\"reason\":\"missing authentication credentials for REST request [" + path + "]\"},\"status\":401}")
                        : (method, path) switch
                        {
                            ("GET", "/") => (200, "{\"name\":\"node-1\",\"cluster_name\":\"shop\",\"version\":{\"number\":\"8.15.2\"}}"),
                            ("PUT", "/posts") => (200, "{\"acknowledged\":true,\"shards_acknowledged\":true,\"index\":\"posts\"}"),
                            ("POST", "/posts/_doc") => (201, "{\"_index\":\"posts\",\"_id\":\"Xy12\",\"_version\":1,\"result\":\"created\"}"),
                            ("POST", "/posts/_search") => (200, body.Contains("\"from\":100", StringComparison.Ordinal) ? Hits(101, 80) : Hits(1, 100)),
                            ("GET", "/posts/_doc/Xy12") => (200, "{\"_index\":\"posts\",\"_id\":\"Xy12\",\"_version\":1,\"found\":true,\"_source\":{\"title\":\"Café news\",\"views\":3}}"),
                            ("POST", "/posts/_update/Xy12") => (200, "{\"_index\":\"posts\",\"_id\":\"Xy12\",\"_version\":2,\"result\":\"updated\"}"),
                            ("POST", "/posts/_count") => (200, "{\"count\":180}"),
                            ("DELETE", "/posts/_doc/Xy12") => (200, "{\"_index\":\"posts\",\"_id\":\"Xy12\",\"_version\":3,\"result\":\"deleted\"}"),
                            ("GET", "/_cat/indices") => (200, "[{\"health\":\"green\",\"status\":\"open\",\"index\":\"posts\",\"pri\":\"1\",\"rep\":\"0\",\"docs.count\":\"180\",\"docs.deleted\":\"0\",\"store.size\":\"20480\"}]"),
                            _ => (404, "{\"error\":{\"type\":\"index_not_found_exception\",\"reason\":\"no such index\"},\"status\":404}"),
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
