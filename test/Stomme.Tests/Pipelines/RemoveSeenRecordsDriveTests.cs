#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Triggers;
using Microsoft.AspNetCore.Builder;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// Remove records seen before DRIVEN over HTTP on an installed SQLite host: a webhook pipeline called twice with
    /// overlapping records passes on what is new each time - what the first call had is gone from the second's result, through
    /// the host's own key-value store and lock - and a step test of it reads what the pipeline has seen and remembers nothing.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class RemoveSeenRecordsDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Secret = "seen-webhook-secret";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_second_call_passes_on_only_what_the_first_had_not_and_a_step_test_remembers_nothing()
        {
            var settings = new JsonObject
            {
                ["Pipelines"] = new JsonObject { ["Enabled"] = true, ["Webhooks"] = new JsonObject { ["Enabled"] = true, ["Secret"] = Secret } },
            };
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: settings);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        new PipelineGraphStep { Id = "w", Type = "Stomme.Webhook", Name = "Orders", Inputs = { ["WebhookName"] = "orders" } },
                        new PipelineGraphStep { Id = "seen", Type = "Stomme.RemoveSeenRecords", Name = "Only new", Inputs = { ["CompareFields"] = "id" } },
                    },
                    Connections = { new PipelineGraphConnection { SourceId = "w", TargetId = "seen" } },
                };
                var created = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "New orders" }, output, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                Assert.Equal(HttpStatusCode.OK, (await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Put, $"/api/Pipelines/{definitionId}/graph", graph, output, "graph")).Status);
                Assert.Equal(HttpStatusCode.OK, (await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"/api/Pipelines/{definitionId}/publish", new { }, output, "publish")).Status);
                using var caller = GateDrive.NewClient(app, Host);

                Assert.Equal(new[] { "1", "2" }, await CallAsync(caller, client, cookies, definitionId, 1, "[{\"id\":\"1\"},{\"id\":\"2\"}]"));
                Assert.Equal(new[] { "3" }, await CallAsync(caller, client, cookies, definitionId, 2, "[{\"id\":\"2\"},{\"id\":\"3\"},{\"id\":\"1\"}]"));

                // A step test reads what the pipeline has seen and remembers nothing: 4 is new to it, and new again to the next call.
                graph.Steps.Single(step => step.Id == "seen").Inputs["Records"] = "[{\"id\":\"3\"},{\"id\":\"4\"}]";
                var test = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"/api/Pipelines/{definitionId}/test-step",
                    new StepTestRequest { Graph = graph, StepId = "seen" }, output, "test step");
                Assert.Equal(HttpStatusCode.OK, test.Status);
                var tested = JsonSerializer.Deserialize<StepTestResult>(test.Body, Json)!;
                Assert.Equal(new[] { "4" }, Ids(tested.Steps.Single(step => step.StepId == "seen").OutputsJson));
                Assert.Equal(new[] { "4" }, await CallAsync(caller, client, cookies, definitionId, 3, "[{\"id\":\"3\"},{\"id\":\"4\"}]"));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        // The ids of the records a step's Result holds - kept as JSON, or as its text.
        private static string[] Ids(string outputsJson)
        {
            var result = JsonNode.Parse(outputsJson)!["Result"]!;
            var rows = result is JsonValue text ? JsonNode.Parse(text.GetValue<string>())! : result;
            return rows.AsArray().Select(row => row!["id"]!.GetValue<string>()).ToArray();
        }

        // Calls the webhook, waits for its run, and answers the ids the step passed on.
        private async Task<string[]> CallAsync(HttpClient caller, HttpClient client, System.Net.CookieContainer cookies, string definitionId, int runs, string records)
        {
            var body = Encoding.UTF8.GetBytes(records);
            using (var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri("http://" + Host), "/api/Pipelines/webhooks/orders")))
            {
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                request.Headers.TryAddWithoutValidation(PipelineWebhooks.SignatureHeaderName, PipelineWebhooks.ComputeSignature(Secret, body));
                Assert.Equal(HttpStatusCode.Accepted, (await caller.SendAsync(request)).StatusCode);
            }

            for (var attempt = 0; attempt < 200; attempt++)
            {
                var all = JsonSerializer.Deserialize<List<PipelineRunView>>((await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs?definitionId={definitionId}", output, "runs")).Body, Json)!;
                if (all.Count >= runs && all.OrderByDescending(run => run.CreatedOnUtc).First() is { IsFinished: true } newest)
                {
                    Assert.Equal("Finished", newest.SubStatus);
                    var steps = JsonSerializer.Deserialize<List<PipelineStepRunView>>((await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{newest.Id}/steps", output, "steps")).Body, Json)!;
                    return Ids(steps.Single(step => step.StepId == "seen").OutputsJson);
                }

                await Task.Delay(100);
            }

            throw new Xunit.Sdk.XunitException($"Run {runs} of {definitionId} did not finish.");
        }
    }
}
