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
    /// Google Contacts DRIVEN on an installed SQLite host, with the REAL connector, the host's egress, the shared OAuth2
    /// token provider and the extraction port, against a loopback stand-in for Google's token endpoint and the People API: a Google
    /// Contacts connection made over the API, its secret and refresh token vaulted, tested (the groups named); a published
    /// pipeline makes a contact, reads the contacts over two pages, changes one with its etag and deletes it - the refresh token
    /// exchanged, every call its access token; the secrets in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class GoogleContactsDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_makes_reads_changes_and_deletes_contacts()
        {
            var secret = "s" + Guid.NewGuid().ToString("N");
            var refresh = "r" + Guid.NewGuid().ToString("N");
            using var google = new PeopleStandIn();
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
                    Name = "Address book", ConnectorType = "googlecontacts", Endpoint = $"http://127.0.0.1:{google.Port}/v1", TimeoutSeconds = 10,
                    Settings = new() { ["token_url"] = $"http://127.0.0.1:{google.Port}/token" },
                    Credentials = new() { ["client-id"] = "client-1", ["client-secret"] = secret, ["refresh-token"] = refresh },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Contacts" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("make", "Ada", "create_contact", "given_name = Ada\nfamily_name = Lovelace\nemails = work: ada@example.com\ncompany = Analytical & Co"),
                        Call("read", "Everyone", "contacts", "fields = names, emailAddresses\nmax = 130"),
                        Call("change", "Rename", "update_contact", "contact = c7\ngiven_name = Augusta"),
                        Call("drop", "Drop", "delete_contact", "contact = c7"),
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
                var received = google.Received;
                Assert.Contains("refresh_token=" + refresh, received.First(request => request.Path == "/token").Body);
                Assert.All(received.Where(request => request.Path != "/token"), request => Assert.Equal("Bearer access-drive", request.Authorization));
                const string all = "names%2CemailAddresses%2CphoneNumbers%2Corganizations%2Cbiographies%2Cbirthdays%2Caddresses%2Cmemberships%2Cmetadata";
                Assert.Equal(new[]
                {
                    "GET /v1/contactGroups?pageSize=100",
                    "POST /v1/people:createContact?personFields=" + all + " {\"names\":[{\"givenName\":\"Ada\",\"familyName\":\"Lovelace\"}],\"emailAddresses\":[{\"value\":\"ada@example.com\",\"type\":\"work\"}],\"organizations\":[{\"name\":\"Analytical & Co\"}]}",
                    "GET /v1/people/me/connections?personFields=names%2CemailAddresses&pageSize=100",
                    "GET /v1/people/me/connections?personFields=names%2CemailAddresses&pageSize=30&pageToken=next",
                    "GET /v1/people/c7?personFields=names",
                    "PATCH /v1/people/c7:updateContact?updatePersonFields=names&personFields=" + all + " {\"names\":[{\"givenName\":\"Augusta\",\"familyName\":\"Lovelace\"}],\"etag\":\"%EgUBAj\"}",
                    "DELETE /v1/people/c7:deleteContact",
                }, received.Where(request => request.Path != "/token").Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                var read2 = JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == "read")!;
                Assert.Contains("\"RecordCount\": 130", read2["outputsJson"]!.GetValue<string>()); // both pages, up to the most asked for
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                foreach (var hidden in new[] { secret, refresh })
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

        /// <summary>A loopback stand-in for Google's token endpoint and the People API: the groups, a contact made, the contacts over two pages, one changed and deleted.</summary>
        private sealed class PeopleStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Path, string Line, string Body, string? Authorization)> _received = new();

            public PeopleStandIn()
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

            private static string Person(int n) => "{\"resourceName\":\"people/c" + n + "\",\"etag\":\"%EgUBAj\",\"names\":[{\"displayName\":\"Ada Lovelace\",\"givenName\":\"Ada\",\"familyName\":\"Lovelace\"}],\"emailAddresses\":[{\"value\":\"ada" + n + "@example.com\"}]}";

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
                        _received.Add((path, $"{context.Request.HttpMethod} {url.PathAndQuery}{(body.Length == 0 || path == "/token" ? "" : " " + body)}", body, context.Request.Headers["Authorization"]));
                    }

                    var (status, answer) = (context.Request.HttpMethod, path) switch
                    {
                        ("POST", "/token") => (200, """{"access_token":"access-drive","expires_in":3600,"token_type":"Bearer"}"""),
                        ("GET", "/v1/contactGroups") => (200, """{"contactGroups":[{"resourceName":"contactGroups/myContacts","formattedName":"My Contacts"}]}"""),
                        ("POST", "/v1/people:createContact") => (200, Person(7)),
                        ("GET", "/v1/people/me/connections") when url.Query.Contains("pageToken=next", StringComparison.Ordinal) => (200, "{\"connections\":[" + string.Join(",", Enumerable.Range(101, 100).Select(Person)) + "]}"),
                        ("GET", "/v1/people/me/connections") => (200, "{\"connections\":[" + string.Join(",", Enumerable.Range(1, 100).Select(Person)) + "],\"nextPageToken\":\"next\"}"),
                        ("GET", "/v1/people/c7") => (200, Person(7)),
                        ("PATCH", "/v1/people/c7:updateContact") => (200, Person(7)),
                        ("DELETE", "/v1/people/c7:deleteContact") => (200, "{}"),
                        _ => (404, """{"error":{"code":404,"message":"Not found.","status":"NOT_FOUND"}}"""),
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
