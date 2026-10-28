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
    /// BL-229 - Amazon SQS DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction
    /// port, against a loopback stand-in for SQS's JSON protocol that checks every request's signature as AWS would - its
    /// target signed with it, re-signed with the key's secret at the request's own time and refused when it differs: an Amazon
    /// SQS connection made over the API, its key pair vaulted, tested; a published pipeline sends a message with attributes to a
    /// queue by its URL and one to a FIFO queue by its name, receives three over two calls, deletes one by its receipt handle and
    /// reads the queue's attributes; the secret in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class AwsSqsDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string AccessKey = "AKIADRIVEEXAMPLE";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_sends_receives_and_deletes_signed_as_aws_checks()
        {
            var secret = "s" + Guid.NewGuid().ToString("N");
            using var sqs = new SqsStandIn(secret);
            var queue = $"http://127.0.0.1:{sqs.Port}/123456789012/orders";
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
                    Name = "Shop queues", ConnectorType = "awssqs", Endpoint = $"http://127.0.0.1:{sqs.Port}", TimeoutSeconds = 10,
                    Settings = new() { ["region"] = "eu-north-1" },
                    Credentials = new() { ["access-key"] = AccessKey, ["secret-key"] = secret },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Queues" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("send", "Send", "send_message", $"queue = {queue}\nbody = {{\"order\": 1042, \"note\": \"Café\"}}\nattributes = {{\"source\": \"shop\", \"items\": 3}}"),
                        Call("fifo", "To FIFO", "send_message", "queue = orders.fifo\nbody = hi\ngroup = shop-1\ndeduplication = o-1042"),
                        Call("receive", "Receive", "receive_messages", $"queue = {queue}\nwait = 1\nmax = 3"),
                        Call("delete", "Delete", "delete_message", $"queue = {queue}\nreceipt = AQEB+r/1="),
                        Call("attributes", "Attributes", "queue_attributes", $"queue = {queue}"),
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
                var received = sqs.Received;
                Assert.All(received, request => Assert.True(request.Verified, "signature refused: " + request.Line));
                Assert.All(received, request => Assert.Contains("SignedHeaders=content-type;host;x-amz-date;x-amz-target,", request.Authorization, StringComparison.Ordinal));
                Assert.Equal(new[]
                {
                    "AmazonSQS.ListQueues {\"MaxResults\":1000}",
                    "AmazonSQS.SendMessage {\"MessageBody\":\"{\\\"order\\\": 1042, \\\"note\\\": \\\"Café\\\"}\",\"MessageAttributes\":{\"source\":{\"DataType\":\"String\",\"StringValue\":\"shop\"},\"items\":{\"DataType\":\"Number\",\"StringValue\":\"3\"}},\"QueueUrl\":\"" + queue + "\"}",
                    "AmazonSQS.GetQueueUrl {\"QueueName\":\"orders.fifo\"}",
                    "AmazonSQS.SendMessage {\"MessageBody\":\"hi\",\"MessageGroupId\":\"shop-1\",\"MessageDeduplicationId\":\"o-1042\",\"QueueUrl\":\"" + queue + ".fifo\"}",
                    "AmazonSQS.ReceiveMessage {\"WaitTimeSeconds\":1,\"MessageAttributeNames\":[\"All\"],\"MessageSystemAttributeNames\":[\"All\"],\"QueueUrl\":\"" + queue + "\",\"MaxNumberOfMessages\":3}",
                    "AmazonSQS.ReceiveMessage {\"WaitTimeSeconds\":1,\"MessageAttributeNames\":[\"All\"],\"MessageSystemAttributeNames\":[\"All\"],\"QueueUrl\":\"" + queue + "\",\"MaxNumberOfMessages\":1}",
                    "AmazonSQS.DeleteMessage {\"ReceiptHandle\":\"AQEB+r/1=\",\"QueueUrl\":\"" + queue + "\"}",
                    "AmazonSQS.GetQueueAttributes {\"AttributeNames\":[\"All\"],\"QueueUrl\":\"" + queue + "\"}",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Contains("\"RecordCount\": 3", Outputs("receive"), StringComparison.Ordinal); // both calls, no more than asked
                Assert.Contains("Café 1", Outputs("receive"), StringComparison.Ordinal);
                Assert.Contains("\"approximate_number_of_messages\": 2", Outputs("attributes"), StringComparison.Ordinal);
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(secret, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for SQS's JSON protocol that re-signs every request with the secret at its own x-amz-date and records whether the signature agreed.</summary>
        private sealed class SqsStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _secret;
            private readonly List<(string Line, string? Authorization, bool Verified)> _received = new();
            private int _receives;

            public SqsStandIn(string secret)
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

            // The signature as AWS would compute it for this request: the same method, address, target, content type and body, at
            // the request's own time.
            private bool Verify(HttpListenerRequest request, byte[] body)
            {
                var date = request.Headers["x-amz-date"];
                var authorization = request.Headers["Authorization"];
                var target = request.Headers["X-Amz-Target"];
                if (date is null || authorization is null || target is null)
                {
                    return false;
                }

                using var again = new HttpRequestMessage(new HttpMethod(request.HttpMethod), $"http://127.0.0.1:{Port}{request.RawUrl}") { Content = new ByteArrayContent(body) };
                again.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType ?? "");
                again.Headers.TryAddWithoutValidation("X-Amz-Target", target);
                var when = DateTimeOffset.ParseExact(date, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
                AwsSigV4.Sign(again, body, "eu-north-1", "sqs", AccessKey, _secret, null, when);
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

                    var target = context.Request.Headers["X-Amz-Target"] ?? "";
                    var verified = Verify(context.Request, body) && context.Request.HttpMethod == "POST" && context.Request.RawUrl == "/" && context.Request.ContentType == "application/x-amz-json-1.0";
                    lock (_gate)
                    {
                        _received.Add(($"{target} {Encoding.UTF8.GetString(body)}", context.Request.Headers["Authorization"], verified));
                    }

                    var queue = $"http://127.0.0.1:{Port}/123456789012/orders";
                    string Message(int n) => "{\"MessageId\":\"m-" + n + "\",\"ReceiptHandle\":\"r-" + n + "\",\"MD5OfBody\":\"x\",\"Body\":\"Café " + n + "\",\"Attributes\":{\"ApproximateReceiveCount\":\"1\"},\"MessageAttributes\":{\"source\":{\"DataType\":\"String\",\"StringValue\":\"shop\"}}}";
                    var (status, answer) = !verified ? (403, """{"__type":"com.amazon.coral.service#IncompleteSignatureException","message":"The request signature we calculated does not match the signature you provided."}""")
                        : target switch
                        {
                            "AmazonSQS.ListQueues" => (200, "{\"QueueUrls\":[\"" + queue + "\",\"" + queue + ".fifo\"]}"),
                            "AmazonSQS.SendMessage" => (200, """{"MessageId":"219f8380","MD5OfMessageBody":"fafb00f5"}"""),
                            "AmazonSQS.GetQueueUrl" => (200, "{\"QueueUrl\":\"" + queue + ".fifo\"}"),
                            "AmazonSQS.ReceiveMessage" => (200, ++_receives == 1 ? "{\"Messages\":[" + Message(1) + "," + Message(2) + "]}" : "{\"Messages\":[" + Message(3) + "]}"),
                            "AmazonSQS.DeleteMessage" => (200, "{}"),
                            "AmazonSQS.GetQueueAttributes" => (200, """{"Attributes":{"ApproximateNumberOfMessages":"2","QueueArn":"arn:aws:sqs:eu-north-1:123456789012:orders"}}"""),
                            _ => (400, """{"__type":"com.amazonaws.sqs#InvalidAction","message":"Unknown action."}"""),
                        };
                    context.Response.StatusCode = status;
                    var bytes = Encoding.UTF8.GetBytes(answer);
                    context.Response.ContentType = "application/x-amz-json-1.0";
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
