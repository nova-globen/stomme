#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Models.Pipelines;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - n8n's "save execution progress" DRIVEN on an installed SQLite host, through the host's own startup (which fills the
    /// engine's strategy registry) and its background dispatcher: a run of a pipeline that saves its progress is there to read
    /// while a Delay holds it - its first step's log line stored, the second's not yet - and a run of the same pipeline without the
    /// setting is not stored until it ends.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class SaveProgressDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_run_that_saves_its_progress_can_be_read_while_a_step_holds_it_and_one_that_does_not_only_once_it_ends()
        {
            var settings = new JsonObject { ["Pipelines"] = new JsonObject { ["Enabled"] = true } };
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: settings);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                Task<(HttpStatusCode Status, string Body)> Send(HttpMethod method, string path, object body, string label) =>
                    GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, method, path, body, output, label);

                // A log line, a pause of six seconds, a log line - saved the way named (the plain save, the editor's merging save, or
                // an import), published, and a run started.
                async Task<string> StartAsync(string name, bool? saveProgress, string route)
                {
                    var graph = new PipelineGraph
                    {
                        SaveProgress = saveProgress,
                        Steps =
                        {
                            new PipelineGraphStep { Id = "first", Type = "Stomme.LogMessage", Name = "First", Inputs = { ["Level"] = "Information", ["Message"] = "first" } },
                            new PipelineGraphStep { Id = "pause", Type = "Stomme.Delay", Name = "Pause", Inputs = { ["Seconds"] = "6" } },
                            new PipelineGraphStep { Id = "second", Type = "Stomme.LogMessage", Name = "Second", Inputs = { ["Level"] = "Information", ["Message"] = "second" } },
                        },
                        Connections =
                        {
                            new PipelineGraphConnection { SourceId = "first", TargetId = "pause" },
                            new PipelineGraphConnection { SourceId = "pause", TargetId = "second" },
                        },
                    };
                    string definitionId;
                    if (route == "import")
                    {
                        var imported = await Send(HttpMethod.Post, "/api/Pipelines/import", new ImportPipelineRequest { Name = name, Graph = graph }, "import");
                        Assert.True(imported.Status == HttpStatusCode.OK, imported.Body);
                        definitionId = JsonNode.Parse(imported.Body)!["definitionId"]!.GetValue<string>();
                    }
                    else
                    {
                        var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = name }, "create");
                        definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                        var saved = route == "merge"
                            ? await Send(HttpMethod.Put, $"/api/Pipelines/{definitionId}/graph/merge", new GraphSaveRequest
                            {
                                Graph = graph,
                                Base = JsonSerializer.Deserialize<PipelineGraph>((await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body, Json)!,
                            }, "merged save")
                            : await Send(HttpMethod.Put, $"/api/Pipelines/{definitionId}/graph", graph, "graph");
                        Assert.True(saved.Status == HttpStatusCode.OK, saved.Body);
                    }

                    var published = await Send(HttpMethod.Post, $"/api/Pipelines/{definitionId}/publish", new { }, "publish");
                    Assert.True(JsonNode.Parse(published.Body)!["succeeded"]!.GetValue<bool>(), published.Body);
                    var started = await Send(HttpMethod.Post, $"/api/Pipelines/{definitionId}/runs", new StartPipelineRequest(), "run");
                    return JsonNode.Parse(started.Body)!["runId"]!.GetValue<string>();
                }

                async Task<PipelineRunView?> ReadAsync(string runId)
                {
                    var read = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}", output, "the run");
                    return read.Status == HttpStatusCode.OK ? JsonSerializer.Deserialize<PipelineRunView>(read.Body, Json) : null;
                }

                async Task<List<string>> LoggedAsync(string runId) =>
                    JsonSerializer.Deserialize<List<PipelineRunLogLine>>((await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/log", output, "the run's log")).Body, Json)!
                        .Where(line => line.StepType == "Stomme.LogMessage" && line.Message is not null).Select(line => line.Message).ToList(); // the lines, not the step's starts and ends

                // Saving its progress, whichever way it was saved: stored once its first step has run, while the pause holds it - the
                // first line there, not the second.
                var saving = new List<string>();
                foreach (var route in new[] { "put", "merge", "import" })
                {
                    var clock = Stopwatch.StartNew();
                    var runId = await StartAsync("Saves its progress - " + route, true, route);
                    PipelineRunView? stored = null;
                    while (stored is null && clock.Elapsed < TimeSpan.FromSeconds(5))
                    {
                        stored = await ReadAsync(runId);
                        await Task.Delay(100);
                    }

                    output.WriteLine($"{route}: stored after {clock.Elapsed.TotalSeconds:0.0} s as {stored?.SubStatus}");
                    Assert.True(stored is not null, $"saved by {route}: not stored while its pause held it");
                    Assert.Equal("Executing", stored!.SubStatus); // the pause still holding it
                    Assert.Equal(["first"], await LoggedAsync(runId));
                    saving.Add(runId);
                }

                // Not saving it: a run of the same shape is not there to read while its pause holds it.
                var started = Stopwatch.StartNew();
                var plain = await StartAsync("Saves when it ends", null, "merge");
                await Task.Delay(TimeSpan.FromSeconds(2));
                Assert.Null(await ReadAsync(plain));
                output.WriteLine($"not stored after {started.Elapsed.TotalSeconds:0.0} s");

                // Each ends with both lines.
                foreach (var runId in saving.Append(plain))
                {
                    PipelineRunView? run = null;
                    for (var attempt = 0; attempt < 200 && run?.IsFinished != true; attempt++)
                    {
                        run = await ReadAsync(runId);
                        await Task.Delay(100);
                    }

                    Assert.Equal("Finished", run?.SubStatus);
                    Assert.Equal(["first", "second"], await LoggedAsync(runId));
                }
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
