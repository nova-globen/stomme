#nullable enable
using System;
using System.Collections.Concurrent;
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
    /// BL-229 - OpenWeatherMap DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction
    /// port, against a loopback stand-in for OpenWeatherMap's API 2.5 that refuses a call without its key: a connection made over
    /// the API, its key vaulted, tested; a published pipeline reading Göteborg's weather now and a day of its forecast, eight
    /// three-hour records; the key in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class OpenWeatherMapDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Key = "owm-drive-key";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        /// <summary>A loopback stand-in for OpenWeatherMap's API 2.5: its key in the address or a 401, every address asked kept.</summary>
        private sealed class WeatherStandIn : IDisposable
        {
            private readonly HttpListener _listener;

            public WeatherStandIn()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public ConcurrentQueue<string> Asked { get; } = new();

            public void Dispose() => _listener.Close();

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

                    var path = context.Request.RawUrl ?? "/";
                    Asked.Enqueue(path);
                    var (status, answer) = (context.Request.QueryString["appid"], path.Split('?')[0]) switch
                    {
                        (not Key, _) => (401, "{\"cod\":401,\"message\":\"Invalid API key.\"}"),
                        (_, "/data/2.5/weather") => (200, "{\"weather\":[{\"description\":\"lätt regn\"}],\"main\":{\"temp\":8.4,\"humidity\":87},\"wind\":{\"speed\":5.1},\"name\":\"Göteborg\"}"),
                        (_, "/data/2.5/forecast") => (200, "{\"cnt\":40,\"list\":[" + string.Join(",", Enumerable.Range(0, 40).Select(i => $"{{\"dt\":{1792400000 + i * 10800},\"main\":{{\"temp\":{7 + i}}}}}")) + "],\"city\":{\"name\":\"Göteborg\",\"country\":\"SE\"}}"),
                        _ => (404, "{\"cod\":\"404\",\"message\":\"Internal error\"}"),
                    };
                    context.Response.StatusCode = status;
                    context.Response.ContentType = "application/json";
                    var bytes = Encoding.UTF8.GetBytes(answer);
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            }
        }

        [Fact]
        public async Task A_pipeline_reads_the_weather_now_and_a_days_forecast()
        {
            using var weather = new WeatherStandIn();
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
                    Name = "Kafé väder", ConnectorType = "openweathermap", Endpoint = $"http://127.0.0.1:{weather.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["api-key"] = Key },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kassans väder" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps = { Call("now", "current", "city = Göteborg,se\nlanguage = sv"), Call("day", "forecast", "city = Göteborg,se\nmax = 8") },
                    Connections = { new PipelineGraphConnection { SourceId = "now", SourcePort = "Done", TargetId = "day" } },
                };
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
                var now = Outputs("now")["Result"]![0]!;
                Assert.Equal(("Göteborg", 8.4, "lätt regn"), (now["name"]!.GetValue<string>(), now["temperature"]!.GetValue<double>(), now["description"]!.GetValue<string>()));
                var day = Outputs("day")["Result"]!.AsArray();
                Assert.Equal(8, day.Count);
                Assert.Equal(("Göteborg", 14.0), (day[7]!["city"]!.GetValue<string>(), day[7]!["temperature"]!.GetValue<double>()));
                Assert.Contains(weather.Asked, path => path.StartsWith("/data/2.5/forecast?", StringComparison.Ordinal) && path.Contains("cnt=8", StringComparison.Ordinal));
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(Key, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(Key, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
