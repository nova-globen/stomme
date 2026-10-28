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
    /// BL-229 - Amazon Transcribe DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction
    /// port, against a loopback stand-in for Transcribe that RE-SIGNS every call with the secret at its own time and refuses one
    /// whose signature does not agree, and serves a finished job's transcript at the address it answers with: a Transcribe
    /// connection made over the API and tested; a published pipeline starts a job, reads it once done - its transcript fetched
    /// unsigned - lists the jobs and deletes it; every signature agreed; the secret in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class AwsTranscribeDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string AccessKey = "AKIAKAFEDRIVE";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_transcribes_a_recording_and_reads_its_text()
        {
            var secret = "transcribe-" + Guid.NewGuid().ToString("N");
            using var transcribe = new TranscribeStandIn(secret);
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
                    Name = "Kafé ljud", ConnectorType = "awstranscribe", Endpoint = $"http://127.0.0.1:{transcribe.Port}", TimeoutSeconds = 10,
                    Settings = new() { ["region"] = "eu-west-1" },
                    Credentials = new() { ["access-key"] = AccessKey, ["secret-key"] = secret },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kassa ljud" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("start", "start_job", "job = kassa-1042\nmedia = s3://kafe-ljud/kassa/1042.mp3\nlanguage = sv-SE\nspeakers = 2"),
                        Call("read", "job", "job = kassa-1042"),
                        Call("list", "jobs", "status = completed\nmax = 10"),
                        Call("drop", "delete_job", "job = kassa-1042"),
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
                for (var attempt = 0; attempt < 300 && run?.IsFinished != true; attempt++)
                {
                    var read = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}", output, "the run");
                    run = read.Status == HttpStatusCode.OK ? JsonSerializer.Deserialize<PipelineRunView>(read.Body, Json) : null;
                    await Task.Delay(100);
                }

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.True(run?.SubStatus == "Finished", steps);
                JsonNode Outputs(string id) => JsonNode.Parse(JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>())!;
                Assert.Equal("IN_PROGRESS", Outputs("start")["Result"]![0]!["TranscriptionJobStatus"]!.GetValue<string>());
                Assert.Equal("Kassan är stängd. Åter klockan åtta.", Outputs("read")["Result"]![0]!["transcript"]!.GetValue<string>());
                Assert.Equal("kassa-1042", Outputs("list")["Result"]![0]!["TranscriptionJobName"]!.GetValue<string>());
                Assert.True(Outputs("drop")["Result"]![0]!["deleted"]!.GetValue<bool>());

                var received = transcribe.Received;
                Assert.All(received.Where(request => request.Target.Length > 0), request => Assert.True(request.Verified, "signature refused: " + request.Target));
                var fetched = Assert.Single(received, request => request.Target.Length == 0);
                Assert.False(fetched.Signed); // the transcript's address carries its own authorisation: none of ours sent
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(secret, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for Transcribe's JSON protocol that re-signs every call with the secret at its own x-amz-date, and serves a transcript file unsigned.</summary>
        private sealed class TranscribeStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _secret;
            private readonly List<(string Target, bool Verified, bool Signed)> _received = new();

            public TranscribeStandIn(string secret)
            {
                _secret = secret;
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Target, bool Verified, bool Signed)> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
                }
            }

            public void Dispose() => _listener.Close();

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
                AwsSigV4.Sign(again, body, "eu-west-1", "transcribe", AccessKey, _secret, null, when);
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
                    var transcriptAddress = $"http://127.0.0.1:{Port}/transcripts/kassa-1042.json";
                    (int Status, string Answer) reply;
                    if (target.Length == 0 && context.Request.HttpMethod == "GET" && context.Request.RawUrl == "/transcripts/kassa-1042.json")
                    {
                        lock (_gate)
                        {
                            _received.Add((string.Empty, true, context.Request.Headers["Authorization"] is not null));
                        }

                        reply = (200, """{"jobName":"kassa-1042","results":{"transcripts":[{"transcript":"Kassan är stängd."},{"transcript":"Åter klockan åtta."}]},"status":"COMPLETED"}""");
                    }
                    else
                    {
                        var verified = Verify(context.Request, body) && context.Request.HttpMethod == "POST" && context.Request.RawUrl == "/" && context.Request.ContentType == "application/x-amz-json-1.1";
                        lock (_gate)
                        {
                            _received.Add((target, verified, true));
                        }

                        reply = !verified ? (403, """{"__type":"com.amazon.coral.service#IncompleteSignatureException","message":"The request signature we calculated does not match the signature you provided."}""")
                            : target switch
                            {
                                "Transcribe.ListTranscriptionJobs" => (200, """{"TranscriptionJobSummaries":[{"TranscriptionJobName":"kassa-1042","TranscriptionJobStatus":"COMPLETED","LanguageCode":"sv-SE"}]}"""),
                                "Transcribe.StartTranscriptionJob" => (200, """{"TranscriptionJob":{"TranscriptionJobName":"kassa-1042","TranscriptionJobStatus":"IN_PROGRESS","LanguageCode":"sv-SE"}}"""),
                                "Transcribe.GetTranscriptionJob" => (200, "{\"TranscriptionJob\":{\"TranscriptionJobName\":\"kassa-1042\",\"TranscriptionJobStatus\":\"COMPLETED\",\"LanguageCode\":\"sv-SE\",\"Transcript\":{\"TranscriptFileUri\":\"" + transcriptAddress + "\"}}}"),
                                "Transcribe.DeleteTranscriptionJob" => (200, "{}"),
                                _ => (400, """{"__type":"UnknownOperationException","message":"Unknown action."}"""),
                            };
                    }

                    context.Response.StatusCode = reply.Status;
                    context.Response.ContentType = "application/x-amz-json-1.1";
                    var bytes = Encoding.UTF8.GetBytes(reply.Answer);
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            }
        }
    }
}
