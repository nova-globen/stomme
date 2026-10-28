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
using Stomme.Modules.Connectors.Wekan;
using Stomme.Tests.Pipelines;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Wekan DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port, against
    /// this machine's throwaway Wekan 12 (scripts/wekan-test-server.ps1) - fail-not-skip without it (D46): a Wekan connection made over
    /// the API, its password vaulted, tested; a published pipeline making a board, a list on it, a card in the list - each an
    /// expression over the step before - and a comment on the card; the password in neither the graph nor the run. The board is
    /// deleted again afterwards.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class WekanDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_makes_a_board_a_list_a_card_and_a_comment()
        {
            var password = WekanTestEndpoint.Password;
            var marker = Guid.NewGuid().ToString("N")[..10];
            string? board = null;
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

                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/Connections/egress", new ConnectionEgressRequest { AllowedHosts = new Uri(WekanTestEndpoint.Endpoint).Host }, "the Host allows Wekan")).Status);
                var made = await Send(HttpMethod.Post, "/api/Connections", new ConnectionRequest
                {
                    Name = "Kafé tavla", ConnectorType = "wekan", Endpoint = WekanTestEndpoint.Endpoint, TimeoutSeconds = 60,
                    Credentials = new() { ["username"] = WekanTestEndpoint.Username, ["password"] = password },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kassans tavla" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string operation, string variable) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation }, OutputVariables = { ["Result"] = variable } };
                var makeBoard = Call("board", "board_create", "v-board");
                makeBoard.Inputs["Parameters"] = $"title = Kafe {marker}";
                var makeList = Call("list", "list_create", "v-list");
                makeList.InputExpressions["Parameters"] = "board = {{ boards[0].id }}\ntitle = Att göra";
                var makeCard = Call("card", "card_create", "v-card");
                makeCard.InputExpressions["Parameters"] = "board = {{ boards[0].id }}\nlist = {{ lists[0].id }}\ntitle = Beställ kaffe";
                var comment = Call("comment", "comment_create", "v-comment");
                comment.InputExpressions["Parameters"] = "board = {{ boards[0].id }}\ncard = {{ cards[0].id }}\ncomment = Klart på fredag";
                var graph = new PipelineGraph
                {
                    Variables =
                    {
                        new PipelineGraphVariable { Id = "v-board", Name = "boards" }, new PipelineGraphVariable { Id = "v-list", Name = "lists" },
                        new PipelineGraphVariable { Id = "v-card", Name = "cards" }, new PipelineGraphVariable { Id = "v-comment", Name = "comments" },
                    },
                    Steps = { makeBoard, makeList, makeCard, comment },
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
                JsonNode Outputs(string id) => JsonNode.Parse(JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>())!;
                board = Outputs("board")["Result"]?[0]?["id"]?.GetValue<string>();
                Assert.True(run?.SubStatus == "Finished", steps);
                Assert.Equal($"Kafe {marker}", Outputs("board")["Result"]![0]!["title"]!.GetValue<string>());
                Assert.Equal("Att göra", Outputs("list")["Result"]![0]!["title"]!.GetValue<string>());
                Assert.Equal("Beställ kaffe", Outputs("card")["Result"]![0]!["title"]!.GetValue<string>());
                Assert.Equal("Klart på fredag", Outputs("comment")["Result"]![0]!["comment"]!.GetValue<string>());

                // Read back from Wekan itself: the card is in the list on the board, the comment on it.
                var cards = await Contexts.DrainAsync(new WekanConnector(), Contexts.Extract(new HostConnectorEgress(new[] { new Uri(WekanTestEndpoint.Endpoint).Host }), WekanTestEndpoint.Endpoint, "cards",
                    new Dictionary<string, string> { ["board"] = board!, ["list"] = Outputs("list")["Result"]![0]!["id"]!.GetValue<string>() }, new Dictionary<string, string>(), new Dictionary<string, string> { ["username"] = WekanTestEndpoint.Username, ["password"] = password }, timeoutSeconds: 60));
                Assert.Equal(Outputs("card")["Result"]![0]!["id"]!.GetValue<string>(), (string?)Assert.Single(cards).Fields["id"]);

                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(password, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(password, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
                if (board is not null)
                {
                    await Contexts.DrainAsync(new WekanConnector(), Contexts.Extract(new HostConnectorEgress(new[] { new Uri(WekanTestEndpoint.Endpoint).Host }), WekanTestEndpoint.Endpoint, "board_delete",
                        new Dictionary<string, string> { ["board"] = board }, new Dictionary<string, string>(), new Dictionary<string, string> { ["username"] = WekanTestEndpoint.Username, ["password"] = password }, timeoutSeconds: 60));
                }
            }
        }
    }
}
