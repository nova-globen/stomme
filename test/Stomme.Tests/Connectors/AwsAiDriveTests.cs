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
    /// Amazon Textract, Rekognition and Comprehend DRIVEN on an installed SQLite host, with the REAL connectors, the host's
    /// egress and the extraction port, against a loopback stand-in for the three services that re-signs every request with the
    /// secret at its own time and its own service: a connection to each made over the API and tested; a published pipeline reads a
    /// note's sentiment from the step's Query box, an S3 image's labels and an S3 receipt's fields; every signature agreed; the
    /// secret in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class AwsAiDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string AccessKey = "AKIAKAFEDRIVE";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_reads_a_notes_sentiment_an_images_labels_and_a_receipts_fields()
        {
            var secret = "s" + Guid.NewGuid().ToString("N");
            using var aws = new AiStandIn(secret);
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
                async Task<string> Connect(string type, string name)
                {
                    var made = await Send(HttpMethod.Post, "/api/Connections", new ConnectionRequest
                    {
                        Name = name, ConnectorType = type, Endpoint = $"http://127.0.0.1:{aws.Port}", TimeoutSeconds = 10,
                        Settings = new() { ["region"] = "eu-west-1" },
                        Credentials = new() { ["access-key"] = AccessKey, ["secret-key"] = secret },
                    }, "the " + type + " connection");
                    Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                    var id = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                    var tested = await Send(HttpMethod.Post, $"/api/Connections/{id}/test", new { }, "test " + type);
                    Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);
                    return id;
                }

                var comprehend = await Connect("awscomprehend", "Kassa text");
                var rekognition = await Connect("awsrekognition", "Kassa foton");
                var textract = await Connect("awstextract", "Kassa kvitton");

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kassa AI" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string connection, string operation, string parameters, string? query = null)
                {
                    var step = new PipelineGraphStep { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                    if (query is not null)
                    {
                        step.Inputs["Query"] = query;
                    }

                    return step;
                }

                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("mood", comprehend, "detect_sentiment", "language = en", "Lovely coffee\nfriendly staff"),
                        Call("labels", rekognition, "detect_labels", "bucket = kafe-foton\nobject = kassa.jpg\nmax = 5"),
                        Call("receipt", textract, "analyze_expense", "bucket = kafe-kvitton\nobject = 2026/1042.png"),
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

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.True(run?.SubStatus == "Finished", steps);
                var received = aws.Received;
                Assert.All(received, request => Assert.True(request.Verified, "signature refused: " + request.Line));
                Assert.Equal(new[]
                {
                    "Comprehend_20171127.DetectDominantLanguage {\"Text\":\"Hej\"}",
                    "RekognitionService.ListCollections {\"MaxResults\":1}",
                    "Textract.DetectDocumentText {\"Document\":{\"Bytes\":\"\"}}",
                    "Comprehend_20171127.DetectSentiment {\"Text\":\"Lovely coffee\\nfriendly staff\",\"LanguageCode\":\"en\"}",
                    "RekognitionService.DetectLabels {\"Image\":{\"S3Object\":{\"Bucket\":\"kafe-foton\",\"Name\":\"kassa.jpg\"}},\"MaxLabels\":5}",
                    "Textract.AnalyzeExpense {\"Document\":{\"S3Object\":{\"Bucket\":\"kafe-kvitton\",\"Name\":\"2026/1042.png\"}}}",
                }, received.Select(request => request.Line));

                JsonNode Outputs(string id) => JsonNode.Parse(JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>())!;
                Assert.Equal("POSITIVE", Outputs("mood")["Result"]![0]!["sentiment"]!.GetValue<string>());
                Assert.Equal("Cup", Outputs("labels")["Result"]![0]!["name"]!.GetValue<string>());
                var receipt = Outputs("receipt")["Result"]![0]!;
                Assert.Equal(("Kafé Ada", "35,00"), (receipt["VENDOR_NAME"]!.GetValue<string>(), receipt["TOTAL"]!.GetValue<string>()));
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(secret, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for the three services' JSON protocol that re-signs every request with the secret at its own x-amz-date, for the service its target names, and records whether the signature agreed.</summary>
        private sealed class AiStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _secret;
            private readonly List<(string Line, bool Verified)> _received = new();

            public AiStandIn(string secret)
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

            private bool Verify(HttpListenerRequest request, byte[] body, string service)
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
                AwsSigV4.Sign(again, body, "eu-west-1", service, AccessKey, _secret, null, when);
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
                    var service = target.StartsWith("Comprehend_", StringComparison.Ordinal) ? "comprehend" : target.StartsWith("Rekognition", StringComparison.Ordinal) ? "rekognition" : "textract";
                    var verified = Verify(context.Request, body, service) && context.Request.HttpMethod == "POST" && context.Request.RawUrl == "/" && context.Request.ContentType == "application/x-amz-json-1.1";
                    lock (_gate)
                    {
                        _received.Add(($"{target} {Encoding.UTF8.GetString(body)}", verified));
                    }

                    var (status, answer) = !verified ? (403, """{"__type":"com.amazon.coral.service#IncompleteSignatureException","message":"The request signature we calculated does not match the signature you provided."}""")
                        : target switch
                        {
                            "Comprehend_20171127.DetectDominantLanguage" => (200, """{"Languages":[{"LanguageCode":"sv","Score":0.9}]}"""),
                            "RekognitionService.ListCollections" => (200, """{"CollectionIds":[]}"""),
                            "Textract.DetectDocumentText" => (400, """{"__type":"com.amazonaws.textract#InvalidParameterException","message":"Request has invalid parameters"}"""),
                            "Comprehend_20171127.DetectSentiment" => (200, """{"Sentiment":"POSITIVE","SentimentScore":{"Positive":0.95,"Negative":0.01,"Neutral":0.03,"Mixed":0.01}}"""),
                            "RekognitionService.DetectLabels" => (200, """{"Labels":[{"Name":"Cup","Confidence":97.5}]}"""),
                            "Textract.AnalyzeExpense" => (200, """{"ExpenseDocuments":[{"SummaryFields":[{"Type":{"Text":"VENDOR_NAME"},"ValueDetection":{"Text":"Kafé Ada"}},{"Type":{"Text":"TOTAL"},"ValueDetection":{"Text":"35,00"}}]}]}"""),
                            _ => (400, """{"__type":"UnknownOperationException","message":"Unknown action."}"""),
                        };
                    context.Response.StatusCode = status;
                    var bytes = Encoding.UTF8.GetBytes(answer);
                    context.Response.ContentType = "application/x-amz-json-1.1";
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
