#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Infrastructure.Connectivity;
using Stomme.Models.Pipelines;
using Stomme.Modules.Connectors.Shared;
using Stomme.Tests.Pipelines;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Amazon Cognito DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction
    /// port, against moto (<c>scripts/moto-test-server.ps1</c>'s throwaway on loopback), which checks every signature against the
    /// IAM key it issued: a Cognito connection made over the API with its user pool a setting, and tested; a published pipeline
    /// making a group, creating a user with no invitation sent, putting them in it, reading the group with its users, and deleting
    /// both; the secret in neither the graph nor the run. Fails, never skips, without moto (D46).
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class AwsCognitoDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_administers_a_user_pools_users_and_groups()
        {
            var secret = MotoTestEndpoint.SecretKey;
            var admin = AwsJsonSession.Of("moto", "cognito-idp", "AWSCognitoIdentityProviderService", MotoTestEndpoint.Endpoint, new System.Collections.Generic.Dictionary<string, string> { ["region"] = MotoTestEndpoint.Region },
                new System.Collections.Generic.Dictionary<string, string> { ["access-key"] = MotoTestEndpoint.AccessKey, ["secret-key"] = secret }, new HostConnectorEgress(new[] { MotoTestEndpoint.Host }), TimeSpan.FromSeconds(30), TimeProvider.System);
            var pool = (await admin.CallAsync("CreateUserPool", new JsonObject { ["PoolName"] = "kafe-drive-" + Guid.NewGuid().ToString("N")[..8] }, default))!["UserPool"]!["Id"]!.GetValue<string>();
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

                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/Connections/egress", new ConnectionEgressRequest { AllowedHosts = MotoTestEndpoint.Host }, "the Host allows loopback")).Status);
                var made = await Send(HttpMethod.Post, "/api/Connections", new ConnectionRequest
                {
                    Name = "Kafé inloggning", ConnectorType = "awscognito", Endpoint = MotoTestEndpoint.Endpoint, TimeoutSeconds = 30,
                    Settings = new() { ["region"] = MotoTestEndpoint.Region, ["user_pool"] = pool },
                    Credentials = new() { ["access-key"] = MotoTestEndpoint.AccessKey, ["secret-key"] = secret },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kassans personal" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("group", "group_create", "group = kassa\ndescription = Kassan"),
                        Call("user", "user_create", "username = ada\nattributes = {\"email\": \"ada@kafe.example\"}\nmessage_action = suppress"),
                        Call("join", "user_add_to_group", "user = ada\ngroup = kassa"),
                        Call("read", "group", "group = kassa\ninclude_users = true"),
                        Call("leave", "user_delete", "user = ada"),
                        Call("drop", "group_delete", "group = kassa"),
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
                Assert.Equal("Kassan", Outputs("group")["Result"]![0]!["Description"]!.GetValue<string>());
                Assert.Equal("ada@kafe.example", Outputs("user")["Result"]![0]!["email"]!.GetValue<string>());
                var members = JsonNode.Parse(Outputs("read")["Result"]![0]!["Users"]!.GetValue<string>())!.AsArray();
                Assert.Equal("ada", Assert.Single(members)!["Username"]!.GetValue<string>());
                Assert.True(Outputs("leave")["Result"]![0]!["deleted"]!.GetValue<bool>());
                Assert.True(Outputs("drop")["Result"]![0]!["deleted"]!.GetValue<bool>());
                Assert.Empty((await admin.CallAsync("ListGroups", new JsonObject { ["UserPoolId"] = pool }, default))!["Groups"]!.AsArray());

                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(secret, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
                await admin.CallAsync("DeleteUserPool", new JsonObject { ["UserPoolId"] = pool }, default);
            }
        }
    }
}
