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
    /// BL-229 - Airtable DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for Airtable's API: an Airtable connection made over the API, its token vaulted and its base a
    /// setting, tested (whom the token is); a published pipeline searches the table with a formula over two pages, creates a
    /// record and updates the one it found - each call with the token as the bearer; the token is not in the graph or the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class AirtableDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string BaseId = "appAbCdEfGhIjKlMn";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_searches_a_table_creates_a_record_and_updates_one_through_a_vaulted_token()
        {
            var token = "pat-" + Guid.NewGuid().ToString("N");
            using var airtable = new AirtableStandIn();
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
                    Name = "Orders base", ConnectorType = "airtable", Endpoint = $"http://127.0.0.1:{airtable.Port}/v0", TimeoutSeconds = 10,
                    Settings = new() { ["base"] = BaseId },
                    Credentials = new() { ["token"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);
                Assert.Contains(airtable.Received, request => request.Path == "/v0/meta/whoami"); // the host words the answer itself (D110)

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Orders base" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                var graph = new PipelineGraph
                {
                    Variables = { new PipelineGraphVariable { Id = "v-open", Name = "open" } },
                    Steps =
                    {
                        new PipelineGraphStep { Id = "search", Type = "Stomme.ExtractFromConnection", Name = "Open orders", Inputs = { ["Connection"] = connection, ["Operation"] = "search", ["Parameters"] = "table = Orders\nformula = {Status} = 'Open'" }, OutputVariables = { ["Result"] = "v-open" } },
                        new PipelineGraphStep { Id = "create", Type = "Stomme.ExtractFromConnection", Name = "New order", Inputs = { ["Connection"] = connection, ["Operation"] = "create", ["Parameters"] = "table = Orders\nfields = {\"Name\": \"Grace\", \"Total\": \"12\"}" } },
                        new PipelineGraphStep { Id = "update", Type = "Stomme.ExtractFromConnection", Name = "Close it", Inputs = { ["Connection"] = connection, ["Operation"] = "update", ["Parameters"] = "table = Orders\nrecords = {\"id\": \"rec00000000000001\", \"Status\": \"Done\"}" } },
                    },
                    Connections =
                    {
                        new PipelineGraphConnection { SourceId = "search", SourcePort = "Done", TargetId = "create" },
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

                // Every call with the token as the bearer: whoami, the search's two pages (the formula on each), the create, the update.
                var received = airtable.Received;
                Assert.All(received, request => Assert.Equal("Bearer " + token, request.Authorization));
                var pages = received.Where(request => request.Method == "GET" && request.Path == $"/v0/{BaseId}/Orders").ToList();
                Assert.Equal(2, pages.Count);
                Assert.All(pages, page => Assert.Contains("filterByFormula=%7BStatus%7D%20%3D%20%27Open%27", page.Query, StringComparison.Ordinal));
                Assert.Contains("offset=itr2", pages[1].Query, StringComparison.Ordinal);
                Assert.Equal("""{"records":[{"fields":{"Name":"Grace","Total":"12"}}],"typecast":true}""", received.Single(request => request.Method == "POST").Body);
                Assert.Equal("""{"records":[{"id":"rec00000000000001","fields":{"Status":"Done"}}],"typecast":true}""", received.Single(request => request.Method == "PATCH").Body);

                // The records found were the search's result, the created one's id the create's.
                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.Contains("Ada", steps);
                Assert.Contains("Lin", steps);
                Assert.Contains("rec00000000000009", steps);
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for Airtable's API: a table 'Orders' whose open records come in two pages.</summary>
        private sealed class AirtableStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Method, string Path, string Query, string? Authorization, string Body)> _received = new();

            public AirtableStandIn()
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
                    var query = context.Request.Url!.Query;
                    var method = context.Request.HttpMethod;
                    lock (_gate)
                    {
                        _received.Add((method, path, query, context.Request.Headers["Authorization"], body));
                    }

                    var answer = (method, path) switch
                    {
                        ("GET", "/v0/meta/whoami") => """{"id":"usrAbc","email":"ops@example.com"}""",
                        ("GET", $"/v0/{BaseId}/Orders") when query.Contains("offset=", StringComparison.Ordinal) =>
                            """{"records":[{"id":"rec00000000000002","createdTime":"2026-10-02T08:00:00.000Z","fields":{"Name":"Lin","Status":"Open"}}]}""",
                        ("GET", $"/v0/{BaseId}/Orders") =>
                            """{"records":[{"id":"rec00000000000001","createdTime":"2026-10-01T08:00:00.000Z","fields":{"Name":"Ada","Status":"Open"}}],"offset":"itr2"}""",
                        ("POST", $"/v0/{BaseId}/Orders") =>
                            """{"records":[{"id":"rec00000000000009","createdTime":"2026-10-11T09:00:00.000Z","fields":{"Name":"Grace","Total":12}}]}""",
                        ("PATCH", $"/v0/{BaseId}/Orders") =>
                            """{"records":[{"id":"rec00000000000001","createdTime":"2026-10-01T08:00:00.000Z","fields":{"Name":"Ada","Status":"Done"}}]}""",
                        _ => """{"error":"NOT_FOUND"}""",
                    };
                    var bytes = Encoding.UTF8.GetBytes(answer);
                    context.Response.StatusCode = answer.Contains("NOT_FOUND", StringComparison.Ordinal) ? 404 : 200;
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
