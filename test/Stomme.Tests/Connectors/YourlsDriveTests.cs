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
    /// BL-229 - YOURLS DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port, against
    /// this machine's throwaway YOURLS 1.10 (scripts/yourls-test-server.ps1) - fail-not-skip without it (D46): a YOURLS connection made
    /// over the API, its signature vaulted, tested; a published pipeline shortening a URL, expanding the short URL - an expression over
    /// the first step's result - and reading its stats; the signature in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class YourlsDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_shortens_a_url_expands_it_and_reads_its_stats()
        {
            var key = YourlsTestEndpoint.Signature;
            var marker = "kafe-" + Guid.NewGuid().ToString("N")[..10];
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

                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/Connections/egress", new ConnectionEgressRequest { AllowedHosts = new Uri(YourlsTestEndpoint.Endpoint).Host }, "the Host allows YOURLS")).Status);
                var made = await Send(HttpMethod.Post, "/api/Connections", new ConnectionRequest
                {
                    Name = "Kafé länkar", ConnectorType = "yourls", Endpoint = YourlsTestEndpoint.Endpoint, TimeoutSeconds = 60,
                    Credentials = new() { ["api-key"] = key },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kassans länkar" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string operation) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation } };
                var open = Call("open", "shorten");
                open.Inputs["Parameters"] = $"url = https://kafe.example/menu?day={marker}\nkeyword = meny{marker.Replace("-", string.Empty)}\ntitle = Dagens meny";
                open.OutputVariables["Result"] = "v-case";
                var indicator = Call("indicator", "expand");
                indicator.InputExpressions["Parameters"] = "short_url = {{ opened[0].short_url }}";
                var find = Call("find", "stats");
                find.InputExpressions["Parameters"] = "short_url = {{ opened[0].keyword }}";
                var graph = new PipelineGraph { Variables = { new PipelineGraphVariable { Id = "v-case", Name = "opened" } }, Steps = { open, indicator, find } };
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
                Assert.Equal(($"meny{marker.Replace("-", string.Empty)}", $"https://kafe.example/menu?day={marker}", false), (Outputs("open")["Result"]![0]!["keyword"]!.GetValue<string>(), Outputs("open")["Result"]![0]!["url"]!.GetValue<string>(), Outputs("open")["Result"]![0]!["existing"]!.GetValue<bool>()));
                Assert.Equal($"https://kafe.example/menu?day={marker}", Outputs("indicator")["Result"]![0]!["url"]!.GetValue<string>());
                Assert.Equal(("Dagens meny", 0), (Outputs("find")["Result"]![0]!["title"]!.GetValue<string>(), Outputs("find")["Result"]![0]!["clicks"]!.GetValue<int>()));
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(key, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(key, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
