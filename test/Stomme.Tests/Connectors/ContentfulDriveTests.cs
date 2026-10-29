#nullable enable
using System;
using System.Collections.Generic;
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
    /// Contentful DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction
    /// port, against a loopback stand-in for Contentful's Delivery API that refuses a call without the token: a Contentful
    /// connection made over the API with its space and environment, its token vaulted, tested; a published pipeline reads a
    /// content type's entries searched with an accent over two pages - no more than asked - gets an entry, an asset and the
    /// content type, and reads the locales; the token in neither the graph nor the run, nor in any address it called.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class ContentfulDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_reads_entries_an_entry_an_asset_a_content_type_and_the_locales_with_its_token()
        {
            var token = "cf" + Guid.NewGuid().ToString("N");
            using var contentful = new ContentfulStandIn(token);
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
                    Name = "Shop content", ConnectorType = "contentful", Endpoint = $"http://127.0.0.1:{contentful.Port}", TimeoutSeconds = 10,
                    Settings = new() { ["space"] = "shop1", ["environment"] = "release-1" },
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
                        Call("read", "Posts", "entries", "content_type = blogPost\nfilters = {\"fields.title[match]\": \"café\"}\norder = -sys.createdAt\nlocale = sv-SE\nmax = 120"),
                        Call("entry", "Entry", "entry", "id = e7"),
                        Call("asset", "Logo", "asset", "id = logo1"),
                        Call("type", "Type", "content_type", "id = blogPost"),
                        Call("locales", "Locales", "locales", ""),
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
                var received = contentful.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "no token: " + request.Line));
                const string At = "/spaces/shop1/environments/release-1";
                Assert.Equal(new[]
                {
                    "/spaces/shop1",
                    $"{At}/locales",
                    $"{At}/entries?content_type=blogPost&fields.title[match]=caf%C3%A9&order=-sys.createdAt&locale=sv-SE&skip=0&limit=100",
                    $"{At}/entries?content_type=blogPost&fields.title[match]=caf%C3%A9&order=-sys.createdAt&locale=sv-SE&skip=100&limit=100",
                    $"{At}/entries/e7",
                    $"{At}/assets/logo1",
                    $"{At}/content_types/blogPost",
                    $"{At}/locales?skip=0&limit=100",
                }, received.Select(request => request.Line));
                Assert.DoesNotContain(received, request => request.Line.Contains(token, StringComparison.Ordinal));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal(120, JsonNode.Parse(Outputs("read"))!["RecordCount"]!.GetValue<int>()); // both pages, no more than asked
                Assert.Equal("Café 7", JsonNode.Parse(Outputs("entry"))!["Result"]![0]!["title"]!.GetValue<string>());
                Assert.Equal("Logotyp", JsonNode.Parse(Outputs("asset"))!["Result"]![0]!["title"]!.GetValue<string>());
                Assert.Equal("Blogginlägg", JsonNode.Parse(Outputs("type"))!["Result"]![0]!["name"]!.GetValue<string>());
                Assert.Equal(2, JsonNode.Parse(Outputs("locales"))!["RecordCount"]!.GetValue<int>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for Contentful's Delivery API that answers only a call carrying the token.</summary>
        private sealed class ContentfulStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _token;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public ContentfulStandIn(string token)
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

                    var raw = context.Request.RawUrl ?? "";
                    var authorized = context.Request.Headers["Authorization"] == "Bearer " + _token && context.Request.HttpMethod == "GET";
                    lock (_gate)
                    {
                        _received.Add((raw, authorized));
                    }

                    static string Entry(int i) => "{\"sys\":{\"id\":\"e" + i + "\",\"type\":\"Entry\",\"contentType\":{\"sys\":{\"id\":\"blogPost\"}}},\"fields\":{\"title\":\"Café " + i + "\"}}";
                    string Page(int from, int count) => "{\"total\":180,\"skip\":" + (from - 1) + ",\"limit\":100,\"items\":[" + string.Join(",", Enumerable.Range(from, count).Select(Entry)) + "]}";
                    const string At = "/spaces/shop1/environments/release-1";
                    var path = raw.Split('?')[0];
                    var (status, answer) = !authorized ? (401, "{\"sys\":{\"type\":\"Error\",\"id\":\"AccessTokenInvalid\"},\"message\":\"The access token you sent could not be found or is invalid.\"}")
                        : path switch
                        {
                            "/spaces/shop1" => (200, "{\"sys\":{\"type\":\"Space\",\"id\":\"shop1\"},\"name\":\"Kafé\",\"locales\":[{\"code\":\"sv-SE\",\"default\":true}]}"),
                            At + "/locales" => (200, "{\"total\":2,\"items\":[{\"code\":\"sv-SE\",\"name\":\"Svenska\",\"default\":true},{\"code\":\"en-US\",\"name\":\"English\",\"default\":false,\"fallbackCode\":\"sv-SE\"}]}"),
                            At + "/entries" => (200, raw.Contains("skip=100", StringComparison.Ordinal) ? Page(101, 80) : Page(1, 100)),
                            At + "/entries/e7" => (200, Entry(7)),
                            At + "/assets/logo1" => (200, "{\"sys\":{\"id\":\"logo1\",\"type\":\"Asset\"},\"fields\":{\"title\":\"Logotyp\",\"file\":{\"url\":\"//images.ctfassets.net/logo.png\"}}}"),
                            At + "/content_types/blogPost" => (200, "{\"sys\":{\"id\":\"blogPost\"},\"name\":\"Blogginlägg\",\"displayField\":\"title\",\"fields\":[]}"),
                            _ => (404, "{\"sys\":{\"type\":\"Error\",\"id\":\"NotFound\"},\"message\":\"The resource could not be found.\"}"),
                        };
                    context.Response.StatusCode = status;
                    var bytes = Encoding.UTF8.GetBytes(answer);
                    context.Response.ContentType = "application/vnd.contentful.delivery.v1+json";
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
