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
    /// BL-229 - Salesforce DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for an org's token endpoint and REST API: a Salesforce connection made over the API, its consumer
    /// secret vaulted, tested (the org named); a published pipeline adds a lead, upserts one by an external id, finds the leads by
    /// SOQL over two pages, reads one with the fields named and deletes the upserted one - each call the access token the client
    /// credentials earned; the secret and the token are in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class SalesforceDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Api = "/services/data/v62.0";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_adds_upserts_finds_reads_and_deletes_leads()
        {
            var secret = "secret-" + Guid.NewGuid().ToString("N");
            using var salesforce = new SalesforceStandIn();
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
                    Name = "Sales", ConnectorType = "salesforce", Endpoint = $"http://127.0.0.1:{salesforce.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["client-id"] = "consumer-1", ["client-secret"] = secret, ["token-url"] = $"http://127.0.0.1:{salesforce.Port}/services/oauth2/token" },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);
                Assert.Contains(salesforce.Received, request => request.Method == "GET" && request.Path == Api + "/limits");

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Leads" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Variables = { new PipelineGraphVariable { Id = "v-found", Name = "found" } },
                    Steps =
                    {
                        Call("add", "Add a lead", "create", "object = Lead\nfields = {\"LastName\": \"Lovelace\", \"Company\": \"Acme\"}"),
                        Call("upsert", "Add or change a lead", "upsert", "object = Lead\nexternal_id_field = Email__c\nexternal_id = grace@example.com\nfields = {\"LastName\": \"Hopper\", \"Company\": \"Navy\"}"),
                        Call("find", "Find the leads", "query", "soql = SELECT Id, Name, Owner.Alias FROM Lead WHERE Company != null"),
                        Call("get", "Read a lead", "get", "object = Lead\nid = 00Q000000000001AAA\nfields = Name, Company"),
                        Call("delete", "Remove a lead", "delete", "object = Lead\nid = 00Q000000000002AAA"),
                    },
                };
                graph.Steps[2].OutputVariables["Result"] = "v-found";
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
                var received = salesforce.Received;
                var calls = received.Where(request => request.Path.StartsWith(Api, StringComparison.Ordinal)).ToList();
                Assert.All(calls, request => Assert.Equal("Bearer " + SalesforceStandIn.AccessToken, request.Authorization));
                Assert.All(received.Where(request => request.Path == "/services/oauth2/token"), request => Assert.Contains("grant_type=client_credentials", request.Body, StringComparison.Ordinal));
                Assert.Equal("{\"LastName\":\"Lovelace\",\"Company\":\"Acme\"}", calls.Single(request => request.Method == "POST" && request.Path == Api + "/sobjects/Lead").Body);
                Assert.Contains(calls, request => request.Method == "PATCH" && request.Path == Api + "/sobjects/Lead/Email__c/grace%40example.com");
                Assert.Contains(calls, request => request.Method == "GET" && request.Path == Api + "/query/01gNEXT-2000"); // the second page
                Assert.Contains(calls, request => request.Method == "GET" && request.Path == Api + "/sobjects/Lead/00Q000000000001AAA" && request.Query == "?fields=Name,Company");
                Assert.Contains(calls, request => request.Method == "DELETE" && request.Path == Api + "/sobjects/Lead/00Q000000000002AAA");

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.Contains("Grace Hopper", steps); // the second page's lead
                Assert.Contains("Owner.Alias", steps); // a related record flattened
                Assert.DoesNotContain(secret, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(SalesforceStandIn.AccessToken, steps, StringComparison.Ordinal);
                var stored = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(secret, stored, StringComparison.Ordinal);
                Assert.DoesNotContain(SalesforceStandIn.AccessToken, stored, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for an org: its token endpoint, its limits, a lead added and one upserted, the leads by SOQL over two pages, one read and one deleted.</summary>
        private sealed class SalesforceStandIn : IDisposable
        {
            public const string AccessToken = "00Dstandin!access-token-1";

            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Method, string Path, string Query, string? Authorization, string Body)> _received = new();

            public SalesforceStandIn()
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

            private static string Lead(string id, string name) =>
                "{\"attributes\":{\"type\":\"Lead\",\"url\":\"" + Api + "/sobjects/Lead/" + id + "\"},\"Id\":\"" + id + "\",\"Name\":\"" + name + "\","
                    + "\"Owner\":{\"attributes\":{\"type\":\"User\"},\"Alias\":\"sales\"}}";

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

                    // The raw path, as sent - an external id's value stays escaped.
                    var raw = context.Request.RawUrl ?? "/";
                    var cut = raw.IndexOf('?', StringComparison.Ordinal);
                    var path = cut < 0 ? raw : raw[..cut];
                    var query = cut < 0 ? "" : raw[cut..];
                    var method = context.Request.HttpMethod;
                    lock (_gate)
                    {
                        _received.Add((method, path, query, context.Request.Headers["Authorization"], body));
                    }

                    var (status, answer) = (method, path) switch
                    {
                        ("POST", "/services/oauth2/token") => (200, "{\"access_token\":\"" + AccessToken + "\",\"instance_url\":\"http://127.0.0.1:" + Port + "\",\"token_type\":\"Bearer\"}"),
                        ("GET", Api + "/limits") => (200, "{\"DailyApiRequests\":{\"Max\":15000,\"Remaining\":14990}}"),
                        ("POST", Api + "/sobjects/Lead") => (201, "{\"id\":\"00Q000000000001AAA\",\"success\":true,\"errors\":[]}"),
                        ("PATCH", Api + "/sobjects/Lead/Email__c/grace%40example.com") => (201, "{\"id\":\"00Q000000000002AAA\",\"success\":true,\"errors\":[],\"created\":true}"),
                        ("GET", Api + "/query") => (200, "{\"totalSize\":2,\"done\":false,\"records\":[" + Lead("00Q000000000001AAA", "Ada Lovelace") + "],\"nextRecordsUrl\":\"" + Api + "/query/01gNEXT-2000\"}"),
                        ("GET", Api + "/query/01gNEXT-2000") => (200, "{\"totalSize\":2,\"done\":true,\"records\":[" + Lead("00Q000000000002AAA", "Grace Hopper") + "]}"),
                        ("GET", Api + "/sobjects/Lead/00Q000000000001AAA") => (200, Lead("00Q000000000001AAA", "Ada Lovelace")),
                        ("DELETE", Api + "/sobjects/Lead/00Q000000000002AAA") => (204, ""),
                        _ => (404, "[{\"errorCode\":\"NOT_FOUND\",\"message\":\"The requested resource does not exist\"}]"),
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
