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
    /// BL-229 - AWS Lambda DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for Lambda's API that checks every request's signature as AWS would - re-signed with the key's
    /// secret at the request's own time over the path as it arrived, and refused when it differs: an AWS Lambda connection made
    /// over the API, its key pair vaulted, tested; a published pipeline invokes a function by its ARN with a payload with an
    /// accent and reads its answer's fields, queues another as an event at an alias and reads the functions; the secret in
    /// neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class AwsLambdaDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string AccessKey = "AKIADRIVEEXAMPLE";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_invokes_queues_and_lists_signed_as_aws_checks()
        {
            var secret = "s" + Guid.NewGuid().ToString("N");
            using var lambda = new LambdaStandIn(secret);
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
                    Name = "Shop functions", ConnectorType = "awslambda", Endpoint = $"http://127.0.0.1:{lambda.Port}", TimeoutSeconds = 10,
                    Settings = new() { ["region"] = "eu-north-1" },
                    Credentials = new() { ["access-key"] = AccessKey, ["secret-key"] = secret },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Functions" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("resize", "Resize", "invoke", "function = arn:aws:lambda:eu-north-1:123456789012:function:resize-images\npayload = {\"image\": \"café.jpg\", \"width\": 800}"),
                        Call("notify", "Notify", "invoke", "function = notify\nqualifier = live\nevent = true\npayload = {\"order\": 1042}"),
                        Call("functions", "Functions", "functions", "max = 10"),
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
                var received = lambda.Received;
                Assert.All(received, request => Assert.True(request.Verified, "signature refused: " + request.Line));
                Assert.Equal(new[]
                {
                    "GET /2015-03-31/functions/?MaxItems=50 -",
                    "POST /2015-03-31/functions/arn%3Aaws%3Alambda%3Aeu-north-1%3A123456789012%3Afunction%3Aresize-images/invocations RequestResponse {\"image\": \"café.jpg\", \"width\": 800}",
                    "POST /2015-03-31/functions/notify/invocations?Qualifier=live Event {\"order\": 1042}",
                    "GET /2015-03-31/functions/?MaxItems=10 -",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Contains("\"resized\": \"café-800.jpg\"", Outputs("resize"), StringComparison.Ordinal); // the answer's own fields
                Assert.Contains("\"queued\": true", Outputs("notify"), StringComparison.Ordinal);
                Assert.Contains("\"runtime\": \"nodejs20.x\"", Outputs("functions"), StringComparison.Ordinal);
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(secret, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for Lambda's API that re-signs every request with the secret at its own x-amz-date and records whether the signature agreed.</summary>
        private sealed class LambdaStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _secret;
            private readonly List<(string Line, bool Verified)> _received = new();

            public LambdaStandIn(string secret)
            {
                _secret = secret;
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Line, bool Verified)> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
                }
            }

            // The signature as AWS would compute it for this request: the same method, path as it arrived, invocation type,
            // content type and body, at the request's own time.
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

                if (request.Headers["X-Amz-Invocation-Type"] is { } invocation)
                {
                    again.Headers.TryAddWithoutValidation("X-Amz-Invocation-Type", invocation);
                }

                var when = DateTimeOffset.ParseExact(date, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
                AwsSigV4.Sign(again, body, "eu-north-1", "lambda", AccessKey, _secret, null, when);
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

                    var raw = context.Request.RawUrl ?? "";
                    var invocation = context.Request.Headers["X-Amz-Invocation-Type"];
                    var verified = Verify(context.Request, body);
                    lock (_gate)
                    {
                        _received.Add(($"{context.Request.HttpMethod} {raw} {(invocation is null ? "-" : invocation + " " + Encoding.UTF8.GetString(body))}", verified));
                    }

                    var (status, answer, failure) = !verified ? (403, """{"message":"The request signature we calculated does not match the signature you provided."}""", (string?)null)
                        : raw.StartsWith("/2015-03-31/functions/?", StringComparison.Ordinal) ? (200, """{"Functions":[{"FunctionName":"resize-images","Runtime":"nodejs20.x","MemorySize":512,"LastModified":"2026-10-01T12:00:00.000+0000"},{"FunctionName":"notify","Runtime":"python3.12"}]}""", null)
                        : invocation == "Event" ? (202, "", null)
                        : raw.EndsWith("/invocations", StringComparison.Ordinal) ? (200, """{"resized":"café-800.jpg","width":800}""", null)
                        : (404, """{"Type":"User","message":"Function not found"}""", null);
                    context.Response.StatusCode = status;
                    if (failure is not null)
                    {
                        context.Response.Headers["X-Amz-Function-Error"] = failure;
                    }

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
