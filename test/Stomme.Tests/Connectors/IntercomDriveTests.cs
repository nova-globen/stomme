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
    /// Intercom DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for Intercom's API: an Intercom connection made over the API, its token vaulted, tested (the
    /// admin and the workspace named); a published pipeline makes a user with HTML and an accent and a custom attribute, reads the
    /// users by a search over two pages by Intercom's cursor - no more than asked - tags the user found by its address with a tag
    /// found by its name, upserts a company, attaches the user to it found by its company id and deletes the user - every call the
    /// token as a bearer with Intercom's API version; the token in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class IntercomDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_makes_reads_tags_attaches_and_deletes_contacts()
        {
            var token = "dG9r" + Guid.NewGuid().ToString("N");
            using var intercom = new IntercomStandIn();
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
                    Name = "Support", ConnectorType = "intercom", Endpoint = $"http://127.0.0.1:{intercom.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["token"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Support" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("make", "New user", "create_contact", "email = ada@example.com\nname = Café <b>Ada</b>\ncustom_attributes = {\"plan\": \"Pro\"}"),
                        Call("read", "Users", "contacts", "role = user\nmax = 160"),
                        Call("tag", "Tag", "tag_contact", "contact = ada@example.com\ntag = VIP"),
                        Call("company", "Company", "upsert_company", "company_id = acme-ab\nname = Acme & Co"),
                        Call("attach", "Attach", "attach_company", "contact = 661\ncompany = company:acme-ab"),
                        Call("drop", "Drop", "delete_contact", "contact = 661"),
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
                var received = intercom.Received;
                Assert.All(received, request => Assert.Equal(("Bearer " + token, "2.11"), (request.Authorization, request.Version)));
                Assert.Equal(new[]
                {
                    "GET /me",
                    "POST /contacts {\"role\":\"user\",\"email\":\"ada@example.com\",\"name\":\"Café <b>Ada</b>\",\"custom_attributes\":{\"plan\":\"Pro\"}}",
                    "POST /contacts/search {\"query\":{\"field\":\"role\",\"operator\":\"=\",\"value\":\"user\"},\"pagination\":{\"per_page\":150}}",
                    "POST /contacts/search {\"query\":{\"field\":\"role\",\"operator\":\"=\",\"value\":\"user\"},\"pagination\":{\"per_page\":10,\"starting_after\":\"next-1\"}}",
                    "POST /contacts/search {\"query\":{\"field\":\"email\",\"operator\":\"=\",\"value\":\"ada@example.com\"},\"pagination\":{\"per_page\":1}}",
                    "GET /tags",
                    "POST /contacts/661/tags {\"id\":\"17\"}",
                    "POST /companies {\"company_id\":\"acme-ab\",\"name\":\"Acme & Co\"}",
                    "GET /companies?company_id=acme-ab",
                    "POST /contacts/661/companies {\"id\":\"c1\"}",
                    "DELETE /contacts/661",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                var read2 = JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == "read")!;
                Assert.Contains("\"RecordCount\": 160", read2["outputsJson"]!.GetValue<string>()); // both pages, no more than asked
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for Intercom's API: the admin, a user made, users searched over two pages, a user found by its address, the tags, a tag put on, a company upserted and found, a user attached and deleted.</summary>
        private sealed class IntercomStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Line, string? Authorization, string? Version)> _received = new();

            public IntercomStandIn()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Line, string? Authorization, string? Version)> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
                }
            }

            private static string Contacts(int from, int count, string? next) =>
                "{\"type\":\"list\",\"data\":[" + string.Join(",", Enumerable.Range(from, count).Select(n => "{\"type\":\"contact\",\"id\":\"" + (660 + n) + "\",\"role\":\"user\",\"email\":\"c" + n + "@example.com\",\"created_at\":1700000000}"))
                + "],\"pages\":{\"type\":\"pages\",\"per_page\":150" + (next is null ? "" : ",\"next\":{\"page\":2,\"starting_after\":\"" + next + "\"}") + "}}";

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

                    var path = context.Request.Url!.AbsolutePath;
                    lock (_gate)
                    {
                        _received.Add(($"{context.Request.HttpMethod} {context.Request.RawUrl}{(body.Length == 0 ? "" : " " + body)}", context.Request.Headers["Authorization"], context.Request.Headers["Intercom-Version"]));
                    }

                    var (status, answer) = (context.Request.HttpMethod, path) switch
                    {
                        ("GET", "/me") => (200, """{"type":"admin","id":"991","name":"Ada Lovelace","app":{"type":"app","name":"Acme Support"}}"""),
                        ("POST", "/contacts") => (200, """{"type":"contact","id":"661","role":"user","email":"ada@example.com","name":"Café <b>Ada</b>","custom_attributes":{"plan":"Pro"}}"""),
                        ("POST", "/contacts/search") when body.Contains("\"field\":\"email\"", StringComparison.Ordinal) => (200, Contacts(1, 1, null)),
                        ("POST", "/contacts/search") when body.Contains("starting_after", StringComparison.Ordinal) => (200, Contacts(151, 50, null)),
                        ("POST", "/contacts/search") => (200, Contacts(1, 150, "next-1")),
                        ("GET", "/tags") => (200, """{"type":"list","data":[{"type":"tag","id":"17","name":"VIP"}]}"""),
                        ("POST", "/contacts/661/tags") => (200, """{"type":"tag","id":"17","name":"VIP"}"""),
                        ("POST", "/companies") => (200, """{"type":"company","id":"c1","company_id":"acme-ab","name":"Acme & Co"}"""),
                        ("GET", "/companies") => (200, """{"type":"company","id":"c1","company_id":"acme-ab","name":"Acme & Co"}"""),
                        ("POST", "/contacts/661/companies") => (200, """{"type":"company","id":"c1","company_id":"acme-ab"}"""),
                        ("DELETE", "/contacts/661") => (200, """{"id":"661","type":"contact","deleted":true}"""),
                        _ => (404, """{"type":"error.list","errors":[{"code":"not_found","message":"Resource Not Found"}]}"""),
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
