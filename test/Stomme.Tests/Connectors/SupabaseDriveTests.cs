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
    /// Supabase DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for a project's REST API: a Supabase connection made over the API, its secret key vaulted,
    /// tested (its tables counted); a published pipeline adds an order, reads the paid orders over two pages by two filters given
    /// as two 'filters =' lines, marks one shipped and deletes another - every call the key in Supabase's apikey header; the key is
    /// in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class SupabaseDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_adds_reads_updates_and_deletes_rows()
        {
            var key = "sb_secret_" + Guid.NewGuid().ToString("N");
            using var supabase = new SupabaseStandIn();
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
                    Name = "Orders project", ConnectorType = "supabase", Endpoint = $"http://127.0.0.1:{supabase.Port}", TimeoutSeconds = 10, Credentials = new() { ["token"] = key },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Orders" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Variables = { new PipelineGraphVariable { Id = "v-paid", Name = "paid" } },
                    Steps =
                    {
                        Call("add", "Add an order", "create_rows", "table = orders\nrows = {\"email\": \"ada@example.com\", \"total\": 120}"),
                        Call("paid", "Paid orders", "rows", "table = orders\nfilters = status eq paid\nfilters = total gte 100\norder = id.asc\nmax = 1500"),
                        Call("ship", "Ship it", "update_rows", "table = orders\nfilters = id eq 7\nfields = {\"status\": \"shipped\"}"),
                        Call("drop", "Drop a test order", "delete_rows", "table = orders\nfilters = email like %@test.example"),
                    },
                };
                graph.Steps[1].OutputVariables["Result"] = "v-paid";
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
                var received = supabase.Received;
                Assert.All(received, request => Assert.Equal((key, (string?)null), (request.ApiKey, request.Authorization)));
                Assert.Equal("{\"email\":\"ada@example.com\",\"total\":120}", received.Single(request => request.Method == "POST").Body);
                var pages = received.Where(request => request.Method == "GET" && request.PathAndQuery.StartsWith("/rest/v1/orders?", StringComparison.Ordinal)).ToList();
                Assert.Equal(2, pages.Count); // a full page, then the rest
                Assert.Equal("/rest/v1/orders?status=eq.paid&total=gte.100&order=id.asc&limit=1000&offset=0", pages[0].PathAndQuery); // both filters, from two lines
                Assert.Equal("/rest/v1/orders?status=eq.paid&total=gte.100&order=id.asc&limit=500&offset=1000", pages[1].PathAndQuery);
                Assert.Equal(("/rest/v1/orders?id=eq.7", "{\"status\":\"shipped\"}", "return=representation"), received.Where(request => request.Method == "PATCH").Select(request => (request.PathAndQuery, request.Body, request.Prefer)).Single());
                Assert.Equal("/rest/v1/orders?email=like.%25%40test.example", received.Single(request => request.Method == "DELETE").PathAndQuery);

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                var paid = JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == "paid")!;
                Assert.Contains("\"RecordCount\": 1500", paid["outputsJson"]!.GetValue<string>()); // both pages' rows - the view shows 32 KB of them
                Assert.DoesNotContain(key, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(key, (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for a project's REST API: its description, an order added, the orders over pages, one updated, one deleted.</summary>
        private sealed class SupabaseStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Method, string PathAndQuery, string? ApiKey, string? Authorization, string? Prefer, string Body)> _received = new();

            public SupabaseStandIn()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Method, string PathAndQuery, string? ApiKey, string? Authorization, string? Prefer, string Body)> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
                }
            }

            private static string Rows(int from, int count) =>
                "[" + string.Join(",", Enumerable.Range(from, count).Select(id => "{\"id\":" + id + ",\"email\":\"user" + id + "@example.com\",\"status\":\"paid\",\"total\":120}")) + "]";

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

                    var raw = context.Request.RawUrl ?? "/";
                    var path = raw.Split('?')[0];
                    var method = context.Request.HttpMethod;
                    lock (_gate)
                    {
                        _received.Add((method, raw, context.Request.Headers["apikey"], context.Request.Headers["Authorization"], context.Request.Headers["Prefer"], body));
                    }

                    var (status, answer) = (method, path) switch
                    {
                        ("GET", "/rest/v1/") => (200, "{\"swagger\":\"2.0\",\"paths\":{\"/\":{},\"/orders\":{},\"/customers\":{}}}"),
                        ("POST", "/rest/v1/orders") => (201, "[{\"id\":2001,\"email\":\"ada@example.com\",\"total\":120,\"status\":\"new\"}]"),
                        ("GET", "/rest/v1/orders") when raw.Contains("offset=1000", StringComparison.Ordinal) => (200, Rows(1001, 500)),
                        ("GET", "/rest/v1/orders") => (200, Rows(1, 1000)),
                        ("PATCH", "/rest/v1/orders") => (200, "[{\"id\":7,\"status\":\"shipped\"}]"),
                        ("DELETE", "/rest/v1/orders") => (200, "[{\"id\":9,\"email\":\"x@test.example\"}]"),
                        _ => (404, "{\"code\":\"PGRST205\",\"message\":\"Could not find the table\"}"),
                    };
                    var bytes = Encoding.UTF8.GetBytes(answer);
                    context.Response.StatusCode = status;
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
