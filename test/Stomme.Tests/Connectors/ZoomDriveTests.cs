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
    /// Zoom DRIVEN on an installed SQLite host, with the REAL connector, the host's egress, its token provider and the
    /// extraction port, against a loopback stand-in for Zoom's token endpoint and API: a Zoom connection made over the API, its
    /// account id a setting and its client vaulted, tested (the user named); a published pipeline makes a meeting with HTML and an
    /// accent, reads the upcoming meetings over two pages - no more than asked - registers someone, changes the meeting and
    /// deletes it - the account's grant exchanged with the client as HTTP Basic, every call its access token; the secret, the
    /// token and a meeting's start link in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class ZoomDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_makes_reads_registers_changes_and_deletes_meetings()
        {
            var secret = "s" + Guid.NewGuid().ToString("N");
            using var zoom = new ZoomStandIn();
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
                    Name = "Meetings", ConnectorType = "zoom", Endpoint = $"http://127.0.0.1:{zoom.Port}/v2", TimeoutSeconds = 10,
                    Settings = new() { ["account_id"] = "acct-drive" },
                    Credentials = new() { ["client-id"] = "client-1", ["client-secret"] = secret, ["token-url"] = $"http://127.0.0.1:{zoom.Port}/oauth/token" },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Meetings" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("make", "New meeting", "create_meeting", "topic = Café <b>review</b>\nstart_time = 2026-10-20T09:00:00+02:00\nduration = 45\nsettings = {\"waiting_room\": true}"),
                        Call("read", "Upcoming", "meetings", "type = upcoming\nmax = 320"),
                        Call("register", "Register", "add_registrant", "meeting = 85746065432\nemail = ada@example.com\nfirst_name = Ada"),
                        Call("change", "Longer", "update_meeting", "meeting = 85746065432\nduration = 60"),
                        Call("drop", "Drop", "delete_meeting", "meeting = 85746065432"),
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
                var received = zoom.Received;
                var grant = received.First(request => request.Path == "/oauth/token");
                Assert.Equal(("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("client-1:" + secret)), "POST /oauth/token grant_type=account_credentials&account_id=acct-drive"), (grant.Authorization, grant.Line));
                Assert.All(received.Where(request => request.Path != "/oauth/token"), request => Assert.Equal("Bearer access-drive", request.Authorization));
                Assert.Equal(new[]
                {
                    "GET /v2/users/me",
                    "POST /v2/users/me/meetings {\"topic\":\"Café <b>review</b>\",\"type\":2,\"start_time\":\"2026-10-20T07:00:00Z\",\"duration\":45,\"settings\":{\"waiting_room\":true}}",
                    "GET /v2/users/me/meetings?type=upcoming&page_size=300",
                    "GET /v2/users/me/meetings?type=upcoming&page_size=20&next_page_token=t2",
                    "POST /v2/meetings/85746065432/registrants {\"email\":\"ada@example.com\",\"first_name\":\"Ada\"}",
                    "PATCH /v2/meetings/85746065432 {\"duration\":60}",
                    "DELETE /v2/meetings/85746065432",
                }, received.Where(request => request.Path != "/oauth/token").Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                var read2 = JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == "read")!;
                Assert.Contains("\"RecordCount\": 320", read2["outputsJson"]!.GetValue<string>()); // both pages, no more than asked
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                foreach (var hidden in new[] { secret, "access-drive", "host-secret" })
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

        /// <summary>A loopback stand-in for Zoom's token endpoint and API: a token, the user, a meeting made, the upcoming meetings over two pages, a registrant added, a meeting changed and deleted.</summary>
        private sealed class ZoomStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Path, string Line, string? Authorization)> _received = new();

            public ZoomStandIn()
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

            private static string Meeting(long id) =>
                "{\"uuid\":\"u" + id + "\",\"id\":" + id + ",\"topic\":\"Café <b>review</b>\",\"type\":2,\"start_time\":\"2026-10-20T07:00:00Z\",\"duration\":45,\"start_url\":\"https://zoom.us/s/" + id + "?zak=host-secret\",\"join_url\":\"https://zoom.us/j/" + id + "\"}";

            private static string Meetings(long from, int count, string next) =>
                "{\"page_size\":300,\"next_page_token\":\"" + next + "\",\"meetings\":[" + string.Join(",", Enumerable.Range(0, count).Select(n => Meeting(from + n))) + "]}";

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
                        ("POST", "/oauth/token") => (200, """{"access_token":"access-drive","token_type":"bearer","expires_in":3599}"""),
                        ("GET", "/v2/users/me") => (200, """{"id":"KDcuGIm1QgePTO8WbOqwIQ","first_name":"Ada","last_name":"Lovelace","email":"ada@example.com"}"""),
                        ("POST", "/v2/users/me/meetings") => (201, Meeting(85746065432)),
                        ("GET", "/v2/users/me/meetings") when url.Query.Contains("next_page_token=t2", StringComparison.Ordinal) => (200, Meetings(85746065732, 40, "")),
                        ("GET", "/v2/users/me/meetings") => (200, Meetings(85746065432, 300, "t2")),
                        ("POST", "/v2/meetings/85746065432/registrants") => (201, """{"id":85746065432,"registrant_id":"r9","topic":"Café <b>review</b>","start_time":"2026-10-20T07:00:00Z","join_url":"https://zoom.us/w/85746065432?tk=r9"}"""),
                        ("PATCH", "/v2/meetings/85746065432") => (204, ""),
                        ("DELETE", "/v2/meetings/85746065432") => (204, ""),
                        _ => (404, """{"code":3001,"message":"Meeting does not exist."}"""),
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
