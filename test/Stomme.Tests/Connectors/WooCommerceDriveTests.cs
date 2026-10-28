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
    /// BL-229 - WooCommerce DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for a store's REST API: a WooCommerce connection made over the API, its consumer secret
    /// vaulted, tested (the orders counted); a published pipeline adds an order with its line items, reads the processing orders
    /// over two pages, completes one and deletes a product for good - every call the REST API key's Basic pair; the secret is in
    /// neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class WooCommerceDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_adds_reads_completes_and_deletes()
        {
            var secret = "cs_" + Guid.NewGuid().ToString("N");
            using var store = new WooCommerceStandIn();
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
                    Name = "Web shop", ConnectorType = "woocommerce", Endpoint = $"http://127.0.0.1:{store.Port}/shop/wp-admin/", TimeoutSeconds = 10, Credentials = new() { ["username"] = "ck_drive", ["password"] = secret },
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
                    Steps =
                    {
                        Call("add", "Add an order", "create_order", "status = processing\ncustomer_id = 7\nset_paid = true\nline_items = [{\"product_id\": 93, \"quantity\": 2}]"),
                        Call("open", "Processing orders", "orders", "status = processing\nmax = 150"),
                        Call("complete", "Complete it", "update_order", "order = 1001\nstatus = completed"),
                        Call("drop", "Drop the old mug", "delete_product", "product = 93\nforce = true"),
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
                var received = store.Received;
                var basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ck_drive:" + secret));
                Assert.All(received, request => Assert.Equal(basic, request.Authorization));
                Assert.Equal("/shop/wp-json/wc/v3/orders?per_page=1", received[0].PathAndQuery); // the test, the pasted admin address cut back to the store's
                Assert.Equal("{\"status\":\"processing\",\"customer_id\":7,\"set_paid\":true,\"line_items\":[{\"product_id\":93,\"quantity\":2}]}", received.Single(request => request.Method == "POST").Body);
                Assert.Equal(new[] { "/shop/wp-json/wc/v3/orders?status=processing&per_page=100&page=1", "/shop/wp-json/wc/v3/orders?status=processing&per_page=50&page=2" },
                    received.Where(request => request.Method == "GET" && request.PathAndQuery.Contains("status=processing", StringComparison.Ordinal)).Select(request => request.PathAndQuery));
                Assert.Equal(("/shop/wp-json/wc/v3/orders/1001", "{\"status\":\"completed\"}"), received.Where(request => request.Method == "PUT").Select(request => (request.PathAndQuery, request.Body)).Single());
                Assert.Equal("/shop/wp-json/wc/v3/products/93?force=true", received.Single(request => request.Method == "DELETE").PathAndQuery);

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                var open = JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == "open")!;
                Assert.Contains("\"RecordCount\": 150", open["outputsJson"]!.GetValue<string>()); // both pages' orders, up to the most asked for
                Assert.DoesNotContain(secret, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for a store's REST API: the orders counted, an order added, the orders over pages, one completed, a product deleted.</summary>
        private sealed class WooCommerceStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Method, string PathAndQuery, string? Authorization, string Body)> _received = new();

            public WooCommerceStandIn()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Method, string PathAndQuery, string? Authorization, string Body)> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
                }
            }

            private static string Orders(int from, int count) =>
                "[" + string.Join(",", Enumerable.Range(from, count).Select(id => "{\"id\":" + id + ",\"number\":\"" + id + "\",\"status\":\"processing\",\"total\":\"249.00\",\"billing\":{\"email\":\"user" + id + "@example.com\"}}")) + "]";

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
                        _received.Add((method, raw, context.Request.Headers["Authorization"], body));
                    }

                    var (status, answer) = (method, path) switch
                    {
                        ("GET", "/shop/wp-json/wc/v3/orders") when raw.Contains("per_page=1&", StringComparison.Ordinal) || raw.EndsWith("per_page=1", StringComparison.Ordinal) => (200, Orders(1, 1)),
                        ("POST", "/shop/wp-json/wc/v3/orders") => (201, "{\"id\":1001,\"number\":\"1001\",\"status\":\"processing\",\"total\":\"398.00\"}"),
                        ("GET", "/shop/wp-json/wc/v3/orders") when raw.EndsWith("page=2", StringComparison.Ordinal) => (200, Orders(101, 50)),
                        ("GET", "/shop/wp-json/wc/v3/orders") => (200, Orders(1, 100)),
                        ("PUT", "/shop/wp-json/wc/v3/orders/1001") => (200, "{\"id\":1001,\"status\":\"completed\"}"),
                        ("DELETE", "/shop/wp-json/wc/v3/products/93") => (200, "{\"id\":93,\"name\":\"Old mug\"}"),
                        _ => (404, "{\"code\":\"rest_no_route\",\"message\":\"No route was found matching the URL and request method.\",\"data\":{\"status\":404}}"),
                    };
                    var bytes = Encoding.UTF8.GetBytes(answer);
                    context.Response.StatusCode = status;
                    context.Response.ContentType = "application/json";
                    context.Response.Headers["X-WP-Total"] = "250";
                    context.Response.Headers["X-WP-TotalPages"] = "3";
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
