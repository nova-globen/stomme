#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Models.Pipelines;
using Stomme.Tests.Pipelines;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// Vonage DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for Vonage's SMS and Account APIs that refuses a call without the key and secret as Basic: a
    /// Vonage connection made over the API, its secret vaulted, tested by the balance; a published pipeline sending an SMS over
    /// several lines from the step's Query box, then a second to the number the first answered - an expression over its result -
    /// as Unicode, its text needing it; the secret in neither the graph, nor the run, nor any form sent.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class VonageDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Key = "kafe-key";
        private const string Secret = "vonage-drive-secret";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        /// <summary>A loopback stand-in for Vonage: the key and secret as Basic or a 401, every form sent kept, decoded.</summary>
        private sealed class VonageStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private int _sent;

            public VonageStandIn()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public ConcurrentQueue<string> Asked { get; } = new();

            public ConcurrentQueue<Dictionary<string, string>> Forms { get; } = new();

            public void Dispose() => _listener.Close();

            private async Task ServeAsync()
            {
                var basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Key}:{Secret}"));
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

                    var path = context.Request.RawUrl ?? "/";
                    Asked.Enqueue($"{context.Request.HttpMethod} {path}");
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    var (status, answer) = (context.Request.HttpMethod, path) switch
                    {
                        _ when context.Request.Headers["Authorization"] != basic => (401, "{\"type\":\"https://developer.vonage.com/api-errors#unauthorized\",\"title\":\"Unauthorized\"}"),
                        ("GET", "/account/get-balance") => (200, "{\"value\":10.28,\"autoReload\":false}"),
                        ("POST", "/sms/json") => (200, Sent(body)),
                        _ => (404, "{\"title\":\"Not found\"}"),
                    };
                    context.Response.StatusCode = status;
                    context.Response.ContentType = "application/json";
                    var bytes = Encoding.UTF8.GetBytes(answer);
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            }

            private string Sent(string body)
            {
                var form = body.Split('&').Select(pair => pair.Split('=', 2)).ToDictionary(pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')), pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')), StringComparer.Ordinal);
                Forms.Enqueue(form);
                var id = $"0A00000{Interlocked.Increment(ref _sent)}";
                return $"{{\"message-count\":\"1\",\"messages\":[{{\"to\":\"{form.GetValueOrDefault("to")}\",\"message-id\":\"{id}\",\"status\":\"0\",\"remaining-balance\":\"10.24\",\"message-price\":\"0.0412\",\"network\":\"24001\"}}]}}";
            }
        }

        [Fact]
        public async Task A_pipeline_sends_two_texts_through_vonage()
        {
            using var vonage = new VonageStandIn();
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
                    Name = "Kassans SMS", ConnectorType = "vonage", Endpoint = $"http://127.0.0.1:{vonage.Port}", TimeoutSeconds = 10,
                    Settings = new() { ["from"] = "Kassan" },
                    Credentials = new() { ["username"] = Key, ["password"] = Secret },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kassans kvitton" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = "send" } };
                var first = Call("first");
                first.Inputs["Parameters"] = "to = +46 70-174 06 05\nttl = 60";
                first.Inputs["Query"] = "Hej!\nKaffet är klart.";
                first.OutputVariables["Result"] = "v-sent";
                var second = Call("second");
                second.InputExpressions["Parameters"] = "to = {{ sent[0].to }}\ntext = Tack ☕\nclient_ref = {{ sent[0].to }}";
                var graph = new PipelineGraph { Variables = { new PipelineGraphVariable { Id = "v-sent", Name = "sent" } }, Steps = { first, second } };
                graph.Connections.Add(new PipelineGraphConnection { SourceId = "first", SourcePort = "Done", TargetId = "second" });

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
                Assert.Equal("0A000001", Outputs("first")["Result"]![0]!["message-id"]!.GetValue<string>());
                Assert.Equal("0A000002", Outputs("second")["Result"]![0]!["message-id"]!.GetValue<string>());

                var forms = vonage.Forms.ToArray();
                Assert.Equal(2, forms.Length);
                Assert.Equal(("Kassan", "46701740605", "Hej!\nKaffet är klart.", "text", "3600000"), (forms[0]["from"], forms[0]["to"], forms[0]["text"], forms[0]["type"], forms[0]["ttl"]));
                Assert.Equal(("Kassan", "46701740605", "Tack ☕", "unicode", "46701740605"), (forms[1]["from"], forms[1]["to"], forms[1]["text"], forms[1]["type"], forms[1]["client-ref"]));
                Assert.Equal(new[] { "GET /account/get-balance", "POST /sms/json", "POST /sms/json" }, vonage.Asked);
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(Secret, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(Secret, graphBody, StringComparison.Ordinal);
                Assert.DoesNotContain(forms, form => form.Values.Any(value => value.Contains(Secret, StringComparison.Ordinal)) || form.ContainsKey("api_secret"));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
