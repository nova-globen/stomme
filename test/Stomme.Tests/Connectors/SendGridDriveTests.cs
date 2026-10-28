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
    /// BL-229 - SendGrid DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for SendGrid's v3 API: a SendGrid connection made over the API, its key vaulted, tested (the
    /// key's scopes read); a published pipeline adds a contact to a list, finds contacts by a query, reads the lists over two pages
    /// by SendGrid's token and sends a mail - every call the key as the bearer; the key is in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class SendGridDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_adds_finds_reads_lists_and_sends_a_mail()
        {
            var key = "SG." + Guid.NewGuid().ToString("N");
            using var sendgrid = new SendGridStandIn();
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
                    Name = "Mail", ConnectorType = "sendgrid", Endpoint = $"http://127.0.0.1:{sendgrid.Port}/v3", TimeoutSeconds = 10, Credentials = new() { ["token"] = key },
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
                        Call("add", "Add Ada", "upsert_contacts", "email = ada@example.com\nfirst_name = Ada\nlists = l1"),
                        Call("find", "Find", "contacts", "query = email LIKE '%@example.com'"),
                        Call("lists", "Lists", "lists", "max = 1200"),
                        Call("mail", "Mail", "send_mail", "from = shop@example.com\nto = ada@example.com\nsubject = Your order\nbody = <p>Thanks, <b>Ada</b></p>\nhtml = true"),
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
                var received = sendgrid.Received;
                Assert.All(received, request => Assert.Equal("Bearer " + key, request.Authorization));
                Assert.Equal(new[]
                {
                    "GET /v3/scopes",
                    "PUT /v3/marketing/contacts {\"contacts\":[{\"email\":\"ada@example.com\",\"first_name\":\"Ada\"}],\"list_ids\":[\"l1\"]}",
                    "POST /v3/marketing/contacts/search {\"query\":\"email LIKE '%@example.com'\"}",
                    "GET /v3/marketing/lists?page_size=1000",
                    "GET /v3/marketing/lists?page_size=200&page_token=tok2",
                    "POST /v3/mail/send {\"personalizations\":[{\"to\":[{\"email\":\"ada@example.com\"}],\"subject\":\"Your order\"}],\"from\":{\"email\":\"shop@example.com\"},\"content\":[{\"type\":\"text/html\",\"value\":\"<p>Thanks, <b>Ada</b></p>\"}]}",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                var lists = JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == "lists")!;
                Assert.Contains("\"RecordCount\": 1200", lists["outputsJson"]!.GetValue<string>()); // both pages, up to the most asked for
                var mail = JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == "mail")!;
                Assert.Contains("msg-drive-1", mail["outputsJson"]!.GetValue<string>(), StringComparison.Ordinal); // SendGrid's message id
                Assert.DoesNotContain(key, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(key, (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for SendGrid's v3 API: the key's scopes, a contact added, contacts found, the lists over two pages, a mail sent.</summary>
        private sealed class SendGridStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Line, string? Authorization)> _received = new();

            public SendGridStandIn()
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

            private static string Lists(int from, int count) => string.Join(",", Enumerable.Range(from, count).Select(n => "{\"id\":\"l" + n + "\",\"name\":\"List " + n + "\",\"contact_count\":" + n + "}"));

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
                        _received.Add(($"{context.Request.HttpMethod} {url.PathAndQuery}{(body.Length == 0 ? "" : " " + body)}", context.Request.Headers["Authorization"]));
                    }

                    var (status, answer) = (context.Request.HttpMethod, path) switch
                    {
                        ("GET", "/v3/scopes") => (200, "{\"scopes\":[\"mail.send\",\"marketing.read\"]}"),
                        ("PUT", "/v3/marketing/contacts") => (202, "{\"job_id\":\"job-1\"}"),
                        ("POST", "/v3/marketing/contacts/search") => (200, "{\"result\":[{\"id\":\"c1\",\"email\":\"ada@example.com\",\"first_name\":\"Ada\",\"list_ids\":[\"l1\"]}],\"contact_count\":1}"),
                        ("GET", "/v3/marketing/lists") when url.Query.Contains("page_token=tok2", StringComparison.Ordinal) => (200, "{\"result\":[" + Lists(1001, 1000) + "],\"_metadata\":{}}"),
                        ("GET", "/v3/marketing/lists") => (200, "{\"result\":[" + Lists(1, 1000) + "],\"_metadata\":{\"next\":\"http://127.0.0.1/v3/marketing/lists?page_size=1000&page_token=tok2\"}}"),
                        ("POST", "/v3/mail/send") => (202, ""),
                        _ => (404, "{\"errors\":[{\"field\":null,\"message\":\"resource not found\"}]}"),
                    };
                    context.Response.StatusCode = status;
                    if (path == "/v3/mail/send")
                    {
                        context.Response.AddHeader("X-Message-Id", "msg-drive-1");
                    }

                    if (answer.Length > 0)
                    {
                        var bytes = Encoding.UTF8.GetBytes(answer);
                        context.Response.ContentType = "application/json";
                        await context.Response.OutputStream.WriteAsync(bytes);
                    }

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
