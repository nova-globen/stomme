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
    /// BL-229 - Webflow DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for Webflow's Data API: a Webflow connection made over the API, its token vaulted, tested (the
    /// token's reach asked for); a published pipeline makes a live item with HTML and an accent, reads the collection's
    /// items over two pages by offset - no more than asked - changes the item, publishes it, deletes it and publishes the site -
    /// every call the token as a bearer; the token in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class WebflowDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Site = "580e63e98c9a982ac9b8b741";
        private const string Collection = "580e63fc8c9a982ac9b8b745";
        private const string Item = "6500c0ffee0000000000002a";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_makes_reads_changes_publishes_and_deletes_an_item_and_publishes_the_site()
        {
            var token = "wf" + Guid.NewGuid().ToString("N");
            using var webflow = new WebflowStandIn();
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
                    Name = "Site CMS", ConnectorType = "webflow", Endpoint = $"http://127.0.0.1:{webflow.Port}/v2", TimeoutSeconds = 10,
                    Credentials = new() { ["token"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Menu" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("add", "Add", "create_item", $"collection = {Collection}\nfields = {{\"name\": \"Café <b>Menu</b>\", \"slug\": \"cafe-menu\", \"post-body\": \"<p>Öppet</p>\"}}\nlive = true"),
                        Call("items", "Items", "items", $"collection = {Collection}\nmax = 150"),
                        Call("change", "Change", "update_item", $"collection = {Collection}\nitem = {Item}\nfields = {{\"price\": 14}}"),
                        Call("publish", "Publish", "publish_items", $"collection = {Collection}\nitems = {Item}"),
                        Call("drop", "Drop", "delete_item", $"collection = {Collection}\nitem = {Item}"),
                        Call("site", "Site", "publish_site", $"site = {Site}"),
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
                var received = webflow.Received;
                Assert.All(received, request => Assert.Equal("Bearer " + token, request.Authorization));
                Assert.Equal(new[]
                {
                    "GET /v2/token/introspect",
                    $"POST /v2/collections/{Collection}/items/live {{\"fieldData\":{{\"name\":\"Café <b>Menu</b>\",\"slug\":\"cafe-menu\",\"post-body\":\"<p>Öppet</p>\"}}}}",
                    $"GET /v2/collections/{Collection}/items?offset=0&limit=100",
                    $"GET /v2/collections/{Collection}/items?offset=100&limit=50",
                    $"PATCH /v2/collections/{Collection}/items/{Item} {{\"fieldData\":{{\"price\":14}}}}",
                    $"POST /v2/collections/{Collection}/items/publish {{\"itemIds\":[\"{Item}\"]}}",
                    $"DELETE /v2/collections/{Collection}/items/{Item}",
                    $"POST /v2/sites/{Site}/publish {{\"publishToWebflowSubdomain\":true}}",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                var items = JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == "items")!;
                Assert.Contains("\"RecordCount\": 150", items["outputsJson"]!.GetValue<string>()); // both pages, no more than asked
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for Webflow's Data API: the token's reach, an item made live, the items over pages by offset, an item changed, published and deleted, the site published.</summary>
        private sealed class WebflowStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Line, string? Authorization)> _received = new();

            public WebflowStandIn()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Line, string? Authorization)> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
                }
            }

            private static string ItemJson(int n) =>
                "{\"id\":\"" + n.ToString("x24", System.Globalization.CultureInfo.InvariantCulture) + "\",\"createdOn\":\"2026-09-30T09:00:00.000Z\",\"lastUpdated\":\"2026-10-02T09:00:00.000Z\",\"lastPublished\":null,\"isDraft\":false,\"isArchived\":false,"
                + "\"fieldData\":{\"name\":\"Dish " + n + "\",\"slug\":\"dish-" + n + "\",\"price\":" + n + "}}";

            private static string Page(int offset, int limit) =>
                "{\"items\":[" + string.Join(",", Enumerable.Range(offset + 1, limit).Select(ItemJson)) + "],\"pagination\":{\"limit\":" + limit + ",\"offset\":" + offset + ",\"total\":300}}";

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

                    var url = context.Request.Url!;
                    var path = url.AbsolutePath;
                    var query = System.Web.HttpUtility.ParseQueryString(url.Query);
                    lock (_gate)
                    {
                        _received.Add(($"{context.Request.HttpMethod} {context.Request.RawUrl}{(body.Length == 0 ? "" : " " + body)}", context.Request.Headers["Authorization"]));
                    }

                    var items = $"/v2/collections/{Collection}/items";
                    var (status, answer) = (context.Request.HttpMethod, path) switch
                    {
                        ("GET", "/v2/token/introspect") => (200, "{\"authorization\":{\"id\":\"a1\",\"grantType\":\"site_token\",\"scope\":\"cms:read,cms:write,sites:write\",\"authorizedTo\":{\"siteIds\":[\"" + Site + "\"],\"workspaceIds\":[],\"userIds\":[]}}}"),
                        ("POST", _) when path == items + "/live" => (202, ItemJson(42)),
                        ("GET", _) when path == items => (200, Page(int.Parse(query["offset"]!, System.Globalization.CultureInfo.InvariantCulture), int.Parse(query["limit"]!, System.Globalization.CultureInfo.InvariantCulture))),
                        ("PATCH", _) when path == items + "/" + Item => (200, ItemJson(42)),
                        ("POST", _) when path == items + "/publish" => (202, "{\"publishedItemIds\":[\"" + Item + "\"],\"errors\":[]}"),
                        ("DELETE", _) when path == items + "/" + Item => (204, ""),
                        ("POST", _) when path == $"/v2/sites/{Site}/publish" => (202, "{\"customDomains\":[],\"publishToWebflowSubdomain\":true,\"publishScope\":\"site\"}"),
                        _ => (404, """{"code":"resource_not_found","message":"Not found"}"""),
                    };
                    context.Response.StatusCode = status;
                    if (answer.Length > 0)
                    {
                        var bytes = Encoding.UTF8.GetBytes(answer);
                        context.Response.ContentType = "application/json";
                        await context.Response.OutputStream.WriteAsync(bytes);
                    }

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
