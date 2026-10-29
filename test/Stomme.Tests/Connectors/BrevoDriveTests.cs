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
    /// Brevo DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port, against
    /// a loopback stand-in for Brevo's API: a Brevo connection made over the API, its key vaulted, tested (the account named); a
    /// published pipeline makes a contact, reads the contacts over two pages, sends an e-mail with HTML and an accent and deletes the
    /// contact - every call the key in Brevo's api-key header; the key in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class BrevoDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_makes_reads_mails_and_deletes_contacts()
        {
            var key = "xkeysib-" + Guid.NewGuid().ToString("N");
            using var brevo = new BrevoStandIn();
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
                    Name = "Mailing", ConnectorType = "brevo", Endpoint = $"http://127.0.0.1:{brevo.Port}/v3", TimeoutSeconds = 10,
                    Credentials = new() { ["api-key"] = key },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Mailing" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("make", "New contact", "create_contact", "email = ada@example.com\nattributes = {\"FIRSTNAME\": \"Café <b>Ada</b>\"}\nlist_ids = 2"),
                        Call("read", "Everyone", "contacts", "max = 600"),
                        Call("mail", "Mail", "send_email", "sender = Café Shop <shop@example.com>\nto = Ada <ada@example.com>\nsubject = Your order\nhtml = <p>Café &amp; more</p>"),
                        Call("drop", "Drop", "delete_contact", "contact = 42"),
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
                var received = brevo.Received;
                Assert.All(received, request => Assert.Equal(key, request.Key));
                Assert.Equal(new[]
                {
                    "GET /v3/account",
                    "POST /v3/contacts {\"email\":\"ada@example.com\",\"attributes\":{\"FIRSTNAME\":\"Café <b>Ada</b>\"},\"listIds\":[2]}",
                    "GET /v3/contacts?limit=500&offset=0&sort=desc",
                    "GET /v3/contacts?limit=100&offset=500&sort=desc",
                    "POST /v3/smtp/email {\"sender\":{\"email\":\"shop@example.com\",\"name\":\"Café Shop\"},\"to\":[{\"email\":\"ada@example.com\",\"name\":\"Ada\"}],\"subject\":\"Your order\",\"htmlContent\":\"<p>Café &amp; more</p>\"}",
                    "DELETE /v3/contacts/42",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                var read2 = JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == "read")!;
                Assert.Contains("\"RecordCount\": 540", read2["outputsJson"]!.GetValue<string>()); // both pages, the last short
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(key, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(key, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for Brevo's API: the account, a contact made, the contacts over two pages, an e-mail sent, a contact deleted.</summary>
        private sealed class BrevoStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Line, string? Key)> _received = new();

            public BrevoStandIn()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Line, string? Key)> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
                }
            }

            private static string Contacts(int from, int count) =>
                "{\"contacts\":[" + string.Join(",", Enumerable.Range(from, count).Select(n => "{\"id\":" + n + ",\"email\":\"c" + n + "@example.com\",\"listIds\":[2],\"attributes\":{\"FIRSTNAME\":\"C" + n + "\"}}")) + "]}";

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
                        _received.Add(($"{context.Request.HttpMethod} {url.PathAndQuery}{(body.Length == 0 ? "" : " " + body)}", context.Request.Headers["api-key"]));
                    }

                    var (status, answer) = (context.Request.HttpMethod, path) switch
                    {
                        ("GET", "/v3/account") => (200, """{"email":"owner@example.com","companyName":"Café Shop AB"}"""),
                        ("POST", "/v3/contacts") => (201, """{"id":42}"""),
                        ("GET", "/v3/contacts") when url.Query.Contains("offset=500", StringComparison.Ordinal) => (200, Contacts(501, 40)),
                        ("GET", "/v3/contacts") => (200, Contacts(1, 500)),
                        ("POST", "/v3/smtp/email") => (201, """{"messageId":"<m1@smtp-relay.mailin.fr>"}"""),
                        ("DELETE", "/v3/contacts/42") => (204, ""),
                        _ => (404, """{"code":"document_not_found","message":"Not found"}"""),
                    };
                    context.Response.StatusCode = status;
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
