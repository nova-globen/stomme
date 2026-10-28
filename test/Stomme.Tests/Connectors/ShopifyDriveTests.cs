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
    /// BL-229 - Shopify DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for a shop's GraphQL Admin API: a Shopify connection made over the API, its token vaulted,
    /// tested (the shop named); a published pipeline adds an order from a variant, finds the paid orders over two pages, adds a
    /// product and reads the active products - every query the token in Shopify's header; the token is in neither the graph nor
    /// the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class ShopifyDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_adds_an_order_finds_orders_adds_a_product_and_reads_products()
        {
            var token = "shpat_" + Guid.NewGuid().ToString("N");
            using var shopify = new ShopifyStandIn();
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
                    Name = "Shop", ConnectorType = "shopify", Endpoint = $"http://127.0.0.1:{shopify.Port}", TimeoutSeconds = 10, Credentials = new() { ["token"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Orders and products" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Variables = { new PipelineGraphVariable { Id = "v-paid", Name = "paid" } },
                    Steps =
                    {
                        Call("order", "Add an order", "create_order", "line_items = [{\"variant_id\": \"4372907\", \"quantity\": 2}]\nemail = ada@example.com\ntags = web"),
                        Call("paid", "Paid orders", "orders", "financial_status = paid"),
                        Call("product", "Add a product", "create_product", "title = Blue mug\nvendor = Acme\nstatus = draft"),
                        Call("products", "Active products", "products", "status = active"),
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
                var received = shopify.Received;
                Assert.All(received, request => Assert.Equal((token, "/admin/api/2026-10/graphql.json"), (request.Token, request.Path)));
                var order = received.Single(request => request.Query.Contains("orderCreate(", StringComparison.Ordinal));
                Assert.Equal("{\"lineItems\":[{\"variantId\":\"gid://shopify/ProductVariant/4372907\",\"quantity\":2}],\"email\":\"ada@example.com\",\"tags\":[\"web\"]}", order.Variables["order"]!.ToJsonString());
                var pages = received.Where(request => request.Query.Contains("orders(", StringComparison.Ordinal)).ToList();
                Assert.Equal(2, pages.Count); // both pages, by the cursor
                Assert.All(pages, page => Assert.Equal("financial_status:paid", (string?)page.Variables["query"]));
                Assert.Equal("p1", (string?)pages[1].Variables["after"]);
                Assert.Equal("{\"title\":\"Blue mug\",\"vendor\":\"Acme\",\"status\":\"DRAFT\"}", received.Single(request => request.Query.Contains("productCreate(", StringComparison.Ordinal)).Variables["product"]!.ToJsonString());
                Assert.Equal("status:active", (string?)received.Single(request => request.Query.Contains("products(", StringComparison.Ordinal)).Variables["query"]);

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.Contains("#1002", steps); // the second page's order
                Assert.Contains("Blue mug", steps);
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for a shop's GraphQL Admin API: the shop, an order added, the paid orders over two pages, a product added, the active products.</summary>
        private sealed class ShopifyStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Path, string? Token, string Query, JsonObject Variables)> _received = new();

            public ShopifyStandIn()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Path, string? Token, string Query, JsonObject Variables)> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
                }
            }

            private static string Order(string id) =>
                "{\"id\":\"gid://shopify/Order/" + id + "\",\"legacyResourceId\":\"" + id + "\",\"name\":\"#" + id + "\",\"email\":\"ada@example.com\",\"createdAt\":\"2026-10-02T10:00:00Z\","
                + "\"displayFinancialStatus\":\"PAID\",\"displayFulfillmentStatus\":\"UNFULFILLED\",\"currencyCode\":\"SEK\",\"totalPriceSet\":{\"shopMoney\":{\"amount\":\"240.0\"}},\"tags\":[\"web\"],\"test\":false,"
                + "\"lineItems\":{\"nodes\":[{\"title\":\"Blue mug\",\"quantity\":2,\"sku\":\"MUG-B\",\"variant\":{\"id\":\"gid://shopify/ProductVariant/4372907\"}}]}}";

            private static string Product(string id, string status) =>
                "{\"id\":\"gid://shopify/Product/" + id + "\",\"legacyResourceId\":\"" + id + "\",\"title\":\"Blue mug\",\"handle\":\"blue-mug\",\"status\":\"" + status + "\",\"vendor\":\"Acme\",\"tags\":[],"
                + "\"priceRangeV2\":{\"minVariantPrice\":{\"amount\":\"120.0\",\"currencyCode\":\"SEK\"},\"maxVariantPrice\":{\"amount\":\"120.0\"}},\"variants\":{\"nodes\":[]}}";

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

                    var json = JsonNode.Parse(body) as JsonObject ?? new JsonObject();
                    var query = (string?)json["query"] ?? "";
                    var variables = json["variables"] as JsonObject ?? new JsonObject();
                    lock (_gate)
                    {
                        _received.Add((context.Request.Url!.AbsolutePath, context.Request.Headers["X-Shopify-Access-Token"], query, variables));
                    }

                    var answer = query switch
                    {
                        _ when query.Contains("shop {", StringComparison.Ordinal) => "{\"data\":{\"shop\":{\"name\":\"Acme\",\"myshopifyDomain\":\"acme.myshopify.com\",\"currencyCode\":\"SEK\"}}}",
                        _ when query.Contains("orderCreate(", StringComparison.Ordinal) => "{\"data\":{\"orderCreate\":{\"order\":" + Order("1001") + ",\"userErrors\":[]}}}",
                        _ when query.Contains("orders(", StringComparison.Ordinal) && variables["after"] is null => "{\"data\":{\"orders\":{\"pageInfo\":{\"hasNextPage\":true,\"endCursor\":\"p1\"},\"nodes\":[" + Order("1001") + "]}}}",
                        _ when query.Contains("orders(", StringComparison.Ordinal) => "{\"data\":{\"orders\":{\"pageInfo\":{\"hasNextPage\":false,\"endCursor\":\"p2\"},\"nodes\":[" + Order("1002") + "]}}}",
                        _ when query.Contains("productCreate(", StringComparison.Ordinal) => "{\"data\":{\"productCreate\":{\"product\":" + Product("88", "DRAFT") + ",\"userErrors\":[]}}}",
                        _ when query.Contains("products(", StringComparison.Ordinal) => "{\"data\":{\"products\":{\"pageInfo\":{\"hasNextPage\":false,\"endCursor\":null},\"nodes\":[" + Product("87", "ACTIVE") + "]}}}",
                        _ => "{\"errors\":[{\"message\":\"Unknown query\"}]}",
                    };
                    var bytes = Encoding.UTF8.GetBytes(answer);
                    context.Response.StatusCode = 200;
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
