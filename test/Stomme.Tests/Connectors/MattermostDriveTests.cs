#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Infrastructure.Connectivity;
using Stomme.Models.Pipelines;
using Stomme.Modules.Connectors.Messaging;
using Stomme.Tests.Pipelines;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - the rest of n8n's Mattermost node DRIVEN on an installed SQLite host, with the REAL connector, the host's egress
    /// and the extraction port, against this machine's throwaway Mattermost Team Edition 10.11 (scripts/mattermost-test-server.ps1)
    /// - fail-not-skip without it (D46): a Mattermost connection made over the API, its token vaulted, tested; a published
    /// pipeline making a channel in the team by its name, posting in it, reacting to the post and reading its reactions - each an
    /// expression over the step before; the token in neither the graph nor the run. The channel is archived again afterwards.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class MattermostDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_makes_a_channel_posts_in_it_and_reacts_to_the_post()
        {
            var token = MattermostTestEndpoint.Token;
            var marker = Guid.NewGuid().ToString("N")[..8];
            string? channel = null;
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

                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/Connections/egress", new ConnectionEgressRequest { AllowedHosts = new Uri(MattermostTestEndpoint.Endpoint).Host }, "the Host allows Mattermost")).Status);
                var made = await Send(HttpMethod.Post, "/api/Connections", new ConnectionRequest
                {
                    Name = "Kafé chatt", ConnectorType = "mattermost", Endpoint = MattermostTestEndpoint.Endpoint, TimeoutSeconds = 60,
                    Credentials = new() { ["token"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Beställningskanal" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string operation, string variable) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation }, OutputVariables = { ["Result"] = variable } };
                var room = Call("room", "channel_create", "v-channel");
                room.Inputs["Parameters"] = $"team = kafe\nname = order-{marker}\ndisplay_name = Beställningar {marker}";
                var post = Call("post", "message", "v-post");
                post.InputExpressions["Parameters"] = $"to = {{{{ channels[0].id }}}}\ntext = Ny beställning {marker}";
                var react = Call("react", "reaction_create", "v-reaction");
                react.InputExpressions["Parameters"] = "post = {{ posts[0].id }}\nemoji = coffee";
                var read = Call("read", "reactions", "v-reactions");
                read.InputExpressions["Parameters"] = "post = {{ posts[0].id }}";
                var graph = new PipelineGraph
                {
                    Variables =
                    {
                        new PipelineGraphVariable { Id = "v-channel", Name = "channels" }, new PipelineGraphVariable { Id = "v-post", Name = "posts" },
                        new PipelineGraphVariable { Id = "v-reaction", Name = "reaction" }, new PipelineGraphVariable { Id = "v-reactions", Name = "reactions" },
                    },
                    Steps = { room, post, react, read },
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
                    var answer = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}", output, "the run");
                    run = answer.Status == HttpStatusCode.OK ? JsonSerializer.Deserialize<PipelineRunView>(answer.Body, Json) : null;
                    await Task.Delay(100);
                }

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                JsonNode? Outputs(string id) => JsonNode.Parse(steps)!.AsArray().SingleOrDefault(step => step!["stepId"]!.GetValue<string>() == id) is { } step ? JsonNode.Parse(step["outputsJson"]!.GetValue<string>()) : null;
                channel = Outputs("room")?["Result"]?[0]?["id"]?.GetValue<string>();
                Assert.True(run?.SubStatus == "Finished", steps);
                Assert.Equal("order-" + marker, Outputs("room")!["Result"]![0]!["name"]!.GetValue<string>());
                Assert.Equal(channel, Outputs("post")!["Result"]![0]!["to"]!.GetValue<string>());
                var reaction = Assert.Single(Outputs("read")!["Result"]!.AsArray())!;
                Assert.Equal(("coffee", Outputs("post")!["Result"]![0]!["id"]!.GetValue<string>()), (reaction["emoji_name"]!.GetValue<string>(), reaction["post_id"]!.GetValue<string>()));
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
                if (channel is { } id)
                {
                    await Contexts.DrainAsync(new MattermostConnector(), Contexts.Extract(new HostConnectorEgress(new[] { new Uri(MattermostTestEndpoint.Endpoint).Host }), MattermostTestEndpoint.Endpoint, "channel_delete",
                        new Dictionary<string, string> { ["channel"] = id }, new Dictionary<string, string>(), new Dictionary<string, string> { ["token"] = token }, timeoutSeconds: 60));
                }
            }
        }
    }
}
