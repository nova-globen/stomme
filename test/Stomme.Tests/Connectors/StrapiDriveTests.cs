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
    /// BL-229 - Strapi DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for a Strapi 5's REST API that refuses a call without the token: a Strapi connection made over
    /// the API, its token vaulted, tested; a published pipeline creates a draft with an accent, reads the drafts filtered over two
    /// pages - no more than asked - publishes the entry, gets it, deletes it and reads the homepage; the token in neither the graph
    /// nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class StrapiDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_creates_reads_publishes_gets_and_deletes_entries_with_its_token()
        {
            var token = "st" + Guid.NewGuid().ToString("N");
            using var strapi = new StrapiStandIn(token);
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
                    Name = "Shop CMS content", ConnectorType = "strapi", Endpoint = $"http://127.0.0.1:{strapi.Port}/admin", TimeoutSeconds = 10,
                    Credentials = new() { ["token"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Content" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("create", "New draft", "create_entry", "content_type = articles\nfields = {\"title\": \"Café news\", \"author\": 3}\nstatus = draft"),
                        Call("read", "Drafts", "entries", "content_type = articles\nfilters = {\"title\": {\"$containsi\": \"café\"}}\nstatus = draft\nmax = 120"),
                        Call("publish", "Publish", "update_entry", "content_type = articles\nid = abc123\nfields = {\"title\": \"Café news!\"}"),
                        Call("get", "Get", "get_entry", "content_type = articles\nid = abc123\npopulate = author"),
                        Call("delete", "Drop", "delete_entry", "content_type = articles\nid = abc123"),
                        Call("home", "Homepage", "single", "content_type = homepage\nlocale = sv"),
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
                var received = strapi.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "no token: " + request.Line));
                Assert.Equal(new[]
                {
                    "GET /api/i18n/locales",
                    "POST /api/articles?status=draft {\"data\":{\"title\":\"Café news\",\"author\":3}}",
                    "GET /api/articles?filters[title][$containsi]=caf%C3%A9&status=draft&pagination[page]=1&pagination[pageSize]=100",
                    "GET /api/articles?filters[title][$containsi]=caf%C3%A9&status=draft&pagination[page]=2&pagination[pageSize]=100",
                    "PUT /api/articles/abc123 {\"data\":{\"title\":\"Café news!\"}}",
                    "GET /api/articles/abc123?populate=author",
                    "DELETE /api/articles/abc123",
                    "GET /api/homepage?locale=sv",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal("abc123", JsonNode.Parse(Outputs("create"))!["Result"]![0]!["documentId"]!.GetValue<string>());
                Assert.Equal(120, JsonNode.Parse(Outputs("read"))!["RecordCount"]!.GetValue<int>()); // both pages, no more than asked
                Assert.Equal("Välkommen", JsonNode.Parse(Outputs("home"))!["Result"]![0]!["title"]!.GetValue<string>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for a Strapi 5's REST API that answers only a call carrying the token.</summary>
        private sealed class StrapiStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _token;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public StrapiStandIn(string token)
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
                    var authorized = context.Request.Headers["Authorization"] == "Bearer " + _token;
                    lock (_gate)
                    {
                        _received.Add(($"{context.Request.HttpMethod} {raw}{(body.Length == 0 ? "" : " " + body)}", authorized));
                    }

                    string Articles(int from, int count) => "[" + string.Join(",", Enumerable.Range(from, count).Select(i => "{\"id\":" + i + ",\"documentId\":\"doc" + i + "\",\"title\":\"Café " + i + "\"}")) + "]";
                    const string Entry = "{\"data\":{\"id\":42,\"documentId\":\"abc123\",\"title\":\"Café news\",\"author\":3},\"meta\":{}}";
                    var (status, answer) = !authorized ? (401, "{\"data\":null,\"error\":{\"status\":401,\"name\":\"UnauthorizedError\",\"message\":\"Missing or invalid credentials\"}}")
                        : (context.Request.HttpMethod, raw) switch
                        {
                            ("GET", "/api/i18n/locales") => (200, "[{\"id\":1,\"code\":\"sv\"},{\"id\":2,\"code\":\"en\"}]"),
                            ("GET", "/api/homepage?locale=sv") => (200, "{\"data\":{\"id\":1,\"documentId\":\"home1\",\"title\":\"Välkommen\"},\"meta\":{}}"),
                            ("GET", _) when raw.Contains("pagination[page]=2", StringComparison.Ordinal) => (200, "{\"data\":" + Articles(101, 80) + ",\"meta\":{\"pagination\":{\"page\":2,\"pageSize\":100,\"pageCount\":2,\"total\":180}}}"),
                            ("GET", _) when raw.Contains("pagination[page]=1", StringComparison.Ordinal) => (200, "{\"data\":" + Articles(1, 100) + ",\"meta\":{\"pagination\":{\"page\":1,\"pageSize\":100,\"pageCount\":2,\"total\":180}}}"),
                            ("GET", _) or ("POST", _) or ("PUT", _) => (200, Entry),
                            ("DELETE", _) => (204, ""),
                            _ => (404, "{\"data\":null,\"error\":{\"status\":404,\"name\":\"NotFoundError\",\"message\":\"Not Found\"}}"),
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
