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
    /// Pipedrive DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for Pipedrive's API: a Pipedrive connection made over the API, its token vaulted, tested (the
    /// user named); a published pipeline adds a deal, reads the open deals over two pages by the cursor, finds a person and notes
    /// the deal - every call the token in Pipedrive's header, never in an address; the token is in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class PipedriveDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_adds_a_deal_reads_deals_finds_a_person_and_notes_the_deal()
        {
            var token = "pd" + Guid.NewGuid().ToString("N");
            using var pipedrive = new PipedriveStandIn();
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
                    Name = "Sales pipeline", ConnectorType = "pipedrive", Endpoint = $"http://127.0.0.1:{pipedrive.Port}", TimeoutSeconds = 10, Credentials = new() { ["api-key"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Deals" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Variables = { new PipelineGraphVariable { Id = "v-open", Name = "open" } },
                    Steps =
                    {
                        Call("deal", "Add a deal", "create", "object = deals\nfields = {\"title\": \"Big deal\", \"value\": 5000, \"currency\": \"SEK\", \"org_id\": 31}"),
                        Call("open", "Open deals", "list", "object = deals\nstatus = open"),
                        Call("find", "Find Ada", "search", "object = persons\nterm = Ada"),
                        Call("note", "Note the deal", "add_note", "content = Sent the quote.\ndeal_id = 5"),
                    },
                };
                graph.Steps[1].OutputVariables["Result"] = "v-open";
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
                var received = pipedrive.Received;
                Assert.All(received, request => Assert.Equal(token, request.Token));
                Assert.All(received, request => Assert.DoesNotContain(token, request.PathAndQuery, StringComparison.Ordinal));
                Assert.Equal("{\"title\":\"Big deal\",\"value\":5000,\"currency\":\"SEK\",\"org_id\":31}", received.Single(request => request.Method == "POST" && request.PathAndQuery == "/api/v2/deals").Body);
                var pages = received.Where(request => request.PathAndQuery.StartsWith("/api/v2/deals?", StringComparison.Ordinal)).ToList();
                Assert.Equal(2, pages.Count); // both pages, by the cursor
                Assert.All(pages, page => Assert.Contains("status=open", page.PathAndQuery, StringComparison.Ordinal));
                Assert.Contains("cursor=next1", pages[1].PathAndQuery, StringComparison.Ordinal);
                Assert.Contains(received, request => request.PathAndQuery.StartsWith("/api/v2/persons/search?term=Ada", StringComparison.Ordinal));
                Assert.Equal("{\"content\":\"Sent the quote.\",\"deal_id\":5}", received.Single(request => request.PathAndQuery == "/v1/notes").Body);

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.Contains("Second deal", steps); // the second page's deal
                Assert.Contains("Ada Lovelace", steps);
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for Pipedrive's API: the user, a deal added, the open deals over two pages, a person found, a note added.</summary>
        private sealed class PipedriveStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Method, string PathAndQuery, string? Token, string Body)> _received = new();

            public PipedriveStandIn()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Method, string PathAndQuery, string? Token, string Body)> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
                }
            }

            private static string Deal(int id, string title) => "{\"id\":" + id + ",\"title\":\"" + title + "\",\"value\":5000,\"currency\":\"SEK\",\"status\":\"open\",\"org_id\":31}";

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
                        _received.Add((method, raw, context.Request.Headers["x-api-token"], body));
                    }

                    var answer = (method, path) switch
                    {
                        ("GET", "/v1/users/me") => "{\"success\":true,\"data\":{\"id\":7,\"name\":\"Ada Lovelace\",\"company_name\":\"Acme\"}}",
                        ("POST", "/api/v2/deals") => "{\"success\":true,\"data\":" + Deal(5, "Big deal") + "}",
                        ("GET", "/api/v2/deals") when raw.Contains("cursor=next1", StringComparison.Ordinal) => "{\"success\":true,\"data\":[" + Deal(2, "Second deal") + "],\"additional_data\":{\"next_cursor\":null}}",
                        ("GET", "/api/v2/deals") => "{\"success\":true,\"data\":[" + Deal(1, "First deal") + "],\"additional_data\":{\"next_cursor\":\"next1\"}}",
                        ("GET", "/api/v2/persons/search") => "{\"success\":true,\"data\":{\"items\":[{\"result_score\":0.9,\"item\":{\"id\":21,\"name\":\"Ada Lovelace\"}}]},\"additional_data\":{\"next_cursor\":null}}",
                        ("POST", "/v1/notes") => "{\"success\":true,\"data\":{\"id\":91,\"content\":\"Sent the quote.\",\"deal_id\":5}}",
                        _ => "{\"success\":false,\"error\":\"Unknown method\"}",
                    };
                    var bytes = Encoding.UTF8.GetBytes(answer);
                    context.Response.StatusCode = answer.Contains("\"success\":false", StringComparison.Ordinal) ? 404 : 200;
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
