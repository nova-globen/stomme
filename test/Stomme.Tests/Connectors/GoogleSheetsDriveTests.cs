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
    /// Google Sheets DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction
    /// port, against a loopback stand-in for Google's token endpoint and the Sheets API: a Google Sheets connection made over the
    /// API, its client secret and refresh token vaulted, tested (the spreadsheet's title read); a published pipeline reads the
    /// sheet's rows as records and appends one under the headers by name - the stand-in got the refresh grant with the client's id
    /// and secret, then each call with the access token; neither secret is in the pipeline's graph or its run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class GoogleSheetsDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Spreadsheet = "1BxiMVs0XRA5nFMdKvBdBZjgmUUqptlbs74OgvE2upms";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_reads_a_sheets_rows_and_appends_one_through_a_vaulted_refresh_token()
        {
            var secret = "secret-" + Guid.NewGuid().ToString("N");
            var refresh = "refresh-" + Guid.NewGuid().ToString("N");
            using var google = new GoogleStandIn();
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
                    Name = "Orders sheet", ConnectorType = "googlesheets", Endpoint = $"http://127.0.0.1:{google.Port}/v4", TimeoutSeconds = 10,
                    Settings = new() { ["token_url"] = $"http://127.0.0.1:{google.Port}/token", ["spreadsheet"] = Spreadsheet },
                    Credentials = new() { ["client-id"] = "client-1", ["client-secret"] = secret, ["refresh-token"] = refresh },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Orders sheet" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                var graph = new PipelineGraph
                {
                    Variables = { new PipelineGraphVariable { Id = "v-rows", Name = "rows" } },
                    Steps =
                    {
                        new PipelineGraphStep { Id = "read", Type = "Stomme.ExtractFromConnection", Name = "Get rows", Inputs = { ["Connection"] = connection, ["Operation"] = "read", ["Parameters"] = "sheet = Orders" }, OutputVariables = { ["Result"] = "v-rows" } },
                        new PipelineGraphStep { Id = "append", Type = "Stomme.ExtractFromConnection", Name = "Append", Inputs = { ["Connection"] = connection, ["Operation"] = "append", ["Parameters"] = "sheet = Orders\nvalues = {\"Order\": \"1003\", \"Customer\": \"Grace\", \"Note\": \"rush\"}" } },
                    },
                    Connections = { new PipelineGraphConnection { SourceId = "read", SourcePort = "Done", TargetId = "append" } },
                };
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

                // The refresh grant, with the client's id and secret, then each call with the access token it gave.
                var token = google.Received.First(request => request.Path == "/token");
                Assert.Contains("grant_type=refresh_token", token.Body);
                Assert.Contains("client_secret=" + secret, token.Body);
                Assert.All(google.Received.Where(request => request.Path.StartsWith("/v4/", StringComparison.Ordinal)), request => Assert.Equal("Bearer access-drive", request.Authorization));

                // The rows were the read's result - records named by the headers - and the record was appended under them by name.
                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.Contains("Ada", steps);
                Assert.Contains("row_number", steps);
                var append = google.Received.Single(request => request.Path.EndsWith(":append", StringComparison.Ordinal));
                Assert.Equal("""{"values":[["1003","Grace"]]}""", append.Body);
                var appended = JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == "append")!["outputsJson"]!.GetValue<string>();
                Assert.Contains("\"left_out\": \"Note\"", appended); // no header names it: left out, and said

                foreach (var hidden in new[] { secret, refresh })
                {
                    Assert.DoesNotContain(hidden, steps, StringComparison.Ordinal);
                    Assert.DoesNotContain(hidden, (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body, StringComparison.Ordinal);
                }
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for Google's token endpoint and the Sheets API: a sheet 'Orders' of two rows under Order, Customer.</summary>
        private sealed class GoogleStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Method, string Path, string? Authorization, string Body)> _received = new();

            public GoogleStandIn()
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

                    string body;
                    using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                    {
                        body = await reader.ReadToEndAsync();
                    }

                    var path = Uri.UnescapeDataString(context.Request.Url!.AbsolutePath);
                    lock (_gate)
                    {
                        _received.Add((context.Request.HttpMethod, path, context.Request.Headers["Authorization"], body));
                    }

                    var answer = path switch
                    {
                        "/token" => """{"access_token":"access-drive","expires_in":3600,"token_type":"Bearer"}""",
                        _ when path.EndsWith(":append", StringComparison.Ordinal) => """{"updates":{"updatedRange":"Orders!A4:B4","updatedRows":1}}""",
                        _ when path.EndsWith("/values/'Orders'!1:1", StringComparison.Ordinal) => """{"range":"Orders!A1:Z1","values":[["Order","Customer"]]}""",
                        _ when path.EndsWith("/values/Orders", StringComparison.Ordinal) => """{"range":"Orders!A1:B3","values":[["Order","Customer"],["1001","Ada"],["1002","Lin"]]}""",
                        _ when path.EndsWith("/spreadsheets/" + Spreadsheet, StringComparison.Ordinal) => """{"properties":{"title":"Orders 2026"}}""",
                        _ => """{"error":{"code":404,"status":"NOT_FOUND"}}""",
                    };
                    var bytes = Encoding.UTF8.GetBytes(answer);
                    context.Response.StatusCode = answer.Contains("NOT_FOUND", StringComparison.Ordinal) ? 404 : 200;
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
