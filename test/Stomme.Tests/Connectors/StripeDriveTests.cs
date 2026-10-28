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
    /// BL-229 - Stripe DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for Stripe's API: a Stripe connection made over the API, its key vaulted, tested (a test key
    /// said); a published pipeline adds a customer with metadata, finds the customers with that address over two pages, reads the
    /// first one's charges and the balance - each call the key as a bearer, each body a form; the key is in neither the graph nor
    /// the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class StripeDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_adds_a_customer_finds_customers_reads_charges_and_the_balance()
        {
            var key = "sk_test_" + Guid.NewGuid().ToString("N");
            using var stripe = new StripeStandIn();
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
                    Name = "Payments", ConnectorType = "stripe", Endpoint = $"http://127.0.0.1:{stripe.Port}/v1", TimeoutSeconds = 10, Credentials = new() { ["token"] = key },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Customer and payments" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Variables = { new PipelineGraphVariable { Id = "v-found", Name = "found" } },
                    Steps =
                    {
                        Call("add", "Add a customer", "create_customer", "email = ada@example.com\nname = Ada Lovelace\nmetadata = {\"order\": \"1001\"}"),
                        Call("find", "Find the customers", "customers", "email = ada@example.com"),
                        Call("charges", "Their charges", "charges", "customer = cus_A1"),
                        Call("balance", "The balance", "balance", ""),
                    },
                };
                graph.Steps[1].OutputVariables["Result"] = "v-found";
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
                var received = stripe.Received;
                Assert.All(received, request => Assert.Equal("Bearer " + key, request.Authorization));
                Assert.Equal("email=ada%40example.com&name=Ada+Lovelace&metadata%5Border%5D=1001", received.Single(request => request.Method == "POST" && request.Path == "/v1/customers").Body);
                Assert.Equal(2, received.Count(request => request.Method == "GET" && request.Path == "/v1/customers")); // both pages, by starting_after
                Assert.Contains(received, request => request.Path == "/v1/customers" && request.Query.Contains("starting_after=cus_A1", StringComparison.Ordinal));
                Assert.Contains(received, request => request.Path == "/v1/charges" && request.Query.Contains("customer=cus_A1", StringComparison.Ordinal));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.Contains("cus_A2", steps); // the second page's customer
                Assert.Contains("ch_1", steps);
                Assert.DoesNotContain(key, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(key, (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for Stripe's API: the balance, a customer added as cus_A1, the customers with an address over two pages, a customer's charges.</summary>
        private sealed class StripeStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Method, string Path, string Query, string? Authorization, string Body)> _received = new();

            public StripeStandIn()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Method, string Path, string Query, string? Authorization, string Body)> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
                }
            }

            private static string Customer(string id) =>
                "{\"id\":\"" + id + "\",\"object\":\"customer\",\"email\":\"ada@example.com\",\"name\":\"Ada Lovelace\",\"balance\":0,\"created\":1790848800,\"metadata\":{\"order\":\"1001\"}}";

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

                    var path = context.Request.Url!.AbsolutePath;
                    var query = context.Request.Url.Query;
                    var method = context.Request.HttpMethod;
                    lock (_gate)
                    {
                        _received.Add((method, path, query, context.Request.Headers["Authorization"], body));
                    }

                    var (status, answer) = (method, path) switch
                    {
                        ("GET", "/v1/balance") => (200, "{\"object\":\"balance\",\"livemode\":false,\"available\":[{\"amount\":12000,\"currency\":\"sek\"}],\"pending\":[]}"),
                        ("POST", "/v1/customers") => (200, Customer("cus_A1")),
                        ("GET", "/v1/customers") when query.Contains("starting_after=cus_A1", StringComparison.Ordinal) => (200, "{\"object\":\"list\",\"data\":[" + Customer("cus_A2") + "],\"has_more\":false}"),
                        ("GET", "/v1/customers") => (200, "{\"object\":\"list\",\"data\":[" + Customer("cus_A1") + "],\"has_more\":true}"),
                        ("GET", "/v1/charges") => (200, "{\"object\":\"list\",\"data\":[{\"id\":\"ch_1\",\"amount\":12000,\"currency\":\"sek\",\"status\":\"succeeded\",\"paid\":true,\"customer\":\"cus_A1\",\"created\":1790848800}],\"has_more\":false}"),
                        _ => (404, "{\"error\":{\"message\":\"Unrecognized request URL.\",\"type\":\"invalid_request_error\"}}"),
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
