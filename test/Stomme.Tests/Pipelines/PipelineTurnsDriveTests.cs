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
using Stomme.Modules.Pipelines.Steps;
using Stomme.Modules.Pipelines.Triggers;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - one at a time, the rest in turn, DRIVEN over HTTP on an installed SQLite host: three webhook calls to a pipeline whose
    /// run waits a few seconds are three runs - one going, two Queued - that run one after the other, those waiting in the order they were sent, each
    /// with the body it was called with; Run while one is going is Queued too; never are two going at once. Before, "one at a time"
    /// answered the second call 202 and ran nothing.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class PipelineTurnsDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Secret = "turns-webhook-secret";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task Calls_while_a_run_is_going_wait_their_turn_and_run_one_after_the_other_oldest_first()
        {
            var settings = new JsonObject
            {
                ["Pipelines"] = new JsonObject
                {
                    ["Enabled"] = true,
                    ["Webhooks"] = new JsonObject { ["Enabled"] = true, ["Secret"] = Secret },
                    ["Waits"] = new JsonObject { ["IntervalSeconds"] = 5 },
                    ["Runs"] = new JsonObject { ["TurnsIntervalSeconds"] = 2 },
                },
            };
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: settings);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                var definitionId = await PublishAsync(app, client, cookies, hostId);
                using var caller = GateDrive.NewClient(app, Host);

                // Three calls at once: one runs, two wait their turn - each a run, Queued.
                foreach (var n in new[] { 1, 2, 3 })
                {
                    var body = Encoding.UTF8.GetBytes("{\"n\":" + n + "}");
                    Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(caller, body)).StatusCode);
                }

                // The first is stored before it runs, so it reads Pending too until its first burst of work is stored (seen under the
                // gate's load: all three Pending for a moment) - wait for it to be going, then two wait.
                var runs = await UntilAsync(client, cookies, definitionId, found => found.Count == 3 && found.Any(run => run.SubStatus != "Pending"), "three runs, one going");
                Assert.Equal(2, runs.Count(run => run.SubStatus == "Pending"));
                var queued = JsonSerializer.Deserialize<List<PipelineRunView>>((await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs?definitionId={definitionId}&state=queued", output, "queued")).Body, Json)!;
                Assert.Equal(2, queued.Count);

                // Run while one is going waits its turn too.
                var started = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"/api/Pipelines/{definitionId}/runs", new StartPipelineRequest(), output, "run");
                Assert.Equal(HttpStatusCode.OK, started.Status);
                var byRun = JsonSerializer.Deserialize<PipelineRunStarted>(started.Body, Json)!;
                Assert.True(byRun.Queued);

                // They run one after the other: never two going at once, all four finished.
                var mostGoing = 0;
                var deadline = DateTime.UtcNow.AddSeconds(120);
                while (true)
                {
                    runs = await RunsAsync(client, cookies, definitionId);
                    mostGoing = Math.Max(mostGoing, runs.Count(run => !run.IsFinished && run.SubStatus != "Pending"));
                    if (runs.Count == 4 && runs.All(run => run.IsFinished))
                    {
                        break;
                    }

                    Assert.True(DateTime.UtcNow < deadline, "The runs did not all finish: " + string.Join(", ", runs.Select(run => run.SubStatus)));
                    await Task.Delay(250);
                }

                Assert.Equal(1, mostGoing);
                Assert.All(runs, run => Assert.Equal("Finished", run.SubStatus));

                // In the order they ran (one at a time, so the order they finished): Run's last; each call ran once, with the body it
                // was called with. The first to start is whichever call the engine took first - it takes calls on more than one worker
                // - and the ones that waited went in the order they were SENT: each call's arrival is stamped as it is handed to the
                // engine (on UAT, 10.36.0, three calls sent one after another were stored, and so run, in reverse).
                var inOrder = runs.OrderBy(run => run.FinishedOnUtc).ToList();
                foreach (var run in inOrder)
                {
                    output.WriteLine($"run {run.Id} created {run.CreatedOnUtc:O} finished {run.FinishedOnUtc:O}");
                }

                Assert.Equal(byRun.RunId, inOrder[3].Id);
                var bodies = new List<int>();
                foreach (var run in inOrder.Take(3))
                {
                    var detail = JsonNode.Parse((await GateDrive.GetAsync(client, cookies, Host, "/api/Pipelines/runs/" + run.Id, output, "run " + run.Id)).Body)!;
                    var input = JsonNode.Parse(detail["inputJson"]!.GetValue<string>())!;
                    var payload = input[Webhook.PayloadInputKey]!; // kept as the JSON it was, or as its text
                    var called = payload is JsonValue text ? JsonNode.Parse(text.GetValue<string>())! : payload;
                    bodies.Add(called["n"]!.GetValue<int>());
                }

                Assert.Equal(new[] { 1, 2, 3 }, bodies.OrderBy(n => n).ToArray());
                var waited = bodies.Skip(1).ToList();
                Assert.Equal(waited.OrderBy(n => n).ToList(), waited);

                // Which order the engine takes the calls in is chance, so the order above can hold without the stamp: the host's
                // dispatcher IS the stamping one.
                var dispatcher = await GateDrive.InTenantAsync(app, Host, (services, _) =>
                    Task.FromResult(services.GetRequiredService<Stomme.Engine.Pipelines.Runtime.IStimulusDispatcher>()));
                Assert.IsType<Stomme.Modules.Pipelines.Reliability.ArrivalStampingStimulusDispatcher>(dispatcher);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private async Task<string> PublishAsync(WebApplication app, HttpClient client, System.Net.CookieContainer cookies, int hostId)
        {
            var created = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "In turn" }, output, "create");
            var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
            var graph = new PipelineGraph
            {
                RunsAtOnce = PipelineRunsAtOnce.OneInTurn,
                Steps =
                {
                    new PipelineGraphStep { Id = "w", Type = "Stomme.Webhook", Name = "Orders", Inputs = { ["WebhookName"] = "orders" } },
                    new PipelineGraphStep { Id = "wait", Type = "Stomme.Wait", Name = "A moment", Inputs = { ["For"] = "2s" } },
                    new PipelineGraphStep { Id = "log", Type = "Stomme.LogMessage", Name = "Log", Inputs = { ["Message"] = "done", ["Level"] = "Information" } },
                },
                Connections = { new PipelineGraphConnection { SourceId = "w", TargetId = "wait" }, new PipelineGraphConnection { SourceId = "wait", TargetId = "log" } },
            };
            Assert.Equal(HttpStatusCode.OK, (await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Put, $"/api/Pipelines/{definitionId}/graph", graph, output, "graph")).Status);
            Assert.Equal(HttpStatusCode.OK, (await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"/api/Pipelines/{definitionId}/publish", new { }, output, "publish")).Status);
            return definitionId;
        }

        private async Task<List<PipelineRunView>> RunsAsync(HttpClient client, System.Net.CookieContainer cookies, string definitionId) =>
            JsonSerializer.Deserialize<List<PipelineRunView>>((await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs?definitionId={definitionId}", output, "runs")).Body, Json)!;

        private async Task<List<PipelineRunView>> UntilAsync(HttpClient client, System.Net.CookieContainer cookies, string definitionId, Func<List<PipelineRunView>, bool> done, string what)
        {
            for (var attempt = 0; attempt < 300; attempt++)
            {
                var runs = await RunsAsync(client, cookies, definitionId);
                if (done(runs))
                {
                    return runs;
                }

                await Task.Delay(100);
            }

            throw new Xunit.Sdk.XunitException("Not reached: " + what);
        }

        private static async Task<HttpResponseMessage> PostAsync(HttpClient client, byte[] body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri("http://" + Host), "/api/Pipelines/webhooks/orders"));
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            request.Headers.TryAddWithoutValidation(PipelineWebhooks.SignatureHeaderName, PipelineWebhooks.ComputeSignature(Secret, body));
            return await client.SendAsync(request);
        }
    }
}
