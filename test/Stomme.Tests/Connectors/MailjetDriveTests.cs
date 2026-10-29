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
    /// Mailjet DRIVEN on an installed SQLite host, with the REAL connectors, the host's egress and the extraction port,
    /// against a loopback stand-in for Mailjet's API that answers the e-mail calls for the API key and secret key alone and the
    /// SMS calls for the SMS token alone: two connections made over the API, their secrets vaulted, tested; a published pipeline
    /// sends an e-mail with an accent, sends a template, reads the templates over two pages - no more than asked - and sends an
    /// SMS; the secrets in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class MailjetDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_sends_mail_and_an_sms_and_reads_the_templates_with_their_secrets()
        {
            var secret = "mj-" + Guid.NewGuid().ToString("N");
            var token = "sms-" + Guid.NewGuid().ToString("N");
            using var mailjet = new MailjetStandIn("key-drive", secret, token);
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
                async Task<string> Connect(string name, string type, Dictionary<string, string> credentials)
                {
                    var made = await Send(HttpMethod.Post, "/api/Connections", new ConnectionRequest { Name = name, ConnectorType = type, Endpoint = $"http://127.0.0.1:{mailjet.Port}", TimeoutSeconds = 10, Credentials = credentials }, "the connection " + name);
                    Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                    var id = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                    var tested = await Send(HttpMethod.Post, $"/api/Connections/{id}/test", new { }, "test " + name);
                    Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);
                    return id;
                }

                var mail = await Connect("Kafé mail", "mailjet", new() { ["username"] = "key-drive", ["password"] = secret });
                var sms = await Connect("Kafé SMS", "mailjetsms", new() { ["token"] = token });

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kafé letters" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string connection, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("mail", "Mail", mail, "send_email", "from = Kafé <kafe@example.com>\nto = ada@example.com\nsubject = Fika & bröd\ntext = Hej!"),
                        Call("template", "Template", mail, "send_template", "from = kafe@example.com\nto = ada@example.com\ntemplate = 4711\nvariables = {\"order\": \"1001\"}"),
                        Call("templates", "Templates", mail, "templates", "max = 120"),
                        Call("sms", "SMS", sms, "send_sms", "from = Kafe\nto = +46701740605\ntext = Fika på café"),
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
                var received = mailjet.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "refused: " + request.Line));
                Assert.Equal(new[]
                {
                    "GET /v3/REST/sender?Limit=10",
                    "GET /v4/sms?Limit=1",
                    "POST /v3.1/send {\"Messages\":[{\"From\":{\"Email\":\"kafe@example.com\",\"Name\":\"Kafé\"},\"To\":[{\"Email\":\"ada@example.com\"}],\"Subject\":\"Fika & bröd\",\"TextPart\":\"Hej!\"}]}",
                    "POST /v3.1/send {\"Messages\":[{\"From\":{\"Email\":\"kafe@example.com\"},\"To\":[{\"Email\":\"ada@example.com\"}],\"TemplateID\":4711,\"Variables\":{\"order\":\"1001\"}}]}",
                    "GET /v3/REST/template?Limit=100&Offset=0",
                    "GET /v3/REST/template?Limit=100&Offset=100",
                    "POST /v4/sms-send {\"From\":\"Kafe\",\"To\":\"+46701740605\",\"Text\":\"Fika på café\"}",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal("success", JsonNode.Parse(Outputs("mail"))!["Result"]![0]!["status"]!.GetValue<string>());
                Assert.Equal(120, JsonNode.Parse(Outputs("templates"))!["RecordCount"]!.GetValue<int>()); // both pages, no more than asked
                Assert.Equal("sent", JsonNode.Parse(Outputs("sms"))!["Result"]![0]!["state"]!.GetValue<string>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                foreach (var hidden in new[] { secret, token })
                {
                    Assert.DoesNotContain(hidden, steps, StringComparison.Ordinal);
                    Assert.DoesNotContain(hidden, graphBody, StringComparison.Ordinal);
                }
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for Mailjet: the e-mail calls for the API key and secret key alone, the SMS calls for the SMS token alone.</summary>
        private sealed class MailjetStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _basic;
            private readonly string _bearer;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public MailjetStandIn(string key, string secret, string token)
            {
                _basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(key + ":" + secret));
                _bearer = "Bearer " + token;
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Line, bool Authorized)> Received
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

                    var raw = context.Request.RawUrl ?? "";
                    var method = context.Request.HttpMethod;
                    var path = raw.Split('?')[0];
                    var authorized = context.Request.Headers["Authorization"] == (path.StartsWith("/v4/", StringComparison.Ordinal) ? _bearer : _basic);
                    lock (_gate)
                    {
                        _received.Add(($"{method} {raw}{(body.Length == 0 ? "" : " " + body)}", authorized));
                    }

                    static string Templates(int first, int count) => "{\"Count\":" + count + ",\"Total\":180,\"Data\":[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"ID\":" + (4700 + i) + ",\"Name\":\"Kvitto " + i + "\"}")) + "]}";
                    var (status, answer) = !authorized ? (401, "{\"ErrorMessage\":\"API key authentication/authorization failure.\"}")
                        : (method, path) switch
                        {
                            ("GET", "/v3/REST/sender") => (200, "{\"Count\":1,\"Data\":[{\"Email\":\"kafe@example.com\"}],\"Total\":1}"),
                            ("GET", "/v4/sms") => (200, "{\"Data\":[]}"),
                            ("POST", "/v3.1/send") => (200, "{\"Messages\":[{\"Status\":\"success\",\"To\":[{\"Email\":\"ada@example.com\",\"MessageUUID\":\"u-1\",\"MessageID\":576460752303423487}]}]}"),
                            ("GET", "/v3/REST/template") => (200, raw.Contains("Offset=100", StringComparison.Ordinal) ? Templates(101, 80) : Templates(1, 100)),
                            ("POST", "/v4/sms-send") => (200, "{\"From\":\"Kafe\",\"To\":\"+46701740605\",\"MessageId\":\"2034075536371630429\",\"SmsCount\":1,\"Status\":{\"Code\":2,\"Name\":\"sent\"}}"),
                            _ => (404, "{\"ErrorMessage\":\"Not found\"}"),
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
