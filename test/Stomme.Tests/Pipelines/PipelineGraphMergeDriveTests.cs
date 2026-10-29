#nullable enable
using System;
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
    /// The collaborating save DRIVEN over HTTP on an installed SQLite host with TWO signed-in
    /// administrators, through the real engine: a save on an unchanged graph applies; a save on a stale base keeps the other
    /// editor's change beside its own; a name both changed is REJECTED with the stored value and nothing stored; a multi-line
    /// input both changed is overwritten last-writer-wins with a warning; a step one removed that the other still used is
    /// rejected; the plain save still overwrites as it always has; a plain user and a missing pipeline are refused.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class PipelineGraphMergeDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task Two_editors_saving_one_pipeline_are_merged_field_by_field_or_refused_and_told_why()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false,
                extraSettings: new JsonObject { ["Pipelines"] = new JsonObject { ["Enabled"] = true } });
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var ada = GateDrive.NewClient(app, Host);
                var adaCookies = await GateDrive.LoginAsync(app, ada, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var adaId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                await GateDrive.AddUserAsync(app, Host, GateDrive.GateUsername, GateDrive.GatePassword, GateDrive.GateEmail, "Administrators");
                using var bo = GateDrive.NewClient(app, Host);
                var boCookies = await GateDrive.LoginAsync(app, bo, Host, GateDrive.GateUsername, GateDrive.GatePassword, output);
                var boId = await GateDrive.UserIdAsync(app, Host, GateDrive.GateUsername);

                var created = Ok<PipelineView>(await Send(app, ada, adaCookies, adaId, HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Shared" }, "create"), "create");
                var graphUrl = $"/api/Pipelines/{created.DefinitionId}/graph";
                var mergeUrl = graphUrl + "/merge";
                var first = new PipelineGraph
                {
                    Steps =
                    {
                        new PipelineGraphStep { Id = "say", Type = "Stomme.WriteLine", Name = "Say", Position = new GraphPosition { X = 10, Y = 10 }, Inputs = { ["Text"] = "hello" } },
                        new PipelineGraphStep { Id = "log", Type = "Stomme.LogMessage", Name = "Log", Position = new GraphPosition { X = 250, Y = 10 }, Inputs = { ["Message"] = "first", ["Level"] = "Information" } },
                        new PipelineGraphStep { Id = "done", Type = "Stomme.Finish", Name = "Done", Position = new GraphPosition { X = 500, Y = 10 } },
                    },
                    Connections =
                    {
                        new PipelineGraphConnection { SourceId = "say", TargetId = "log" },
                        new PipelineGraphConnection { SourceId = "log", TargetId = "done" },
                    },
                };
                Ok<PipelineView>(await Send(app, ada, adaCookies, adaId, HttpMethod.Put, graphUrl, first, "the first graph"), "the first graph");

                // Both open the editor on the same graph.
                var adaBase = await Get<PipelineGraph>(ada, adaCookies, graphUrl, "Ada loads");
                var boBase = await Get<PipelineGraph>(bo, boCookies, graphUrl, "Bo loads");

                // Nothing changed underneath: Ada's save applies exactly as drawn.
                var adaEdit = Copy(adaBase);
                adaEdit.Steps.Single(step => step.Id == "say").Name = "Say hello";
                var applied = Ok<GraphSaveResult>(await Send(app, ada, adaCookies, adaId, HttpMethod.Put, mergeUrl, new GraphSaveRequest { Graph = adaEdit, Base = adaBase }, "Ada saves"), "Ada saves");
                Assert.Equal("Applied", applied.Status);
                adaBase = applied.Graph;

                // Bo, on the stale base, changes a DIFFERENT step: merged - Ada's rename kept beside Bo's level and move.
                var boEdit = Copy(boBase);
                var boLog = boEdit.Steps.Single(step => step.Id == "log");
                boLog.Inputs["Level"] = "Warning";
                boLog.Position = new GraphPosition { X = 260, Y = 80 };
                var merged = Ok<GraphSaveResult>(await Send(app, bo, boCookies, boId, HttpMethod.Put, mergeUrl, new GraphSaveRequest { Graph = boEdit, Base = boBase }, "Bo saves on a stale base"), "Bo saves on a stale base");
                Assert.Equal("Merged", merged.Status);
                Assert.Empty(merged.Warnings);
                var stored = await Get<PipelineGraph>(ada, adaCookies, graphUrl, "the merged graph");
                Assert.Equal("Say hello", stored.Steps.Single(step => step.Id == "say").Name);
                Assert.Equal(("Warning", 260d, 80d), (stored.Steps.Single(step => step.Id == "log").Inputs["Level"], stored.Steps.Single(step => step.Id == "log").Position.X, stored.Steps.Single(step => step.Id == "log").Position.Y));
                Assert.Equal(2, stored.Connections.Count);
                boBase = merged.Graph;

                // Ada, on HER stale base, renames the step Bo... no: the step she renamed is untouched by Bo; she renames "log",
                // which Bo did not rename either - applied as a merge. Then Bo, stale again, renames "log" differently: REJECTED.
                var adaRename = Copy(adaBase);
                adaRename.Steps.Single(step => step.Id == "log").Name = "Record";
                Assert.Equal("Merged", (Ok<GraphSaveResult>(await Send(app, ada, adaCookies, adaId, HttpMethod.Put, mergeUrl, new GraphSaveRequest { Graph = adaRename, Base = adaBase }, "Ada renames the log step"), "Ada renames the log step")).Status);
                var boRename = Copy(boBase);
                boRename.Steps.Single(step => step.Id == "log").Name = "Audit";
                var refused = await Send(app, bo, boCookies, boId, HttpMethod.Put, mergeUrl, new GraphSaveRequest { Graph = boRename, Base = boBase }, "Bo renames it too");
                Assert.Equal(HttpStatusCode.Conflict, refused.Status);
                var rejection = JsonSerializer.Deserialize<GraphSaveResult>(refused.Body, Json)!;
                Assert.Equal("Rejected", rejection.Status);
                var conflict = Assert.Single(rejection.Conflicts);
                Assert.Equal(("step/log/name", "Audit", "Record"), (conflict.Path, conflict.ProposedValue, conflict.CurrentValue));
                Assert.Equal("step 'Record', its name", PipelineGraphFields.Describe(conflict.Path, rejection.Graph));
                Assert.Equal("Record", (await Get<PipelineGraph>(ada, adaCookies, graphUrl, "after the rejection")).Steps.Single(step => step.Id == "log").Name);

                // A multi-line input both changed: the last writer wins, and the overwrite is reported.
                var boneBase = rejection.Graph;
                var adaMessage = Copy(boneBase);
                adaMessage.Steps.Single(step => step.Id == "log").Inputs["Message"] = "Ada's message";
                adaBase = (Ok<GraphSaveResult>(await Send(app, ada, adaCookies, adaId, HttpMethod.Put, mergeUrl, new GraphSaveRequest { Graph = adaMessage, Base = boneBase }, "Ada writes the message"), "Ada writes the message")).Graph;
                var boMessage = Copy(boneBase);
                boMessage.Steps.Single(step => step.Id == "log").Inputs["Message"] = "Bo's message";
                var overwritten = Ok<GraphSaveResult>(await Send(app, bo, boCookies, boId, HttpMethod.Put, mergeUrl, new GraphSaveRequest { Graph = boMessage, Base = boneBase }, "Bo writes it too"), "Bo writes it too");
                Assert.Equal("Merged", overwritten.Status);
                var warning = Assert.Single(overwritten.Warnings);
                Assert.Equal(("step/log/input/Message", "Bo's message", "Ada's message", boId), (warning.Path, warning.WinningValue, warning.OverwrittenValue, warning.WinningAuthorUserId));
                Assert.Equal("Bo's message", (await Get<PipelineGraph>(ada, adaCookies, graphUrl, "after the overwrite")).Steps.Single(step => step.Id == "log").Inputs["Message"]);
                boBase = overwritten.Graph;

                // Ada removes the finish step; Bo, stale, connects a new step to it: rejected, the step is gone.
                var adaRemove = Copy(boBase);
                adaRemove.Steps.RemoveAll(step => step.Id == "done");
                adaRemove.Connections.RemoveAll(connection => connection.TargetId == "done");
                Assert.Equal("Applied", (Ok<GraphSaveResult>(await Send(app, ada, adaCookies, adaId, HttpMethod.Put, mergeUrl, new GraphSaveRequest { Graph = adaRemove, Base = boBase }, "Ada removes a step"), "Ada removes a step")).Status);
                var boUses = Copy(boBase);
                boUses.Steps.Add(new PipelineGraphStep { Id = "tail", Type = "Stomme.WriteLine", Name = "Tail", Inputs = { ["Text"] = "bye" } });
                boUses.Connections.Add(new PipelineGraphConnection { SourceId = "done", TargetId = "tail" });
                var dangling = await Send(app, bo, boCookies, boId, HttpMethod.Put, mergeUrl, new GraphSaveRequest { Graph = boUses, Base = boBase }, "Bo uses the removed step");
                Assert.Equal(HttpStatusCode.Conflict, dangling.Status);
                Assert.Equal("connection/done//tail", Assert.Single(JsonSerializer.Deserialize<GraphSaveResult>(dangling.Body, Json)!.Conflicts).Path);
                Assert.DoesNotContain((await Get<PipelineGraph>(ada, adaCookies, graphUrl, "after the dangling refusal")).Steps, step => step.Id == "tail");

                // The plain save is unchanged: the last one simply wins, as it always has.
                Assert.Equal(HttpStatusCode.OK, (await Send(app, bo, boCookies, boId, HttpMethod.Put, graphUrl, boUses, "Bo's plain save")).Status);
                Assert.Contains((await Get<PipelineGraph>(ada, adaCookies, graphUrl, "after the plain save")).Steps, step => step.Id == "tail");

                // A plain user is refused; a pipeline that does not exist is 404.
                await GateDrive.AddUserAsync(app, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, GateDrive.PlainEmail);
                using var plain = GateDrive.NewClient(app, Host);
                var plainCookies = await GateDrive.LoginAsync(app, plain, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, output);
                var plainId = await GateDrive.UserIdAsync(app, Host, GateDrive.PlainUsername);
                Assert.Equal(HttpStatusCode.Forbidden, (await Send(app, plain, plainCookies, plainId, HttpMethod.Put, mergeUrl, new GraphSaveRequest { Graph = boUses, Base = boBase }, "a plain user")).Status);
                Assert.Equal(HttpStatusCode.NotFound, (await Send(app, ada, adaCookies, adaId, HttpMethod.Put, "/api/Pipelines/no-such-pipeline/graph/merge", new GraphSaveRequest(), "a missing pipeline")).Status);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static PipelineGraph Copy(PipelineGraph graph) => JsonSerializer.Deserialize<PipelineGraph>(JsonSerializer.Serialize(graph, Json), Json)!;

        private Task<(HttpStatusCode Status, string Body)> Send(Microsoft.AspNetCore.Builder.WebApplication app, HttpClient client, CookieContainer cookies, int userId, HttpMethod method, string path, object body, string label) =>
            GateDrive.SendJsonAsync(app, client, cookies, Host, userId, method, path, body, output, label);

        private async Task<T> Get<T>(HttpClient client, CookieContainer cookies, string path, string label)
        {
            var answer = await GateDrive.GetAsync(client, cookies, Host, path, output, label);
            return Ok<T>((answer.Status, answer.Body), label);
        }

        private static T Ok<T>((HttpStatusCode Status, string Body) answer, string label)
        {
            Assert.True(answer.Status == HttpStatusCode.OK, $"[{label}] {(int)answer.Status}: {GateDrive.Truncate(answer.Body)}");
            return JsonSerializer.Deserialize<T>(answer.Body, Json)!;
        }
    }
}
