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
    /// BL-229 - Amazon DynamoDB DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction
    /// port, against a loopback stand-in for DynamoDB's JSON protocol that checks every request's signature as AWS would - its
    /// target signed with it, re-signed with the key's secret at the request's own time and refused when it differs: an Amazon
    /// DynamoDB connection made over the API, its key pair vaulted, tested; a published pipeline puts an item with an accent, a
    /// number, a flag and a list on a condition, gets it, updates it, queries a customer's orders over two pages and deletes the
    /// item; every item plain JSON in the run, typed on the wire; the secret in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class AwsDynamoDbDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string AccessKey = "AKIADRIVEEXAMPLE";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_puts_gets_updates_queries_and_deletes_signed_as_aws_checks()
        {
            var secret = "s" + Guid.NewGuid().ToString("N");
            using var dynamo = new DynamoStandIn(secret);
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
                    Name = "Shop tables", ConnectorType = "awsdynamodb", Endpoint = $"http://127.0.0.1:{dynamo.Port}", TimeoutSeconds = 10,
                    Settings = new() { ["region"] = "eu-north-1" },
                    Credentials = new() { ["access-key"] = AccessKey, ["secret-key"] = secret },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Tables" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("put", "Put", "put_item", "table = Orders\nitem = {\"pk\": \"order#1042\", \"customer\": \"Café Ada\", \"total\": 99.50, \"paid\": false, \"lines\": [\"fika\", \"bulle\"]}\ncondition = attribute_not_exists(pk)"),
                        Call("get", "Get", "get_item", "table = Orders\nkey = {\"pk\": \"order#1042\"}\nconsistent = true"),
                        Call("update", "Update", "update_item", "table = Orders\nkey = {\"pk\": \"order#1042\"}\nupdate = SET paid = :p\nvalues = {\":p\": true}"),
                        Call("query", "Query", "query", "table = Orders\nindex = by-customer\nkey_condition = customer = :c\nvalues = {\":c\": \"Café Ada\"}\nmax = 3"),
                        Call("delete", "Delete", "delete_item", "table = Orders\nkey = {\"pk\": \"order#1042\"}"),
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
                var received = dynamo.Received;
                Assert.All(received, request => Assert.True(request.Verified, "signature refused: " + request.Line));
                const string Key = "{\"pk\":{\"S\":\"order#1042\"}}";
                Assert.Equal(new[]
                {
                    "DynamoDB_20120810.ListTables {\"Limit\":100}",
                    "DynamoDB_20120810.PutItem {\"TableName\":\"Orders\",\"Item\":{\"pk\":{\"S\":\"order#1042\"},\"customer\":{\"S\":\"Café Ada\"},\"total\":{\"N\":\"99.50\"},\"paid\":{\"BOOL\":false},\"lines\":{\"L\":[{\"S\":\"fika\"},{\"S\":\"bulle\"}]}},\"ConditionExpression\":\"attribute_not_exists(pk)\",\"ReturnValues\":\"ALL_OLD\"}",
                    "DynamoDB_20120810.GetItem {\"TableName\":\"Orders\",\"Key\":" + Key + ",\"ConsistentRead\":true}",
                    "DynamoDB_20120810.UpdateItem {\"TableName\":\"Orders\",\"Key\":" + Key + ",\"UpdateExpression\":\"SET paid = :p\",\"ExpressionAttributeValues\":{\":p\":{\"BOOL\":true}},\"ReturnValues\":\"ALL_NEW\"}",
                    "DynamoDB_20120810.Query {\"TableName\":\"Orders\",\"KeyConditionExpression\":\"customer = :c\",\"ExpressionAttributeValues\":{\":c\":{\"S\":\"Café Ada\"}},\"IndexName\":\"by-customer\",\"Limit\":3}",
                    "DynamoDB_20120810.Query {\"TableName\":\"Orders\",\"KeyConditionExpression\":\"customer = :c\",\"ExpressionAttributeValues\":{\":c\":{\"S\":\"Café Ada\"}},\"IndexName\":\"by-customer\",\"Limit\":1,\"ExclusiveStartKey\":{\"pk\":{\"S\":\"order#1041\"}}}",
                    "DynamoDB_20120810.DeleteItem {\"TableName\":\"Orders\",\"Key\":" + Key + ",\"ReturnValues\":\"ALL_OLD\"}",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                JsonNode Result(string id) => JsonNode.Parse(Outputs(id))!["Result"]![0]!;
                Assert.Equal(("Café Ada", "99.50", false, "[\"fika\",\"bulle\"]"), (Result("get")["customer"]!.GetValue<string>(), Result("get")["total"]!.ToJsonString(), Result("get")["paid"]!.GetValue<bool>(), Result("get")["lines"]!.GetValue<string>())); // plain JSON again, the number as written
                Assert.True(Result("update")["paid"]!.GetValue<bool>());
                Assert.Equal(3, JsonNode.Parse(Outputs("query"))!["RecordCount"]!.GetValue<int>()); // both pages, no more than asked
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(secret, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for DynamoDB's JSON protocol that re-signs every request with the secret at its own x-amz-date and records whether the signature agreed.</summary>
        private sealed class DynamoStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _secret;
            private readonly List<(string Line, bool Verified)> _received = new();

            public DynamoStandIn(string secret)
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

            // The signature as AWS would compute it for this request: the same method, address, target, content type and body, at
            // the request's own time.
            private bool Verify(HttpListenerRequest request, byte[] body)
            {
                var date = request.Headers["x-amz-date"];
                var authorization = request.Headers["Authorization"];
                var target = request.Headers["X-Amz-Target"];
                if (date is null || authorization is null || target is null || request.HttpMethod != "POST" || request.RawUrl != "/" || request.ContentType != "application/x-amz-json-1.0")
                {
                    return false;
                }

                using var again = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{Port}/") { Content = new ByteArrayContent(body) };
                again.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType);
                again.Headers.TryAddWithoutValidation("X-Amz-Target", target);
                var when = DateTimeOffset.ParseExact(date, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
                AwsSigV4.Sign(again, body, "eu-north-1", "dynamodb", AccessKey, _secret, null, when);
                return again.Headers.GetValues("Authorization").Single() == authorization;
            }

            private async Task ServeAsync()
            {
                const string Item = """{"pk":{"S":"order#1042"},"customer":{"S":"Café Ada"},"total":{"N":"99.50"},"paid":{"BOOL":false},"lines":{"L":[{"S":"fika"},{"S":"bulle"}]}}""";
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
                    var text = Encoding.UTF8.GetString(body);
                    var verified = Verify(context.Request, body);
                    lock (_gate)
                    {
                        _received.Add(($"{target} {text}", verified));
                    }

                    var (status, answer) = !verified ? (400, """{"__type":"com.amazon.coral.service#InvalidSignatureException","message":"The request signature we calculated does not match the signature you provided."}""")
                        : target switch
                        {
                            "DynamoDB_20120810.ListTables" => (200, """{"TableNames":["Orders"]}"""),
                            "DynamoDB_20120810.PutItem" => (200, "{}"),
                            "DynamoDB_20120810.GetItem" => (200, "{\"Item\":" + Item + "}"),
                            "DynamoDB_20120810.UpdateItem" => (200, "{\"Attributes\":" + Item.Replace("\"BOOL\":false", "\"BOOL\":true", StringComparison.Ordinal) + "}"),
                            "DynamoDB_20120810.Query" => (200, text.Contains("ExclusiveStartKey", StringComparison.Ordinal)
                                ? """{"Items":[{"pk":{"S":"order#1040"}}],"Count":1}"""
                                : """{"Items":[{"pk":{"S":"order#1042"}},{"pk":{"S":"order#1041"}}],"Count":2,"LastEvaluatedKey":{"pk":{"S":"order#1041"}}}"""),
                            "DynamoDB_20120810.DeleteItem" => (200, "{\"Attributes\":" + Item + "}"),
                            _ => (400, """{"__type":"com.amazon.coral.service#UnknownOperationException","message":"Unknown operation."}"""),
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
