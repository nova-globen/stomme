#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Models.Pipelines;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// Send through connection on an installed SQLite host (BL-95, 2026-10-03), with the REAL REST connector: a connection to a
    /// loopback server holding a bearer token in the site's secret store, loopback allowed by the Host on the connections page
    /// (BL-180), and a pipeline authored, published and run over the API that posts an event through it. The server received
    /// the POST at the connection's endpoint plus the step's path, with the vaulted token as the bearer and the step's body; the
    /// run finished; and the token is nowhere in the pipeline's graph or its run log - the reason the step exists, since the
    /// HTTP request step keeps its Authorization value in the pipeline.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class SendThroughConnectionDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_posts_through_a_connection_with_the_vaulted_token_and_never_holds_it()
        {
            var token = "tok-" + Guid.NewGuid().ToString("N");
            using var server = new RecordingServer();
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
                var connection = await Send(HttpMethod.Post, "/api/Connections",
                    new ConnectionRequest { Name = "Events API", ConnectorType = "rest", Endpoint = $"http://127.0.0.1:{server.Port}/api", TimeoutSeconds = 10, Credentials = new() { ["token"] = token } }, "the connection");
                Assert.True(connection.Status == HttpStatusCode.OK, connection.Body);
                var connectionId = JsonSerializer.Deserialize<ConnectionView>(connection.Body, Json)!.Id;

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Announce a deploy" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        new PipelineGraphStep { Id = "send", Type = "Stomme.SendThroughConnection", Name = "Send", Inputs = { ["Connection"] = connectionId.ToString(), ["Method"] = "POST", ["Path"] = "api/v1/events", ["Body"] = "{\"event\":\"deployed\"}" } },
                        new PipelineGraphStep { Id = "log", Type = "Stomme.LogMessage", Name = "Log", Inputs = { ["Message"] = "the event was accepted", ["Level"] = "Warning" } },
                    },
                    Connections = { new PipelineGraphConnection { SourceId = "send", SourcePort = "Done", TargetId = "log" } },
                };
                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, $"/api/Pipelines/{definitionId}/graph", graph, "save the graph")).Status);
                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"/api/Pipelines/{definitionId}/publish", new { }, "publish")).Status);
                var started = await Send(HttpMethod.Post, $"/api/Pipelines/{definitionId}/runs", new StartPipelineRequest(), "start");
                var runId = JsonNode.Parse(started.Body)!["runId"]!.GetValue<string>();

                PipelineRunView? run = null;
                for (var attempt = 0; attempt < 150 && run?.IsFinished != true; attempt++)
                {
                    var read = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}", output, "the run");
                    run = read.Status == HttpStatusCode.OK ? JsonSerializer.Deserialize<PipelineRunView>(read.Body, Json) : null;
                    await Task.Delay(100);
                }

                var logBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/log", output, "the run's log")).Body;
                var log = JsonSerializer.Deserialize<List<PipelineRunLogLine>>(logBody, Json)!;
                foreach (var line in log)
                {
                    output.WriteLine($"[log] {line.StepType} {line.EventName} {line.Message}");
                }

                Assert.True(run?.IsFinished, $"the run did not finish: {run?.Status} {run?.SubStatus}");
                Assert.DoesNotContain(log, line => line.EventName is "Failed" or "Faulted");
                Assert.Contains(log, line => line.StepType == "Stomme.LogMessage" && line.EventName == "Warning" && line.Message == "the event was accepted");

                // The remote got the vaulted token as the bearer, at the connection's endpoint plus the step's path, with the body.
                var request = Assert.Single(server.Received);
                Assert.Equal(("POST", "/api/v1/events", "Bearer " + token, "{\"event\":\"deployed\"}"), (request.Method, request.Path, request.Authorization, request.Body));

                // And the pipeline never held it: not in its graph, not in its run's log.
                var stored = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the stored graph");
                Assert.Equal(HttpStatusCode.OK, stored.Status);
                Assert.DoesNotContain(token, stored.Body, StringComparison.Ordinal);
                Assert.DoesNotContain(token, logBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback HTTP server that records each request and answers 201 with a small JSON body.</summary>
        private sealed class RecordingServer : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Method, string Path, string? Authorization, string Body)> _received = new();

            public RecordingServer()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Method, string Path, string? Authorization, string Body)> Received
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

                    using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                    {
                        var body = await reader.ReadToEndAsync();
                        lock (_gate)
                        {
                            _received.Add((context.Request.HttpMethod, context.Request.Url!.AbsolutePath, context.Request.Headers["Authorization"], body));
                        }
                    }

                    var answer = Encoding.UTF8.GetBytes("{\"id\":5}");
                    context.Response.StatusCode = 201;
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(answer);
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
