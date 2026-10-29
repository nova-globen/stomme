#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Models.Pipelines;
using Stomme.Modules.Connectors.Shared;
using Stomme.Tests.Pipelines;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Amazon SES DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction
    /// port, against a loopback stand-in for the SES API v2 that checks every request's signature as AWS would - re-signed with
    /// the key's secret at the request's own time and refused when it differs: an Amazon SES connection made over the API, its
    /// key pair vaulted, tested; a published pipeline sends an HTML e-mail with an accent in its sender's name, makes a
    /// template, reads the templates over two pages, sends from the template and deletes it; the secret in neither the graph nor
    /// the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class AwsSesDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string AccessKey = "AKIADRIVEEXAMPLE";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_sends_makes_reads_and_deletes_signed_as_aws_checks()
        {
            var secret = "s" + Guid.NewGuid().ToString("N");
            using var ses = new SesStandIn(secret);
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
                    Name = "Shop mail", ConnectorType = "awsses", Endpoint = $"http://127.0.0.1:{ses.Port}", TimeoutSeconds = 10,
                    Settings = new() { ["region"] = "eu-north-1" },
                    Credentials = new() { ["access-key"] = AccessKey, ["secret-key"] = secret },
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
                        Call("send", "Send", "send_email", "from = Café Shop <shop@example.com>\nto = ada@example.com\nsubject = Your order\nhtml = <p>Thanks - café</p>"),
                        Call("template", "Template", "create_template", "template = Order_1\nsubject = Hej {{name}}\nhtml = <p>Hej {{name}}</p>"),
                        Call("templates", "Templates", "templates", "max = 3"),
                        Call("from", "From template", "send_template", "template = Order_1\ndata = {\"name\": \"Ada\"}\nfrom = shop@example.com\nto = ada@example.com"),
                        Call("drop", "Drop", "delete_template", "template = Order_1"),
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
                var received = ses.Received;
                Assert.All(received, request => Assert.True(request.Verified, "signature refused: " + request.Line));
                Assert.All(received, request => Assert.StartsWith($"AWS4-HMAC-SHA256 Credential={AccessKey}/", request.Authorization, StringComparison.Ordinal));
                Assert.Equal(new[]
                {
                    "GET /v2/email/account",
                    "POST /v2/email/outbound-emails {\"FromEmailAddress\":\"=?UTF-8?B?Q2Fmw6kgU2hvcA==?= <shop@example.com>\",\"Destination\":{\"ToAddresses\":[\"ada@example.com\"]},\"Content\":{\"Simple\":{\"Subject\":{\"Data\":\"Your order\",\"Charset\":\"UTF-8\"},\"Body\":{\"Html\":{\"Data\":\"<p>Thanks - café</p>\",\"Charset\":\"UTF-8\"}}}}}",
                    "POST /v2/email/templates {\"TemplateName\":\"Order_1\",\"TemplateContent\":{\"Subject\":\"Hej {{name}}\",\"Html\":\"<p>Hej {{name}}</p>\"}}",
                    "GET /v2/email/templates?PageSize=3",
                    "GET /v2/email/templates?PageSize=1&NextToken=page%2B2",
                    "POST /v2/email/outbound-emails {\"FromEmailAddress\":\"shop@example.com\",\"Destination\":{\"ToAddresses\":[\"ada@example.com\"]},\"Content\":{\"Template\":{\"TemplateName\":\"Order_1\",\"TemplateData\":\"{\\\"name\\\":\\\"Ada\\\"}\"}}}",
                    "DELETE /v2/email/templates/Order_1",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                var templates = JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == "templates")!;
                Assert.Contains("\"RecordCount\": 3", templates["outputsJson"]!.GetValue<string>()); // both pages, no more than asked
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(secret, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for the SES API v2 that re-signs every request with the secret at its own x-amz-date and records whether the signature agreed.</summary>
        private sealed class SesStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _secret;
            private readonly List<(string Line, string? Authorization, bool Verified)> _received = new();

            public SesStandIn(string secret)
            {
                _secret = secret;
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Line, string? Authorization, bool Verified)> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
                }
            }

            // The signature as AWS would compute it for this request: the same method, address, content type and body, at the
            // request's own time.
            private bool Verify(HttpListenerRequest request, byte[] body)
            {
                var date = request.Headers["x-amz-date"];
                var authorization = request.Headers["Authorization"];
                if (date is null || authorization is null)
                {
                    return false;
                }

                using var again = new HttpRequestMessage(new HttpMethod(request.HttpMethod), $"http://127.0.0.1:{Port}{request.RawUrl}");
                if (request.ContentType is { Length: > 0 } type)
                {
                    again.Content = new ByteArrayContent(body);
                    again.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
                }

                var when = DateTimeOffset.ParseExact(date, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
                AwsSigV4.Sign(again, body, "eu-north-1", "ses", AccessKey, _secret, null, when);
                return again.Headers.GetValues("Authorization").Single() == authorization;
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

                    byte[] body;
                    using (var buffer = new MemoryStream())
                    {
                        await context.Request.InputStream.CopyToAsync(buffer);
                        body = buffer.ToArray();
                    }

                    var path = context.Request.Url!.AbsolutePath;
                    var verified = Verify(context.Request, body);
                    lock (_gate)
                    {
                        _received.Add(($"{context.Request.HttpMethod} {context.Request.RawUrl}{(body.Length == 0 ? "" : " " + Encoding.UTF8.GetString(body))}", context.Request.Headers["Authorization"], verified));
                    }

                    var (status, answer) = !verified ? (403, """{"message":"The request signature we calculated does not match the signature you provided."}""")
                        : (context.Request.HttpMethod, path) switch
                        {
                            ("GET", "/v2/email/account") => (200, """{"SendingEnabled":true,"ProductionAccessEnabled":true,"SendQuota":{"Max24HourSend":50000.0,"MaxSendRate":14.0,"SentLast24Hours":3.0}}"""),
                            ("POST", "/v2/email/outbound-emails") => (200, """{"MessageId":"0102018-drive"}"""),
                            ("POST", "/v2/email/templates") => (200, "{}"),
                            ("GET", "/v2/email/templates") when context.Request.Url.Query.Contains("NextToken", StringComparison.Ordinal) => (200, """{"TemplatesMetadata":[{"TemplateName":"T3","CreatedTimestamp":1760259600}]}"""),
                            ("GET", "/v2/email/templates") => (200, """{"TemplatesMetadata":[{"TemplateName":"Order_1","CreatedTimestamp":1760259600},{"TemplateName":"T2","CreatedTimestamp":1760259600}],"NextToken":"page+2"}"""),
                            ("DELETE", "/v2/email/templates/Order_1") => (200, "{}"),
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
