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
    /// BL-229 - Microsoft Excel 365 DRIVEN on an installed SQLite host, with the REAL connector, the host's egress, the shared OAuth2
    /// token provider and the extraction port, against a loopback stand-in for the tenant's token endpoint and Graph: a Microsoft
    /// Excel 365 connection made over the API, its secret vaulted, tested (the drive named); a published pipeline reads a sheet's
    /// rows under its headers, appends a record after the last used row, changes a row it finds by a column - a formula it leaves
    /// alone kept - reads a table's rows over two pages and looks one up - an app-only token, every Graph call its bearer; the
    /// secret in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class MicrosoftExcelDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_reads_appends_updates_and_looks_up_rows()
        {
            var secret = "s" + Guid.NewGuid().ToString("N");
            using var microsoft = new GraphStandIn();
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
                    Name = "Sales books", ConnectorType = "microsoftexcel", Endpoint = $"http://127.0.0.1:{microsoft.Port}/v1.0", TimeoutSeconds = 10,
                    Settings = new() { ["user"] = "ada@contoso.com" },
                    Credentials = new() { ["token-url"] = $"http://127.0.0.1:{microsoft.Port}/tenant/oauth2/v2.0/token", ["client-id"] = "app-1", ["client-secret"] = secret },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Sales" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("read", "Orders", "read_rows", "workbook = /Reports/Sales.xlsx\nsheet = Orders"),
                        Call("append", "New order", "append_rows", "workbook = /Reports/Sales.xlsx\nsheet = Orders\nvalues = {\"Id\": 3, \"Name\": \"Café <b>Grace</b>\", \"Extra\": 1}"),
                        Call("update", "Paid", "update_rows", "workbook = /Reports/Sales.xlsx\nsheet = Orders\nmatch_column = Id\nvalues = {\"Id\": 2, \"Status\": \"Paid\"}"),
                        Call("lines", "Lines", "table_rows", "workbook = /Reports/Sales.xlsx\ntable = Lines\nmax = 130"),
                        Call("find", "Find", "lookup", "workbook = /Reports/Sales.xlsx\ntable = Lines\ncolumn = Sku\nvalue = S-105"),
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
                var received = microsoft.Received;
                var token = received.First(request => request.Path == "/tenant/oauth2/v2.0/token").Body;
                Assert.Contains("grant_type=client_credentials", token);
                Assert.All(received.Where(request => request.Path.StartsWith("/v1.0/", StringComparison.Ordinal)), request => Assert.Equal("Bearer access-drive", request.Authorization));
                const string book = "/v1.0/users/ada%40contoso.com/drive/root:/Reports/Sales.xlsx:/workbook";
                Assert.Equal(new[]
                {
                    "GET /v1.0/users/ada%40contoso.com/drive?$select=name,driveType",
                    "GET " + book + "/worksheets/Orders/usedRange(valuesOnly=true)?$select=address,values",
                    "GET " + book + "/worksheets/Orders/usedRange(valuesOnly=true)?$select=address,values",
                    "PATCH " + book + "/worksheets/Orders/range(address='A4:C4') {\"values\":[[3,\"Café <b>Grace</b>\",\"\"]]}",
                    "GET " + book + "/worksheets/Orders/usedRange(valuesOnly=true)?$select=address,values,formulas",
                    "PATCH " + book + "/worksheets/Orders/range(address='A3:C3') {\"formulas\":[[2,\"Lin\",\"Paid\"]]}",
                    "GET " + book + "/tables/Lines/columns?$select=id,name,index",
                    "GET " + book + "/tables/Lines/rows?$top=100&$skip=0",
                    "GET " + book + "/tables/Lines/rows?$top=30&$skip=100",
                    "GET " + book + "/tables/Lines/columns?$select=id,name,index",
                    "GET " + book + "/tables/Lines/rows?$top=100&$skip=0",
                    "GET " + book + "/tables/Lines/rows?$top=100&$skip=100",
                }, received.Where(request => request.Path.StartsWith("/v1.0/", StringComparison.Ordinal)).Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Out(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Contains("\"RecordCount\": 2", Out("read"));
                Assert.Contains("\"RecordCount\": 130", Out("lines")); // both pages, up to the most asked for
                Assert.Contains("\"RecordCount\": 1", Out("find"));
                Assert.Contains("S-105", Out("find"));
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(secret, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for the tenant's token endpoint and Graph: a drive, a sheet of orders, a table of lines over two pages.</summary>
        private sealed class GraphStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Path, string Line, string Body, string? Authorization)> _received = new();

            public GraphStandIn()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Path, string Line, string Body, string? Authorization)> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
                }
            }

            private static string Lines(int from, int count) =>
                "{\"value\":[" + string.Join(",", Enumerable.Range(from, count).Select(n => "{\"index\":" + n + ",\"values\":[[\"S-" + n + "\"," + n + "]]}")) + "]}";

            private async Task ServeAsync()
            {
                const string book = "/v1.0/users/ada%40contoso.com/drive/root:/Reports/Sales.xlsx:/workbook";
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

                    var url = context.Request.Url!;
                    var path = url.AbsolutePath;
                    var query = url.Query;
                    lock (_gate)
                    {
                        _received.Add((path, $"{context.Request.HttpMethod} {url.PathAndQuery}{(body.Length == 0 || path.EndsWith("/token", StringComparison.Ordinal) ? "" : " " + body)}", body, context.Request.Headers["Authorization"]));
                    }

                    var (status, answer) = (context.Request.HttpMethod, path) switch
                    {
                        ("POST", "/tenant/oauth2/v2.0/token") => (200, """{"access_token":"access-drive","expires_in":3600,"token_type":"Bearer"}"""),
                        ("GET", "/v1.0/users/ada%40contoso.com/drive") => (200, """{"name":"OneDrive","driveType":"business"}"""),
                        ("GET", book + "/worksheets/Orders/usedRange(valuesOnly=true)") => (200, """{"address":"Orders!A1:C3","values":[["Id","Name","Status"],[1,"Ada","Open"],[2,"Lin","Open"]],"formulas":[["Id","Name","Status"],[1,"Ada","Open"],[2,"Lin","Open"]]}"""),
                        ("PATCH", _) when path.StartsWith(book + "/worksheets/Orders/range", StringComparison.Ordinal) => (200, """{"address":"Orders!A4:C4"}"""),
                        ("GET", book + "/tables/Lines/columns") => (200, """{"value":[{"id":"1","name":"Sku","index":0},{"id":"2","name":"Qty","index":1}]}"""),
                        ("GET", book + "/tables/Lines/rows") when query.Contains("$skip=100", StringComparison.Ordinal) => (200, Lines(100, 40)),
                        ("GET", book + "/tables/Lines/rows") => (200, Lines(0, 100)),
                        _ => (404, """{"error":{"code":"ItemNotFound","message":"Not found."}}"""),
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
