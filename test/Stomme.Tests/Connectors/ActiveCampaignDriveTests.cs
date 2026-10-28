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
    /// BL-229 - ActiveCampaign DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction
    /// port, against a loopback stand-in for its API: an ActiveCampaign connection made over the API, its key vaulted, tested (the
    /// key's user named); a published pipeline makes or changes a contact with HTML and an accent and a custom field by its
    /// personalization tag, tags the contact found by its address with a tag found by its name, reads a list's contacts (the list
    /// by its name) over two pages, makes a deal and deletes the contact - every call the key in the Api-Token header; the key in
    /// neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class ActiveCampaignDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_syncs_tags_reads_and_deletes_contacts_and_makes_a_deal()
        {
            var key = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            using var account = new ActiveCampaignStandIn();
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
                    Name = "Marketing", ConnectorType = "activecampaign", Endpoint = $"http://127.0.0.1:{account.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["api-key"] = key },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Marketing" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("sync", "Sync", "sync_contact", "email = ada@example.com\nfirst_name = Café <b>Ada</b>\nfields = {\"COMPANY\": \"Acme & Co\"}"),
                        Call("tag", "Tag", "add_tag", "contact = ada@example.com\ntag = VIP"),
                        Call("read", "Newsletter", "contacts", "list = Newsletter\nmax = 130"),
                        Call("deal", "Deal", "create_deal", "title = Café renewal\ncontact = 42\nvalue = 45000\ncurrency = EUR"),
                        Call("drop", "Drop", "delete_contact", "contact = 42"),
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
                var received = account.Received;
                Assert.All(received, request => Assert.Equal(key, request.Key));
                Assert.Equal(new[]
                {
                    "GET /api/3/users/me",
                    "GET /api/3/fields?limit=100&offset=0",
                    "POST /api/3/contact/sync {\"contact\":{\"email\":\"ada@example.com\",\"firstName\":\"Café <b>Ada</b>\",\"fieldValues\":[{\"field\":\"1\",\"value\":\"Acme & Co\"}]}}",
                    "GET /api/3/contacts?email=ada%40example.com&limit=1",
                    "GET /api/3/tags?search=VIP&limit=100",
                    "POST /api/3/contactTags {\"contactTag\":{\"contact\":\"42\",\"tag\":\"3\"}}",
                    "GET /api/3/lists?filters%5Bname%5D=Newsletter&limit=100",
                    "GET /api/3/contacts?listid=2&limit=100&offset=0",
                    "GET /api/3/contacts?listid=2&limit=30&offset=100",
                    "POST /api/3/deals {\"deal\":{\"title\":\"Café renewal\",\"value\":45000,\"currency\":\"eur\",\"contact\":\"42\"}}",
                    "DELETE /api/3/contacts/42",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                var read2 = JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == "read")!;
                Assert.Contains("\"RecordCount\": 130", read2["outputsJson"]!.GetValue<string>()); // both pages, no more than asked
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(key, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(key, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for ActiveCampaign's API: the key's user, the custom fields, a contact synced and found by its address, a tag found and put on, a list found and its contacts over two pages, a deal made, a contact deleted.</summary>
        private sealed class ActiveCampaignStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Line, string? Key)> _received = new();

            public ActiveCampaignStandIn()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Line, string? Key)> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
                }
            }

            private static string Contacts(int from, int count, int total) =>
                "{\"contacts\":[" + string.Join(",", Enumerable.Range(from, count).Select(n => "{\"id\":\"" + n + "\",\"email\":\"c" + n + "@example.com\",\"firstName\":\"C" + n + "\"}")) + "],\"meta\":{\"total\":\"" + total + "\"}}";

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
                    lock (_gate)
                    {
                        _received.Add(($"{context.Request.HttpMethod} {context.Request.RawUrl}{(body.Length == 0 ? "" : " " + body)}", context.Request.Headers["Api-Token"]));
                    }

                    var (status, answer) = (context.Request.HttpMethod, path) switch
                    {
                        ("GET", "/api/3/users/me") => (200, """{"user":{"username":"admin","firstName":"Ada","lastName":"Lovelace"}}"""),
                        ("GET", "/api/3/fields") => (200, """{"fields":[{"id":"1","title":"Company","perstag":"COMPANY"}],"meta":{"total":"1"}}"""),
                        ("POST", "/api/3/contact/sync") => (201, """{"contact":{"id":"42","email":"ada@example.com"}}"""),
                        ("GET", "/api/3/contacts") when url.Query.Contains("email=", StringComparison.Ordinal) => (200, """{"contacts":[{"id":"42","email":"ada@example.com"}],"meta":{"total":"1"}}"""),
                        ("GET", "/api/3/contacts") when url.Query.Contains("offset=100", StringComparison.Ordinal) => (200, Contacts(101, 40, 140)),
                        ("GET", "/api/3/contacts") => (200, Contacts(1, 100, 140)),
                        ("GET", "/api/3/tags") => (200, """{"tags":[{"id":"3","tag":"VIP"}],"meta":{"total":"1"}}"""),
                        ("POST", "/api/3/contactTags") => (201, """{"contactTag":{"id":"77","contact":"42","tag":"3"}}"""),
                        ("GET", "/api/3/lists") => (200, """{"lists":[{"id":"2","name":"Newsletter"}],"meta":{"total":"1"}}"""),
                        ("POST", "/api/3/deals") => (201, """{"deal":{"id":"9","title":"Café renewal","value":"45000"}}"""),
                        ("DELETE", "/api/3/contacts/42") => (200, "{}"),
                        _ => (404, """{"message":"No Result found"}"""),
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
