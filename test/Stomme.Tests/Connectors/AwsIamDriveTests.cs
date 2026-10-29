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
    /// AWS IAM DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against moto (<c>scripts/moto-test-server.ps1</c>'s throwaway on loopback), which checks every signature against the IAM key
    /// it issued: a connection made over the API and tested; a published pipeline, under a path of its own, making a group and a
    /// user, putting the user in it, reading the group with its users, and deleting the group - its user taken out first - and the
    /// user; the secret in neither the graph nor the run. Fails, never skips, without moto.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class AwsIamDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_administers_users_and_groups()
        {
            var secret = MotoTestEndpoint.SecretKey;
            var token = Guid.NewGuid().ToString("N")[..8];
            var path = $"/kafe-drive-{token}/";
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false,
                extraSettings: new JsonObject { ["Pipelines"] = new JsonObject { ["Enabled"] = true } });
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                Task<(HttpStatusCode Status, string Body)> Send(HttpMethod method, string route, object body, string label) =>
                    GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, method, route, body, output, label);

                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/Connections/egress", new ConnectionEgressRequest { AllowedHosts = MotoTestEndpoint.Host }, "the Host allows loopback")).Status);
                var made = await Send(HttpMethod.Post, "/api/Connections", new ConnectionRequest
                {
                    Name = "Kafé åtkomst", ConnectorType = "awsiam", Endpoint = MotoTestEndpoint.Endpoint, TimeoutSeconds = 30,
                    Credentials = new() { ["access-key"] = MotoTestEndpoint.AccessKey, ["secret-key"] = secret },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kassans åtkomst" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("team", "group_create", $"group = kassa-{token}\npath = {path}"),
                        Call("person", "user_create", $"user = ada-{token}\npath = {path}\ntags = {{\"team\": \"kassa\"}}"),
                        Call("join", "user_add_to_group", $"user = ada-{token}\ngroup = kassa-{token}"),
                        Call("read", "group", $"group = kassa-{token}\ninclude_users = true"),
                        Call("disband", "group_delete", $"group = kassa-{token}"),
                        Call("leave", "user_delete", $"user = ada-{token}"),
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
                for (var attempt = 0; attempt < 600 && run?.IsFinished != true; attempt++)
                {
                    var read = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}", output, "the run");
                    run = read.Status == HttpStatusCode.OK ? JsonSerializer.Deserialize<PipelineRunView>(read.Body, Json) : null;
                    await Task.Delay(100);
                }

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.True(run?.SubStatus == "Finished", steps);
                JsonNode Outputs(string id) => JsonNode.Parse(JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>())!;
                Assert.Equal(path, Outputs("team")["Result"]![0]!["Path"]!.GetValue<string>());
                Assert.Equal($"ada-{token}", Outputs("person")["Result"]![0]!["UserName"]!.GetValue<string>());
                var members = JsonNode.Parse(Outputs("read")["Result"]![0]!["Users"]!.GetValue<string>())!.AsArray();
                Assert.Equal($"ada-{token}", Assert.Single(members)!["UserName"]!.GetValue<string>());
                Assert.Equal($"[\"ada-{token}\"]", Outputs("disband")["Result"]![0]!["removed_users"]!.GetValue<string>());
                Assert.True(Outputs("leave")["Result"]![0]!["deleted"]!.GetValue<bool>());

                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(secret, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
