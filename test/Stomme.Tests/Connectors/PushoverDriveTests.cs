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
    /// Pushover DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for Pushover's API that refuses a call without the token: a Pushover connection made over the
    /// API, its token vaulted, tested; a published pipeline validates a user, pushes an emergency message with an accent, reads
    /// its receipt and cancels it, and lists the sounds; the token in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class PushoverDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string User = "uQiRzpo4DXghDmr9QzzfQu27cmVRsG";
        private const string Receipt = "rLqVuqTRh62UzxtmqiaLzQmVcPgiCy";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_validates_pushes_and_keeps_a_receipt_with_its_token()
        {
            var token = "a" + Guid.NewGuid().ToString("N")[..29];
            using var pushover = new PushoverStandIn(token);
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
                    Name = "Shop alerts", ConnectorType = "pushover", Endpoint = $"http://127.0.0.1:{pushover.Port}/1", TimeoutSeconds = 10,
                    Credentials = new() { ["api-key"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Fika alarm" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("validate", "Validate", "validate_user", $"user = {User}"),
                        Call("push", "Push", "push", $"user = {User}\nmessage = Kafé is on fire\ntitle = Fika\npriority = 2\nretry = 60\nexpire = 600"),
                        Call("receipt", "Receipt", "receipt", $"receipt = {Receipt}"),
                        Call("cancel", "Cancel", "cancel_receipt", $"receipt = {Receipt}"),
                        Call("sounds", "Sounds", "sounds", ""),
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
                var received = pushover.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "no token: " + request.Line));
                Assert.Equal(new[]
                {
                    "GET /1/apps/limits.json",
                    $"POST /1/users/validate.json user={User}",
                    $"POST /1/messages.json user={User}&message=Kaf%C3%A9+is+on+fire&title=Fika&priority=2&retry=60&expire=600",
                    $"GET /1/receipts/{Receipt}.json",
                    $"POST /1/receipts/{Receipt}/cancel.json",
                    "GET /1/sounds.json",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal("[\"iphone\"]", JsonNode.Parse(Outputs("validate"))!["Result"]![0]!["devices"]!.GetValue<string>());
                Assert.Equal(Receipt, JsonNode.Parse(Outputs("push"))!["Result"]![0]!["receipt"]!.GetValue<string>());
                Assert.Equal(0, JsonNode.Parse(Outputs("receipt"))!["Result"]![0]!["acknowledged"]!.GetValue<long>());
                Assert.Equal(1, JsonNode.Parse(Outputs("cancel"))!["Result"]![0]!["status"]!.GetValue<long>());
                Assert.Equal(2, JsonNode.Parse(Outputs("sounds"))!["RecordCount"]!.GetValue<int>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for Pushover's API that answers only a call carrying the token - in a POST's form or a GET's query.</summary>
        private sealed class PushoverStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _token;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public PushoverStandIn(string token)
            {
                _token = token;
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
                    var authorized = method == "GET" ? raw.EndsWith("?token=" + _token, StringComparison.Ordinal) : body.StartsWith("token=" + _token, StringComparison.Ordinal);
                    var rest = method == "GET" ? "" : body.Length > ("token=" + _token).Length ? " " + body[("token=" + _token + "&").Length..] : "";
                    lock (_gate)
                    {
                        _received.Add(($"{method} {path}{rest}", authorized));
                    }

                    var (status, answer) = !authorized ? (400, "{\"token\":\"invalid\",\"errors\":[\"application token is invalid\"],\"status\":0}")
                        : (method, path) switch
                        {
                            ("GET", "/1/apps/limits.json") => (200, "{\"limit\":10000,\"remaining\":9990,\"reset\":1761955200,\"status\":1}"),
                            ("POST", "/1/users/validate.json") => (200, "{\"status\":1,\"group\":0,\"devices\":[\"iphone\"],\"licenses\":[\"iOS\"]}"),
                            ("POST", "/1/messages.json") => (200, "{\"status\":1,\"request\":\"r2\",\"receipt\":\"" + Receipt + "\"}"),
                            ("GET", "/1/receipts/" + Receipt + ".json") => (200, "{\"status\":1,\"acknowledged\":0,\"expired\":0}"),
                            ("POST", "/1/receipts/" + Receipt + "/cancel.json") => (200, "{\"status\":1,\"request\":\"r3\"}"),
                            ("GET", "/1/sounds.json") => (200, "{\"sounds\":{\"pushover\":\"Pushover (default)\",\"bike\":\"Bike\"},\"status\":1}"),
                            _ => (404, "{\"errors\":[\"not found\"],\"status\":0}"),
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
