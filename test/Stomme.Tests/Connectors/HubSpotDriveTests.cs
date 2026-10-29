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
    /// HubSpot DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for HubSpot's API: a HubSpot connection made over the API, its token vaulted, tested (the
    /// account named); a published pipeline creates or updates a contact by its email, then finds the customers - each call with
    /// the token as the bearer; the token is in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class HubSpotDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_upserts_a_contact_and_finds_the_customers_through_a_vaulted_token()
        {
            var token = "pat-" + Guid.NewGuid().ToString("N");
            using var hubspot = new HubSpotStandIn();
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
                    Name = "CRM", ConnectorType = "hubspot", Endpoint = $"http://127.0.0.1:{hubspot.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["token"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "CRM" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                var graph = new PipelineGraph
                {
                    Variables = { new PipelineGraphVariable { Id = "v-customers", Name = "customers" } },
                    Steps =
                    {
                        new PipelineGraphStep { Id = "upsert", Type = "Stomme.ExtractFromConnection", Name = "Keep Ada", Inputs = { ["Connection"] = connection, ["Operation"] = "upsert", ["Parameters"] = "records = {\"email\": \"ada@example.com\", \"firstname\": \"Ada\", \"lifecyclestage\": \"customer\"}" } },
                        new PipelineGraphStep { Id = "search", Type = "Stomme.ExtractFromConnection", Name = "Customers", Inputs = { ["Connection"] = connection, ["Operation"] = "search", ["Parameters"] = "filters = lifecyclestage = customer\nproperties = email, firstname" }, OutputVariables = { ["Result"] = "v-customers" } },
                    },
                    Connections = { new PipelineGraphConnection { SourceId = "upsert", SourcePort = "Done", TargetId = "search" } },
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
                var received = hubspot.Received;
                Assert.All(received, request => Assert.Equal("Bearer " + token, request.Authorization));
                Assert.Equal("""{"inputs":[{"idProperty":"email","id":"ada@example.com","properties":{"email":"ada@example.com","firstname":"Ada","lifecyclestage":"customer"}}]}""",
                    received.Single(request => request.Path == "/crm/v3/objects/contacts/batch/upsert").Body);
                var search = JsonNode.Parse(received.Single(request => request.Path == "/crm/v3/objects/contacts/search").Body)!;
                Assert.Equal(("lifecyclestage", "customer", "[\"email\",\"firstname\"]"), (search["filterGroups"]![0]!["filters"]![0]!["propertyName"]!.GetValue<string>(), search["filterGroups"]![0]!["filters"]![0]!["value"]!.GetValue<string>(), search["properties"]!.ToJsonString()));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.Contains("ada@example.com", steps);
                Assert.Contains("grace@example.com", steps); // the customers found were the search's result
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for HubSpot's API: an account, a contact upserted, two customers found.</summary>
        private sealed class HubSpotStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Method, string Path, string? Authorization, string Body)> _received = new();

            public HubSpotStandIn()
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

                    var path = context.Request.Url!.AbsolutePath;
                    lock (_gate)
                    {
                        _received.Add((context.Request.HttpMethod, path, context.Request.Headers["Authorization"], body));
                    }

                    var answer = path switch
                    {
                        "/account-info/v3/details" => """{"portalId":4711,"accountType":"STANDARD"}""",
                        "/crm/v3/objects/contacts/batch/upsert" => """{"status":"COMPLETE","results":[{"id":"101","properties":{"email":"ada@example.com","firstname":"Ada","lifecyclestage":"customer"},"createdAt":"2026-10-01T08:00:00Z","updatedAt":"2026-10-11T08:00:00Z","archived":false}]}""",
                        "/crm/v3/objects/contacts/search" => """{"total":2,"results":[{"id":"101","properties":{"email":"ada@example.com","firstname":"Ada"},"createdAt":"2026-10-01T08:00:00Z","updatedAt":"2026-10-11T08:00:00Z","archived":false},{"id":"102","properties":{"email":"grace@example.com","firstname":"Grace"},"createdAt":"2026-10-02T08:00:00Z","updatedAt":"2026-10-02T08:00:00Z","archived":false}]}""",
                        _ => """{"status":"error","message":"Not found","category":"OBJECT_NOT_FOUND"}""",
                    };
                    var bytes = Encoding.UTF8.GetBytes(answer);
                    context.Response.StatusCode = answer.Contains("OBJECT_NOT_FOUND", StringComparison.Ordinal) ? 404 : 200;
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
