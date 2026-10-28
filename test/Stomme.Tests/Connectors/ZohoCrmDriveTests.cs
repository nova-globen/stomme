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
    /// BL-229 - Zoho CRM DRIVEN on an installed SQLite host, with the REAL connector, the host's egress, its token provider and the
    /// extraction port, against a loopback stand-in for Zoho's accounts server and CRM API: a Zoho CRM connection made over the API,
    /// its secret and refresh token vaulted, tested (the user named); a published pipeline makes a lead with HTML and an accent,
    /// reads the leads over two pages - no more than asked - upserts a contact by its address, changes the lead and deletes it - the
    /// refresh token exchanged, every call its access token in Zoho's scheme; the secrets in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class ZohoCrmDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_makes_reads_upserts_changes_and_deletes_records()
        {
            var secret = "s" + Guid.NewGuid().ToString("N");
            var refresh = "r" + Guid.NewGuid().ToString("N");
            using var zoho = new ZohoStandIn();
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
                    Name = "Sales", ConnectorType = "zohocrm", Endpoint = $"http://127.0.0.1:{zoho.Port}/crm/v2", TimeoutSeconds = 10,
                    Settings = new() { ["token_url"] = $"http://127.0.0.1:{zoho.Port}/oauth/v2/token" },
                    Credentials = new() { ["client-id"] = "client-1", ["client-secret"] = secret, ["refresh-token"] = refresh },
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
                        Call("make", "New lead", "create_record", "module = Leads\nvalues = {\"Last_Name\": \"Café <b>Lovelace</b>\", \"Company\": \"Acme & Co\"}"),
                        Call("read", "Leads", "records", "module = Leads\nfields = Last_Name, Email\norder = Created_Time desc\nmax = 250"),
                        Call("upsert", "Contact", "upsert_record", "module = Contacts\nvalues = {\"Last_Name\": \"Lovelace\", \"Email\": \"ada@example.com\"}\nduplicate_check_fields = Email"),
                        Call("change", "Contacted", "update_record", "module = Leads\nid = 4150868000000002\nvalues = {\"Lead_Status\": \"Contacted\"}"),
                        Call("drop", "Drop", "delete_record", "module = Leads\nid = 4150868000000002"),
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
                var received = zoho.Received;
                Assert.Contains("refresh_token=" + refresh, received.First(request => request.Path == "/oauth/v2/token").Line);
                Assert.All(received.Where(request => request.Path != "/oauth/v2/token"), request => Assert.Equal("Zoho-oauthtoken access-drive", request.Authorization));
                Assert.Equal(new[]
                {
                    "GET /crm/v2/users?type=CurrentUser",
                    "POST /crm/v2/Leads {\"data\":[{\"Last_Name\":\"Café <b>Lovelace</b>\",\"Company\":\"Acme & Co\"}]}",
                    "GET /crm/v2/Leads?fields=Last_Name%2CEmail&sort_by=Created_Time&sort_order=desc&per_page=200&page=1",
                    "GET /crm/v2/Leads?fields=Last_Name%2CEmail&sort_by=Created_Time&sort_order=desc&per_page=200&page=2",
                    "POST /crm/v2/Contacts/upsert {\"data\":[{\"Last_Name\":\"Lovelace\",\"Email\":\"ada@example.com\"}],\"duplicate_check_fields\":[\"Email\"]}",
                    "PUT /crm/v2/Leads/4150868000000002 {\"data\":[{\"Lead_Status\":\"Contacted\"}]}",
                    "DELETE /crm/v2/Leads/4150868000000002",
                }, received.Where(request => request.Path != "/oauth/v2/token").Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                var read2 = JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == "read")!;
                Assert.Contains("\"RecordCount\": 250", read2["outputsJson"]!.GetValue<string>()); // both pages, no more than asked
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                foreach (var hidden in new[] { secret, refresh, "access-drive" })
                {
                    Assert.DoesNotContain(hidden, steps, StringComparison.Ordinal);
                    Assert.DoesNotContain(hidden, graphBody, StringComparison.Ordinal);
                }
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for Zoho's accounts server and CRM API: a token, the user, a lead made, the leads over two pages, a contact upserted, a lead changed and deleted.</summary>
        private sealed class ZohoStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Path, string Line, string? Authorization)> _received = new();

            public ZohoStandIn()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Path, string Line, string? Authorization)> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
                }
            }

            private static string Leads(int from, int count, bool more) =>
                "{\"data\":[" + string.Join(",", Enumerable.Range(from, count).Select(n => "{\"id\":\"41508680000" + n.ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + "\",\"Last_Name\":\"L" + n + "\",\"Email\":\"l" + n + "@example.com\",\"Owner\":{\"name\":\"Ada\",\"id\":\"9\"}}"))
                + "],\"info\":{\"per_page\":200,\"count\":" + count + ",\"more_records\":" + (more ? "true" : "false") + "}}";

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
                        _received.Add((path, $"{context.Request.HttpMethod} {context.Request.RawUrl}{(body.Length == 0 ? "" : " " + body)}", context.Request.Headers["Authorization"]));
                    }

                    var (status, answer) = (context.Request.HttpMethod, path) switch
                    {
                        ("POST", "/oauth/v2/token") => (200, """{"access_token":"access-drive","expires_in":3600,"token_type":"Bearer","api_domain":"http://127.0.0.1"}"""),
                        ("GET", "/crm/v2/users") => (200, """{"users":[{"full_name":"Ada Lovelace","email":"ada@example.com","id":"9"}]}"""),
                        ("POST", "/crm/v2/Leads") => (201, """{"data":[{"code":"SUCCESS","details":{"Created_Time":"2026-10-12T14:00:00+02:00","Modified_Time":"2026-10-12T14:00:00+02:00","id":"4150868000000002"},"message":"record added","status":"success"}]}"""),
                        ("GET", "/crm/v2/Leads") when url.Query.EndsWith("&page=2", StringComparison.Ordinal) => (200, Leads(201, 120, more: false)),
                        ("GET", "/crm/v2/Leads") => (200, Leads(1, 200, more: true)),
                        ("POST", "/crm/v2/Contacts/upsert") => (200, """{"data":[{"code":"SUCCESS","duplicate_field":null,"action":"insert","details":{"id":"4150868000000003"},"message":"record added","status":"success"}]}"""),
                        ("PUT", "/crm/v2/Leads/4150868000000002") => (200, """{"data":[{"code":"SUCCESS","details":{"id":"4150868000000002"},"message":"record updated","status":"success"}]}"""),
                        ("DELETE", "/crm/v2/Leads/4150868000000002") => (200, """{"data":[{"code":"SUCCESS","details":{"id":"4150868000000002"},"message":"record deleted","status":"success"}]}"""),
                        _ => (404, """{"code":"INVALID_URL_PATTERN","message":"Please check if the URL trying to access is a correct one","status":"error"}"""),
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
