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
    /// Notion DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for Notion's API: a Notion connection made over the API, its secret vaulted and its database a
    /// setting, tested (who the integration is); a published pipeline reads the database's open pages over two cursors, creates a
    /// page typed by the database's properties and updates the one it found - each call with the secret as the bearer and the API
    /// version named; the secret is not in the graph or the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class NotionDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Database = "01234567-89ab-cdef-0123-456789abcdef";
        private const string Ada = "aaaaaaaa-0000-0000-0000-000000000001";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_reads_a_databases_pages_creates_one_and_updates_one_through_a_vaulted_secret()
        {
            var secret = "ntn-" + Guid.NewGuid().ToString("N");
            using var notion = new NotionStandIn();
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
                    Name = "Orders database", ConnectorType = "notion", Endpoint = $"http://127.0.0.1:{notion.Port}/v1", TimeoutSeconds = 10,
                    Settings = new() { ["database"] = $"https://www.notion.so/acme/Orders-{Database.Replace("-", "")}" },
                    Credentials = new() { ["token"] = secret },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);
                Assert.Contains(notion.Received, request => request.Path == "/v1/users/me"); // the host words the answer itself

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Orders database" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                var graph = new PipelineGraph
                {
                    Variables = { new PipelineGraphVariable { Id = "v-open", Name = "open" } },
                    Steps =
                    {
                        new PipelineGraphStep { Id = "query", Type = "Stomme.ExtractFromConnection", Name = "Open orders", Inputs = { ["Connection"] = connection, ["Operation"] = "query", ["Parameters"] = "filter = {\"property\": \"Status\", \"status\": {\"equals\": \"Open\"}}" }, OutputVariables = { ["Result"] = "v-open" } },
                        new PipelineGraphStep { Id = "create", Type = "Stomme.ExtractFromConnection", Name = "New order", Inputs = { ["Connection"] = connection, ["Operation"] = "create", ["Parameters"] = "properties = {\"title\": \"Grace\", \"Total\": \"12\", \"Status\": \"Open\", \"Owner\": \"nobody\"}" } },
                        new PipelineGraphStep { Id = "update", Type = "Stomme.ExtractFromConnection", Name = "Close it", Inputs = { ["Connection"] = connection, ["Operation"] = "update", ["Parameters"] = $"pages = {{\"id\": \"{Ada}\", \"Status\": \"Done\"}}" } },
                    },
                    Connections =
                    {
                        new PipelineGraphConnection { SourceId = "query", SourcePort = "Done", TargetId = "create" },
                        new PipelineGraphConnection { SourceId = "create", SourcePort = "Done", TargetId = "update" },
                    },
                };
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

                // Every call with the secret as the bearer and the version named: who, the query's two cursors (the filter on each),
                // the database's properties, the page created, the page read and updated.
                var received = notion.Received;
                Assert.All(received, request => Assert.Equal(("Bearer " + secret, "2022-06-28"), (request.Authorization, request.Version)));
                var queries = received.Where(request => request.Path == $"/v1/databases/{Database}/query").ToList();
                Assert.Equal(2, queries.Count);
                Assert.All(queries, query => Assert.Equal("Open", JsonNode.Parse(query.Body)!["filter"]!["status"]!["equals"]!.GetValue<string>()));
                Assert.Equal("cursor-2", JsonNode.Parse(queries[1].Body)!["start_cursor"]!.GetValue<string>());
                var page = JsonNode.Parse(received.Single(request => request.Method == "POST" && request.Path == "/v1/pages").Body)!;
                Assert.Equal((Database, """{"title":[{"text":{"content":"Grace"}}]}""", """{"number":12}""", """{"status":{"name":"Open"}}"""),
                    (page["parent"]!["database_id"]!.GetValue<string>(), page["properties"]!["Name"]!.ToJsonString(), page["properties"]!["Total"]!.ToJsonString(), page["properties"]!["Status"]!.ToJsonString()));
                Assert.Equal("""{"properties":{"Status":{"status":{"name":"Done"}}}}""", received.Single(request => request.Method == "PATCH").Body);

                // The pages found were the query's result; what no property took was said.
                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.Contains("Ada", steps);
                Assert.Contains("Lin", steps);
                var createdOutputs = JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == "create")!["outputsJson"]!.GetValue<string>();
                Assert.Contains("\"left_out\": \"Owner\"", createdOutputs);
                Assert.DoesNotContain(secret, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for Notion's API: a database 'Orders' (Name, Total, Status) whose open pages come in two cursors.</summary>
        private sealed class NotionStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Method, string Path, string? Authorization, string? Version, string Body)> _received = new();

            public NotionStandIn()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Method, string Path, string? Authorization, string? Version, string Body)> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
                }
            }

            private static string Page(string id, string name, string status) =>
                """{"object":"page","id":"@ID","url":"https://www.notion.so/@SLUG","created_time":"2026-10-01T08:00:00.000Z","last_edited_time":"2026-10-02T08:00:00.000Z","properties":{"Name":{"id":"title","type":"title","title":[{"plain_text":"@NAME"}]},"Total":{"id":"a","type":"number","number":12},"Status":{"id":"b","type":"status","status":{"name":"@STATUS"}}}}"""
                    .Replace("@ID", id, StringComparison.Ordinal).Replace("@SLUG", id.Replace("-", "", StringComparison.Ordinal), StringComparison.Ordinal)
                    .Replace("@NAME", name, StringComparison.Ordinal).Replace("@STATUS", status, StringComparison.Ordinal);

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

                    var path = Uri.UnescapeDataString(context.Request.Url!.AbsolutePath);
                    var method = context.Request.HttpMethod;
                    lock (_gate)
                    {
                        _received.Add((method, path, context.Request.Headers["Authorization"], context.Request.Headers["Notion-Version"], body));
                    }

                    var answer = (method, path) switch
                    {
                        ("GET", "/v1/users/me") => """{"object":"user","id":"b1","name":"Orders bot","type":"bot","bot":{"workspace_name":"Acme"}}""",
                        ("POST", $"/v1/databases/{Database}/query") when body.Contains("start_cursor", StringComparison.Ordinal) =>
                            $$"""{"results":[{{Page("aaaaaaaa-0000-0000-0000-000000000002", "Lin", "Open")}}],"has_more":false,"next_cursor":null}""",
                        ("POST", $"/v1/databases/{Database}/query") => $$"""{"results":[{{Page(Ada, "Ada", "Open")}}],"has_more":true,"next_cursor":"cursor-2"}""",
                        ("GET", $"/v1/databases/{Database}") =>
                            """{"object":"database","properties":{"Name":{"id":"title","type":"title","title":{}},"Total":{"id":"a","type":"number","number":{}},"Status":{"id":"b","type":"status","status":{}}}}""",
                        ("POST", "/v1/pages") => Page("aaaaaaaa-0000-0000-0000-000000000009", "Grace", "Open"),
                        ("GET", $"/v1/pages/{Ada}") => Page(Ada, "Ada", "Open"),
                        ("PATCH", $"/v1/pages/{Ada}") => Page(Ada, "Ada", "Done"),
                        _ => """{"object":"error","status":404,"code":"object_not_found"}""",
                    };
                    var bytes = Encoding.UTF8.GetBytes(answer);
                    context.Response.StatusCode = answer.Contains("object_not_found", StringComparison.Ordinal) ? 404 : 200;
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
