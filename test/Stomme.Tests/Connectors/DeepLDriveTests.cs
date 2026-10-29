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
    /// DeepL DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for DeepL's API v2 that refuses a call without the key in DeepL's header: a DeepL connection
    /// made over the API, its key vaulted, tested; a published pipeline translates a text with an accent from British English,
    /// sixty texts in two calls, and reads the target languages, the usage and the glossaries; the key in neither the graph nor
    /// the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class DeepLDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_translates_a_text_and_a_list_and_reads_the_languages_usage_and_glossaries_with_its_key()
        {
            var key = "dl" + Guid.NewGuid().ToString("N") + ":fx";
            using var deepL = new DeepLStandIn(key);
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
                    Name = "Shop translations", ConnectorType = "deepl", Endpoint = $"http://127.0.0.1:{deepL.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["api-key"] = key },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Translations" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var texts = "[" + string.Join(", ", Enumerable.Range(1, 60).Select(i => "\"Coffee " + i + "\"")) + "]";
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("one", "Sign", "translate", "text = The café is closed\ntarget = SV\nsource = EN-GB\nformality = less"),
                        Call("many", "Menu", "translate", "texts = " + texts + "\ntarget = DE"),
                        Call("languages", "Languages", "languages", ""),
                        Call("usage", "Usage", "usage", ""),
                        Call("glossaries", "Glossaries", "glossaries", ""),
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
                var received = deepL.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "no key: " + request.Line));
                Assert.Equal(new[]
                {
                    "GET /v2/usage",
                    "POST /v2/translate {\"target_lang\":\"SV\",\"source_lang\":\"EN\",\"formality\":\"less\",\"text\":[\"The café is closed\"]}",
                    "POST /v2/translate 50",
                    "POST /v2/translate 10",
                    "GET /v2/languages?type=target",
                    "GET /v2/usage",
                    "GET /v2/glossaries",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal("Kaféet är stängt", JsonNode.Parse(Outputs("one"))!["Result"]![0]!["text"]!.GetValue<string>());
                Assert.Equal(60, JsonNode.Parse(Outputs("many"))!["RecordCount"]!.GetValue<int>());
                Assert.Equal("SV", JsonNode.Parse(Outputs("languages"))!["Result"]![0]!["language"]!.GetValue<string>());
                Assert.Equal(42L, JsonNode.Parse(Outputs("usage"))!["Result"]![0]!["character_count"]!.GetValue<long>());
                Assert.Equal("Café words", JsonNode.Parse(Outputs("glossaries"))!["Result"]![0]!["name"]!.GetValue<string>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(key, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(key, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for DeepL's API v2 that answers only a call carrying the key in DeepL's header.</summary>
        private sealed class DeepLStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _key;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public DeepLStandIn(string key)
            {
                _key = key;
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
                    var authorized = context.Request.Headers["Authorization"] == "DeepL-Auth-Key " + _key;
                    var texts = body.Length == 0 ? null : JsonNode.Parse(body)!["text"]!.AsArray().Select(text => text!.GetValue<string>()).ToList();

                    // A long list logged by its count; one text by its whole body.
                    lock (_gate)
                    {
                        _received.Add(($"{method} {raw}{(texts is null ? "" : " " + (texts.Count > 1 ? texts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) : body))}", authorized));
                    }

                    var (status, answer) = !authorized ? (403, "{\"message\":\"Authorization failure, check auth_key\"}")
                        : (method, raw.Split('?')[0]) switch
                        {
                            ("GET", "/v2/usage") => (200, "{\"character_count\":42,\"character_limit\":500000}"),
                            ("POST", "/v2/translate") => (200, "{\"translations\":[" + string.Join(",", texts!.Select(text => "{\"detected_source_language\":\"EN\",\"text\":\"" + (text == "The café is closed" ? "Kaféet är stängt" : "Kaffee " + text[7..]) + "\"}")) + "]}"),
                            ("GET", "/v2/languages") => (200, "[{\"language\":\"SV\",\"name\":\"Swedish\",\"supports_formality\":false},{\"language\":\"DE\",\"name\":\"German\",\"supports_formality\":true}]"),
                            ("GET", "/v2/glossaries") => (200, "{\"glossaries\":[{\"glossary_id\":\"def3a26b-3e84-45b3-84ae-0c0aaf3525f7\",\"name\":\"Café words\",\"ready\":true,\"source_lang\":\"en\",\"target_lang\":\"sv\",\"entry_count\":3}]}"),
                            _ => (404, "{\"message\":\"Not found\"}"),
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
