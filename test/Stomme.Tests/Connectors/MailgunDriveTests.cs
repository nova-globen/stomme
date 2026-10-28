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
    /// BL-229 - Mailgun DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for Mailgun's API: a Mailgun connection made over the API, its domain a setting and its key
    /// vaulted, tested (the domain named); a published pipeline sends an e-mail with HTML and an accent, reads the failed events
    /// over two pages by Mailgun's own next link, reads the bounces over two pages by theirs and deletes one - every call the key
    /// as HTTP Basic; the key in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class MailgunDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_sends_reads_events_and_bounces_and_deletes_one()
        {
            var key = "key-" + Guid.NewGuid().ToString("N");
            using var mailgun = new MailgunStandIn();
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
                    Name = "Mail", ConnectorType = "mailgun", Endpoint = $"http://127.0.0.1:{mailgun.Port}/v3", TimeoutSeconds = 10,
                    Settings = new() { ["domain"] = "mg.example.com" },
                    Credentials = new() { ["api-key"] = key },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Mail" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("send", "Send", "send_email", "from = Café Shop <shop@mg.example.com>\nto = Ada <ada@example.com>\nsubject = Your order\nhtml = <p>Café &amp; more</p>\ntags = orders"),
                        Call("events", "Failed", "events", "event = failed\nmax = 400"),
                        Call("bounces", "Bounces", "bounces", "max = 1500"),
                        Call("drop", "Unbounce", "delete_suppression", "list = bounces\naddress = ada@example.com"),
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
                var received = mailgun.Received;
                Assert.All(received, request => Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("api:" + key)), request.Authorization));
                Assert.Equal(new[]
                {
                    "GET /v3/domains/mg.example.com",
                    "POST /v3/mg.example.com/messages from=Caf%C3%A9+Shop+%3Cshop%40mg.example.com%3E&to=Ada+%3Cada%40example.com%3E&subject=Your+order&html=%3Cp%3ECaf%C3%A9+%26amp%3B+more%3C%2Fp%3E&o%3Atag=orders",
                    "GET /v3/mg.example.com/events?event=failed&ascending=no&limit=300",
                    "GET /v3/mg.example.com/events/page-2",
                    "GET /v3/mg.example.com/bounces?limit=1000",
                    "GET /v3/mg.example.com/bounces?page=next&address=c1000%40example.com",
                    "GET /v3/mg.example.com/bounces?page=last",
                    "DELETE /v3/mg.example.com/bounces/ada%40example.com",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                var events = JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == "events")!;
                Assert.Contains("\"RecordCount\": 400", events["outputsJson"]!.GetValue<string>()); // both pages, no more than asked
                var bounces = JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == "bounces")!;
                Assert.Contains("\"RecordCount\": 1200", bounces["outputsJson"]!.GetValue<string>()); // the second page by its query link
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(key, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(key, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for Mailgun's API: the domain, a message sent, failed events over two pages, bounces over two pages, a bounce deleted.</summary>
        private sealed class MailgunStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Line, string? Authorization)> _received = new();

            public MailgunStandIn()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Line, string? Authorization)> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
                }
            }

            private string Events(int from, int count, string next) =>
                "{\"items\":[" + string.Join(",", Enumerable.Range(from, count).Select(n => "{\"id\":\"e" + n + "\",\"event\":\"failed\",\"timestamp\":1760259600.5,\"recipient\":\"c" + n + "@example.com\",\"delivery-status\":{\"code\":550}}"))
                + "],\"paging\":{\"next\":\"http://127.0.0.1:" + Port + "/v3/mg.example.com/events/" + next + "\"}}";

            private string Bounces(int from, int count, string next) =>
                "{\"items\":[" + string.Join(",", Enumerable.Range(from, count).Select(n => "{\"address\":\"c" + n + "@example.com\",\"code\":\"550\",\"error\":\"No such mailbox\",\"created_at\":\"Mon, 05 Oct 2026 09:00:00 UTC\"}"))
                + "],\"paging\":{\"next\":\"http://127.0.0.1:" + Port + "/v3/mg.example.com/bounces?" + next + "\"}}";

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

                    var url = context.Request.Url!;
                    var path = url.AbsolutePath;
                    lock (_gate)
                    {
                        _received.Add(($"{context.Request.HttpMethod} {context.Request.RawUrl}{(body.Length == 0 ? "" : " " + body)}", context.Request.Headers["Authorization"]));
                    }

                    var (status, answer) = (context.Request.HttpMethod, path) switch
                    {
                        ("GET", "/v3/domains/mg.example.com") => (200, """{"domain":{"name":"mg.example.com","state":"active"}}"""),
                        ("POST", "/v3/mg.example.com/messages") => (200, """{"id":"<20261012.1@mg.example.com>","message":"Queued. Thank you."}"""),
                        ("GET", "/v3/mg.example.com/events") => (200, Events(1, 300, "page-2")),
                        ("GET", "/v3/mg.example.com/events/page-2") => (200, Events(301, 300, "page-3")),
                        ("GET", "/v3/mg.example.com/bounces") when url.Query.Contains("page=last", StringComparison.Ordinal) => (200, """{"items":[],"paging":{}}"""),
                        ("GET", "/v3/mg.example.com/bounces") when url.Query.Contains("page=next", StringComparison.Ordinal) => (200, Bounces(1001, 200, "page=last")),
                        ("GET", "/v3/mg.example.com/bounces") => (200, Bounces(1, 1000, "page=next&address=c1000%40example.com")),
                        ("DELETE", _) when path.StartsWith("/v3/mg.example.com/bounces/ada", StringComparison.Ordinal) => (200, """{"address":"ada@example.com","message":"Bounced address has been removed"}"""),
                        _ => (404, """{"message":"Not found"}"""),
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
