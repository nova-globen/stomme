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
    /// BL-229 - Mautic DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for an instance's API that refuses a call without the user's name and password: a Mautic
    /// connection made over the API, its password vaulted, tested; a published pipeline creates a contact with an accent,
    /// reads the contacts over two pages - no more than asked - adds points, adds the contact to a segment, sets their
    /// do-not-contact, reads the companies and deletes the contact; the password in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class MauticDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_creates_reads_scores_segments_and_deletes_contacts_with_its_credentials()
        {
            var password = "mp" + Guid.NewGuid().ToString("N");
            using var mautic = new MauticStandIn("kafe-api:" + password);
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
                    Name = "Shop marketing", ConnectorType = "mautic", Endpoint = $"http://127.0.0.1:{mautic.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["username"] = "kafe-api", ["password"] = password },
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
                        Call("create", "Create", "create_contact", "email = ada@kafe.example.test\nfirstname = Ada\ncompany = Kafé\ntags = fika"),
                        Call("read", "Read", "contacts", "search = fika\nmax = 120"),
                        Call("points", "Points", "points", "contact = 47\npoints = 5"),
                        Call("segment", "Segment", "add_to_segment", "contact = 47\nsegment = 2"),
                        Call("dnc", "Do not contact", "add_do_not_contact", "contact = 47\nreason = unsubscribed"),
                        Call("companies", "Companies", "companies", ""),
                        Call("delete", "Delete", "delete_contact", "contact = 47"),
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
                var received = mautic.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "no credentials: " + request.Line));
                Assert.Equal(new[]
                {
                    "GET /api/users/self",
                    "POST /api/contacts/new {\"email\":\"ada@kafe.example.test\",\"firstname\":\"Ada\",\"company\":\"Kafé\",\"tags\":[\"fika\"]}",
                    "GET /api/contacts?search=fika&start=0&limit=100",
                    "GET /api/contacts?search=fika&start=100&limit=100",
                    "POST /api/contacts/47/points/plus/5",
                    "POST /api/segments/2/contact/47/add",
                    "POST /api/contacts/47/dnc/email/add {\"reason\":1}",
                    "GET /api/companies?start=0&limit=100",
                    "DELETE /api/contacts/47/delete",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal("Kafé", JsonNode.Parse(Outputs("create"))!["Result"]![0]!["company"]!.GetValue<string>());
                Assert.Equal(120, JsonNode.Parse(Outputs("read"))!["RecordCount"]!.GetValue<int>()); // both pages, no more than asked
                Assert.Equal(5, JsonNode.Parse(Outputs("points"))!["Result"]![0]!["points"]!.GetValue<long>());
                Assert.True(JsonNode.Parse(Outputs("segment"))!["Result"]![0]!["added"]!.GetValue<bool>());
                Assert.Equal("Kafé AB", JsonNode.Parse(Outputs("companies"))!["Result"]![0]!["companyname"]!.GetValue<string>());
                Assert.Equal(47, JsonNode.Parse(Outputs("delete"))!["Result"]![0]!["id"]!.GetValue<long>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(password, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(password, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for a Mautic instance's API that answers only a call carrying the user's name and password.</summary>
        private sealed class MauticStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _basic;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public MauticStandIn(string userAndPassword)
            {
                _basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(userAndPassword));
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
                    var authorized = context.Request.Headers["Authorization"] == _basic;
                    lock (_gate)
                    {
                        _received.Add(($"{method} {raw}{(body.Length == 0 ? "" : " " + body)}", authorized));
                    }

                    static string Contact(int id) => "{\"id\":" + id + ",\"fields\":{\"all\":{\"id\":" + id + ",\"firstname\":\"Ada\",\"email\":\"ada@kafe.example.test\",\"company\":\"Kafé\"}}}";
                    string Contacts(int first, int count) => "{\"total\":\"180\",\"contacts\":{" + string.Join(",", Enumerable.Range(first, count).Select(i => "\"" + i + "\":" + Contact(i))) + "}}";
                    var path = raw.Split('?')[0];
                    var (status, answer) = !authorized ? (401, "{\"errors\":[{\"code\":401,\"message\":\"API authorization denied.\"}]}")
                        : (method, path) switch
                        {
                            ("GET", "/api/users/self") => (200, "{\"id\":1,\"username\":\"kafe-api\",\"firstName\":\"Kafé\",\"lastName\":\"API\"}"),
                            ("POST", "/api/contacts/new") => (201, "{\"contact\":" + Contact(47) + "}"),
                            ("GET", "/api/contacts") => (200, raw.Contains("start=100", StringComparison.Ordinal) ? Contacts(101, 80) : Contacts(1, 100)),
                            ("POST", "/api/contacts/47/points/plus/5") or ("POST", "/api/segments/2/contact/47/add") => (200, "{\"success\":1}"),
                            ("POST", "/api/contacts/47/dnc/email/add") => (200, "{\"contact\":" + Contact(47) + "}"),
                            ("GET", "/api/companies") => (200, "{\"total\":1,\"companies\":{\"5\":{\"id\":5,\"fields\":{\"all\":{\"id\":5,\"companyname\":\"Kafé AB\"}}}}}"),
                            ("DELETE", "/api/contacts/47/delete") => (200, "{\"contact\":" + Contact(47) + "}"),
                            _ => (404, "{\"errors\":[{\"code\":404,\"message\":\"Item was not found.\"}]}"),
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
