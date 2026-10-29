#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
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
    /// Google Cloud Storage DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the
    /// extraction port, against a real fake-gcs-server (<see cref="GcsTestEndpoint"/>) and a loopback stand-in for Google's token
    /// endpoint: a connection made over the API - the client's secret and refresh token vaulted - refused while loopback is not
    /// allowlisted, then tested; a published pipeline makes a bucket, uploads a note over several lines from the step's Query box,
    /// lists the bucket, downloads the note into the site's pipeline folder, deletes it and the bucket; the secrets in neither the
    /// graph nor the run. Fails, never skips, without a server.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class GoogleCloudStorageDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_keeps_a_note_in_a_bucket_and_fetches_it()
        {
            using var tokens = new GoogleTokenStandIn("kafe-client", "kafe-secret-drive", "kafe-refresh-drive");
            var bucket = "kafe-" + Guid.NewGuid().ToString("N")[..10];
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

                var made = await Send(HttpMethod.Post, "/api/Connections", new ConnectionRequest
                {
                    Name = "Kafé bucket", ConnectorType = "googlecloudstorage", Endpoint = GcsTestEndpoint.Endpoint, TimeoutSeconds = 30,
                    Settings = new() { ["token_url"] = tokens.Url, ["project"] = "kafe-kassa" },
                    Credentials = new() { ["client-id"] = "kafe-client", ["client-secret"] = "kafe-secret-drive", ["refresh-token"] = "kafe-refresh-drive" },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var refused = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test before loopback is allowed");
                Assert.False(JsonNode.Parse(refused.Body)!["success"]!.GetValue<bool>(), refused.Body); // loopback is a private host: refused until allowlisted

                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/Connections/egress", new ConnectionEgressRequest { AllowedHosts = GcsTestEndpoint.Host }, "the Host allows the server")).Status);
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kassa bucket" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string operation, string parameters, string? query = null)
                {
                    var step = new PipelineGraphStep { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                    if (query is not null)
                    {
                        step.Inputs["Query"] = query;
                    }

                    return step;
                }

                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("bucket", "bucket_create", $"bucket = {bucket}"),
                        Call("note", "upload", $"bucket = {bucket}\nobject = anteckningar/stängt.txt\nmetadata = {{\"kund\": \"ada\"}}", "Kassan stängd\nÅter 08:00"),
                        Call("list", "objects", $"bucket = {bucket}\nprefix = anteckningar/"),
                        Call("fetch", "download", $"bucket = {bucket}\nobject = anteckningar/stängt.txt\nfile = gcs/"),
                        Call("remove", "delete", $"bucket = {bucket}\nobject = anteckningar/stängt.txt"),
                        Call("clean", "bucket_delete", $"bucket = {bucket}"),
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
                for (var attempt = 0; attempt < 300 && run?.IsFinished != true; attempt++)
                {
                    var read = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}", output, "the run");
                    run = read.Status == HttpStatusCode.OK ? JsonSerializer.Deserialize<PipelineRunView>(read.Body, Json) : null;
                    await Task.Delay(100);
                }

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.True(run?.SubStatus == "Finished", steps);
                JsonNode Outputs(string id) => JsonNode.Parse(JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>())!;
                var note = System.Text.Encoding.UTF8.GetByteCount("Kassan stängd\nÅter 08:00");
                Assert.Equal(note, Outputs("note")["Result"]![0]!["size"]!.GetValue<long>());
                var listed = Outputs("list")["Result"]![0]!;
                Assert.Equal(("anteckningar/stängt.txt", (long)note, "file"), (listed["name"]!.GetValue<string>(), listed["size"]!.GetValue<long>(), listed["type"]!.GetValue<string>()));
                var fetched = Outputs("fetch")["Result"]![0]!;
                Assert.Equal(("gcs/stängt.txt", (long)note), (fetched["file"]!.GetValue<string>(), fetched["size"]!.GetValue<long>())); // written into the site's pipeline folder
                Assert.True(Outputs("clean")["Result"]![0]!["deleted"]!.GetValue<bool>());
                Assert.True(tokens.Issued >= 1); // the refresh grant exchanged at the token endpoint the connection names
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                foreach (var secret in new[] { "kafe-secret-drive", "kafe-refresh-drive" })
                {
                    Assert.DoesNotContain(secret, steps, StringComparison.Ordinal);
                    Assert.DoesNotContain(secret, graphBody, StringComparison.Ordinal);
                }
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
