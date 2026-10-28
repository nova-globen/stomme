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
    /// BL-229 - Amazon SNS DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction
    /// port, against a loopback stand-in for SNS's query API that checks every request's signature as AWS would - re-signed
    /// with the key's secret at the request's own time and refused when it differs: an Amazon SNS connection made over the API,
    /// its key pair vaulted, tested; a published pipeline makes a topic, publishes a message with an accent and an attribute
    /// to it by its name (found among the topics), subscribes an address, reads the subscriptions and deletes the topic; the
    /// secret in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class AwsSnsDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string AccessKey = "AKIADRIVEEXAMPLE";
        private const string Orders = "arn:aws:sns:eu-north-1:123456789012:orders";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_makes_publishes_subscribes_and_deletes_signed_as_aws_checks()
        {
            var secret = "s" + Guid.NewGuid().ToString("N");
            using var sns = new SnsStandIn(secret);
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
                    Name = "Shop topics", ConnectorType = "awssns", Endpoint = $"http://127.0.0.1:{sns.Port}", TimeoutSeconds = 10,
                    Settings = new() { ["region"] = "eu-north-1" },
                    Credentials = new() { ["access-key"] = AccessKey, ["secret-key"] = secret },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Topics" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("create", "New topic", "create_topic", "name = orders\ndisplay_name = Shop orders"),
                        Call("publish", "Publish", "publish", "topic = orders\nsubject = Order 1042\nmessage = Tack för din beställning!\nattributes = {\"source\": \"shop\"}"),
                        Call("subscribe", "Subscribe", "subscribe", $"topic = {Orders}\nprotocol = email\nendpoint = ada@example.com"),
                        Call("subscriptions", "Subscriptions", "subscriptions", $"topic = {Orders}"),
                        Call("delete", "Drop topic", "delete_topic", $"topic = {Orders}"),
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
                var received = sns.Received;
                Assert.All(received, request => Assert.True(request.Verified, "signature refused: " + request.Body));
                const string Arn = "arn%3Aaws%3Asns%3Aeu-north-1%3A123456789012%3Aorders";
                Assert.Equal(new[]
                {
                    "Action=ListTopics&Version=2010-03-31",
                    "Action=CreateTopic&Version=2010-03-31&Name=orders&Attributes.entry.1.key=DisplayName&Attributes.entry.1.value=Shop%20orders",
                    "Action=ListTopics&Version=2010-03-31",
                    $"Action=Publish&Version=2010-03-31&TopicArn={Arn}&Message=Tack%20f%C3%B6r%20din%20best%C3%A4llning%21&Subject=Order%201042&MessageAttributes.entry.1.Name=source&MessageAttributes.entry.1.Value.DataType=String&MessageAttributes.entry.1.Value.StringValue=shop",
                    $"Action=Subscribe&Version=2010-03-31&TopicArn={Arn}&Protocol=email&Endpoint=ada%40example.com&ReturnSubscriptionArn=true",
                    $"Action=ListSubscriptionsByTopic&Version=2010-03-31&TopicArn={Arn}",
                    $"Action=DeleteTopic&Version=2010-03-31&TopicArn={Arn}",
                }, received.Select(request => request.Body));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Contains("\"message_id\": \"95df01b4\"", Outputs("publish"), StringComparison.Ordinal);
                Assert.Contains("\"pending\": true", Outputs("subscribe"), StringComparison.Ordinal);
                Assert.Contains("\"endpoint\": \"ada@example.com\"", Outputs("subscriptions"), StringComparison.Ordinal);
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(secret, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for SNS's query API that re-signs every request with the secret at its own x-amz-date and records whether the signature agreed.</summary>
        private sealed class SnsStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _secret;
            private readonly List<(string Body, bool Verified)> _received = new();

            public SnsStandIn(string secret)
            {
                _secret = secret;
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Body, bool Verified)> Received
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
                if (date is null || authorization is null || request.HttpMethod != "POST" || request.RawUrl != "/" || request.ContentType != "application/x-www-form-urlencoded; charset=utf-8")
                {
                    return false;
                }

                using var again = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{Port}/") { Content = new ByteArrayContent(body) };
                again.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType);
                var when = DateTimeOffset.ParseExact(date, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
                AwsSigV4.Sign(again, body, "eu-north-1", "sns", AccessKey, _secret, null, when);
                return again.Headers.GetValues("Authorization").Single() == authorization;
            }

            private static string Wrapped(string action, string result) =>
                $"<{action}Response xmlns=\"http://sns.amazonaws.com/doc/2010-03-31/\"><{action}Result>{result}</{action}Result><ResponseMetadata><RequestId>r-1</RequestId></ResponseMetadata></{action}Response>";

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

                    var text = Encoding.UTF8.GetString(body);
                    var verified = Verify(context.Request, body);
                    lock (_gate)
                    {
                        _received.Add((text, verified));
                    }

                    var action = text.Split('&').FirstOrDefault(pair => pair.StartsWith("Action=", StringComparison.Ordinal))?["Action=".Length..] ?? "";
                    var (status, answer) = !verified ? (403, "<ErrorResponse><Error><Type>Sender</Type><Code>SignatureDoesNotMatch</Code><Message>The request signature we calculated does not match the signature you provided.</Message></Error></ErrorResponse>")
                        : action switch
                        {
                            "ListTopics" => (200, Wrapped(action, $"<Topics><member><TopicArn>{Orders}</TopicArn></member><member><TopicArn>{Orders}-b</TopicArn></member></Topics>")),
                            "CreateTopic" => (200, Wrapped(action, $"<TopicArn>{Orders}</TopicArn>")),
                            "Publish" => (200, Wrapped(action, "<MessageId>95df01b4</MessageId>")),
                            "Subscribe" => (200, Wrapped(action, "<SubscriptionArn>pending confirmation</SubscriptionArn>")),
                            "ListSubscriptionsByTopic" => (200, Wrapped(action, $"<Subscriptions><member><SubscriptionArn>PendingConfirmation</SubscriptionArn><Owner>123456789012</Owner><Protocol>email</Protocol><Endpoint>ada@example.com</Endpoint><TopicArn>{Orders}</TopicArn></member></Subscriptions>")),
                            "DeleteTopic" => (200, Wrapped(action, "")),
                            _ => (400, "<ErrorResponse><Error><Type>Sender</Type><Code>InvalidAction</Code><Message>Unknown action.</Message></Error></ErrorResponse>"),
                        };
                    context.Response.StatusCode = status;
                    var bytes = Encoding.UTF8.GetBytes(answer);
                    context.Response.ContentType = "text/xml";
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
