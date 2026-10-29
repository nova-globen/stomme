#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
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
    /// Mailchimp DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for the Marketing API: a Mailchimp connection made over the API, its key vaulted, tested (the
    /// account named); a published pipeline subscribes a member with merge fields, reads the audience's subscribed members over two
    /// pages, tags the member and sends a campaign - every call the key by Mailchimp's Basic pair, the member by the MD5 of its
    /// address; the key is in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class MailchimpDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_subscribes_reads_tags_and_sends()
        {
            var key = Guid.NewGuid().ToString("N") + "-us21";
            using var mailchimp = new MailchimpStandIn();
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
                    Name = "Newsletter", ConnectorType = "mailchimp", Endpoint = $"http://127.0.0.1:{mailchimp.Port}", TimeoutSeconds = 10, Credentials = new() { ["api-key"] = key },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);
                Assert.Single(mailchimp.Received, request => request.PathAndQuery == "/3.0/"); // the account asked for

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Newsletter" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("subscribe", "Subscribe Ada", "add_member", "list = a1b2c3d4e5\nemail = Ada@Example.com\nstatus = subscribed\nmerge_fields = {\"FNAME\": \"Ada\"}\ntags = web"),
                        Call("members", "Subscribed", "members", "list = a1b2c3d4e5\nstatus = subscribed\nmax = 1003"),
                        Call("tag", "Tag Ada", "tag_member", "list = a1b2c3d4e5\nemail = ada@example.com\nadd = vip\nremove = trial"),
                        Call("send", "Send the news", "send_campaign", "campaign = c0ffee1234"),
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
                var received = mailchimp.Received;
                var basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("stomme:" + key));
                Assert.All(received, request => Assert.Equal(basic, request.Authorization));
                var ada = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes("ada@example.com"))).ToLowerInvariant();
                Assert.Equal(("/3.0/lists/a1b2c3d4e5/members", "{\"email_address\":\"Ada@Example.com\",\"status\":\"subscribed\",\"merge_fields\":{\"FNAME\":\"Ada\"},\"tags\":[\"web\"]}"),
                    received.Where(request => request.Method == "POST" && request.PathAndQuery.EndsWith("/members", StringComparison.Ordinal)).Select(request => (request.PathAndQuery, request.Body)).Single());
                var pages = received.Where(request => request.Method == "GET" && request.PathAndQuery.StartsWith("/3.0/lists/a1b2c3d4e5/members?", StringComparison.Ordinal)).Select(request => request.PathAndQuery).ToList();
                Assert.Equal(new[] { "/3.0/lists/a1b2c3d4e5/members?status=subscribed&count=1000&offset=0", "/3.0/lists/a1b2c3d4e5/members?status=subscribed&count=3&offset=1000" }, pages);
                Assert.Equal(($"/3.0/lists/a1b2c3d4e5/members/{ada}/tags", "{\"tags\":[{\"name\":\"vip\",\"status\":\"active\"},{\"name\":\"trial\",\"status\":\"inactive\"}]}"),
                    received.Where(request => request.PathAndQuery.EndsWith("/tags", StringComparison.Ordinal)).Select(request => (request.PathAndQuery, request.Body)).Single());
                Assert.Single(received, request => request.Method == "POST" && request.PathAndQuery == "/3.0/campaigns/c0ffee1234/actions/send");

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                var subscribed = JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == "members")!;
                Assert.Contains("\"RecordCount\": 1003", subscribed["outputsJson"]!.GetValue<string>()); // both pages' members
                Assert.DoesNotContain(key, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(key, (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for the Marketing API: the account, a member added, the members over pages, a member tagged, a campaign sent.</summary>
        private sealed class MailchimpStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Method, string PathAndQuery, string? Authorization, string Body)> _received = new();

            public MailchimpStandIn()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Method, string PathAndQuery, string? Authorization, string Body)> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
                }
            }

            private static string Members(int from, int count) =>
                "{\"members\":[" + string.Join(",", Enumerable.Range(from, count).Select(id => "{\"id\":\"h" + id + "\",\"email_address\":\"user" + id + "@example.com\",\"status\":\"subscribed\"}")) + "],\"total_items\":1003}";

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

                    var raw = context.Request.RawUrl ?? "/";
                    var path = raw.Split('?')[0];
                    var method = context.Request.HttpMethod;
                    lock (_gate)
                    {
                        _received.Add((method, raw, context.Request.Headers["Authorization"], body));
                    }

                    var (status, answer) = (method, path) switch
                    {
                        ("GET", "/3.0/") => (200, "{\"account_id\":\"x1\",\"account_name\":\"Acme Shop\",\"email\":\"ada@acme.example\"}"),
                        ("POST", "/3.0/lists/a1b2c3d4e5/members") => (200, "{\"id\":\"h1\",\"email_address\":\"ada@example.com\",\"status\":\"subscribed\"}"),
                        ("GET", "/3.0/lists/a1b2c3d4e5/members") when raw.Contains("offset=1000", StringComparison.Ordinal) => (200, Members(1001, 3)),
                        ("GET", "/3.0/lists/a1b2c3d4e5/members") => (200, Members(1, 1000)),
                        ("POST", _) when path.EndsWith("/tags", StringComparison.Ordinal) => (204, ""),
                        ("POST", "/3.0/campaigns/c0ffee1234/actions/send") => (204, ""),
                        _ => (404, "{\"title\":\"Resource Not Found\",\"status\":404,\"detail\":\"The requested resource could not be found.\"}"),
                    };
                    var bytes = Encoding.UTF8.GetBytes(answer);
                    context.Response.StatusCode = status;
                    context.Response.ContentType = "application/json";
                    if (bytes.Length > 0)
                    {
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
