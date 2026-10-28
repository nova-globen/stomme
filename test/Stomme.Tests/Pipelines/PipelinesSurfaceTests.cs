#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Models.Notifications;
using Stomme.Models.Pipelines;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-95 increment (b), driven over REAL HTTP on a scratch install: the Host authors a pipeline, publishes it, starts a
    /// run and OBSERVES it by polling (never awaited - D111 item 3), reads its log, retracts and deletes; a Registered user
    /// is refused per key; anonymous is not OK; and with the switch off the routes do not exist and no page is placed.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class PipelinesSurfaceTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task The_Host_authors_publishes_starts_observes_retracts_and_deletes_over_HTTP()
        {
            using var scratch = await ScratchAsync(enabled: true);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);

                // The admin page is placed while the switch is on.
                var page = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                    (await sp.GetRequiredService<IPageRepository>().GetPagesAsync(alias.SiteId)).SingleOrDefault(p => p.Path == "admin/pipelines"));
                Assert.NotNull(page);

                // Author.
                var refused = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "   " }, output, "a blank name");
                Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
                var created = await PostAsync<PipelineView>(app, client, cookies, hostId, "/api/Pipelines", new CreatePipelineRequest { Name = "Nightly", Description = "first" }, "create");
                Assert.Equal(("Nightly", "first", 1, false, true), (created.Name, created.Description, created.Version, created.IsPublished, created.IsLatest));

                var list = await GetAsync<List<PipelineView>>(client, cookies, "/api/Pipelines", "list");
                Assert.Contains(list, p => p.DefinitionId == created.DefinitionId && !p.IsPublished);

                var detail = await GetAsync<PipelineDetail>(client, cookies, $"/api/Pipelines/{created.DefinitionId}", "detail");
                Assert.False(string.IsNullOrEmpty(detail.Body), "a new pipeline has an empty-sequence body");

                var updated = await PutAsync<PipelineView>(app, client, cookies, hostId, $"/api/Pipelines/{created.DefinitionId}", new UpdatePipelineRequest { Name = "Nightly report", Description = "second" }, "update");
                Assert.Equal(("Nightly report", "second"), (updated.Name, updated.Description));
                var badBody = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Put, $"/api/Pipelines/{created.DefinitionId}", new UpdatePipelineRequest { Name = "Nightly report", Body = "not a pipeline" }, output, "a body the engine cannot read");
                Assert.Equal(HttpStatusCode.BadRequest, badBody.Status);

                // The editor's seam (increment (c)): the step catalog, and the graph saved as the draft's body.
                var steps = await GetAsync<List<StepCatalogEntry>>(client, cookies, "/api/Pipelines/steps", "step catalog");
                var writeLine = Assert.Single(steps, s => s.Type == "Stomme.WriteLine");
                Assert.Equal(("Console", "Text", "String", "singleline"), (writeLine.Category, writeLine.Inputs.Single().Name, writeLine.Inputs.Single().TypeName, writeLine.Inputs.Single().UIHint));
                Assert.Contains(steps, s => s.Type == "Stomme.FlowDecision" && s.Ports.SequenceEqual(new[] { "True", "False" }));
                Assert.DoesNotContain(steps, s => s.Type == "Stomme.Flowchart");
                // The step families (increment (d)) are in the catalog with their ports, dropdown options and the sensitive flag.
                var http = Assert.Single(steps, s => s.Type == "Stomme.SendHttpRequest");
                Assert.Equal(("HTTP", "HTTP request"), (http.Category, http.DisplayName));
                Assert.Equal(new[] { "Done", "Failed" }, http.Ports);
                Assert.True(http.Inputs.Single(i => i.Name == "Authorization").IsSensitive);
                Assert.False(http.Inputs.Single(i => i.Name == "Url").IsSensitive);
                Assert.Equal(new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD" }, http.Inputs.Single(i => i.Name == "Method").Options);
                Assert.Contains(steps, s => s.Type == "Stomme.ValidateRecords" && s.Ports.SequenceEqual(new[] { "Passed", "Failed" }));
                Assert.Contains(steps, s => s.Type == "Stomme.LogMessage" && s.Inputs.Single(i => i.Name == "Level").Options!.SequenceEqual(new[] { "Information", "Warning", "Error" }));
                Assert.Contains(steps, s => s.Type == "Stomme.RunScript" && s.Inputs.Single(i => i.Name == "Script").UIHint == "code-editor");
                Assert.Contains(steps, s => s.Type == "Stomme.SendNotification" && s.Category == "Notifications");
                Assert.Empty((await GetAsync<PipelineGraph>(client, cookies, $"/api/Pipelines/{created.DefinitionId}/graph", "the empty graph")).Steps);
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        new PipelineGraphStep { Id = "say", Type = "Stomme.WriteLine", Name = "Say hello", Position = new GraphPosition { X = 10, Y = 20 }, Inputs = { ["Text"] = "hello over http" } },
                        // The step families (increment (d)) through the editor's seam: a dropdown value, the records convention, a routed outcome.
                        new PipelineGraphStep { Id = "parse", Type = "Stomme.ParseCsv", Name = "Parse", Inputs = { ["Text"] = "k\nv\n" } },
                        new PipelineGraphStep { Id = "check", Type = "Stomme.ValidateRecords", Name = "Check", Inputs = { ["Rules"] = "k required", ["FailureMode"] = "failfast", ["OnFailure"] = "Continue" } },
                        new PipelineGraphStep { Id = "log", Type = "Stomme.LogMessage", Name = "Log", Inputs = { ["Message"] = "validated over http", ["Level"] = "Warning" } },
                        // A notification from a DISPATCHED run: the step must enter the site of its engine tenant on the worker.
                        new PipelineGraphStep { Id = "notify", Type = "Stomme.SendNotification", Name = "Notify", Inputs = { ["TemplateKey"] = "pipelines.surface", ["Recipients"] = "user:" + hostId, ["Channels"] = "InApp", ["OnFailure"] = "Continue" } },
                        new PipelineGraphStep { Id = "done", Type = "Stomme.Finish", Name = "Done" },
                    },
                    Connections =
                    {
                        new PipelineGraphConnection { SourceId = "say", TargetId = "parse" },
                        new PipelineGraphConnection { SourceId = "parse", SourcePort = "Done", TargetId = "check" },
                        new PipelineGraphConnection { SourceId = "check", SourcePort = "Passed", TargetId = "log" },
                        new PipelineGraphConnection { SourceId = "log", TargetId = "notify" },
                        new PipelineGraphConnection { SourceId = "notify", SourcePort = "Done", TargetId = "done" },
                    },
                };
                var saved = await PutAsync<PipelineView>(app, client, cookies, hostId, $"/api/Pipelines/{created.DefinitionId}/graph", graph, "save the graph");
                Assert.Equal(created.DefinitionId, saved.DefinitionId);
                var readBack = await GetAsync<PipelineGraph>(client, cookies, $"/api/Pipelines/{created.DefinitionId}/graph", "the graph read back");
                Assert.Equal(new[] { "say", "parse", "check", "log", "notify", "done" }, readBack.Steps.Select(s => s.Id));
                Assert.Equal("hello over http", readBack.Steps[0].Inputs["Text"]);
                Assert.Equal((10d, 20d), (readBack.Steps[0].Position.X, readBack.Steps[0].Position.Y));
                Assert.Equal("FailFast", readBack.Steps[2].Inputs["FailureMode"]);
                Assert.Equal(5, readBack.Connections.Count);
                var badOption = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Put, $"/api/Pipelines/{created.DefinitionId}/graph", new PipelineGraph { Steps = { new PipelineGraphStep { Id = "x", Type = "Stomme.LogMessage", Inputs = { ["Level"] = "Loud" } } } }, output, "a dropdown value the input cannot hold");
                Assert.Equal(HttpStatusCode.BadRequest, badOption.Status);
                var badGraph = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Put, $"/api/Pipelines/{created.DefinitionId}/graph", new PipelineGraph { Steps = { new PipelineGraphStep { Id = "x", Type = "Stomme.NoSuchStep" } } }, output, "a graph with an unknown step");
                Assert.Equal(HttpStatusCode.BadRequest, badGraph.Status);

                // The admin page and the editor action are served to the Host. No test transport renders a clean-arch Client
                // control's markup (BL-79's finding stands), so the controls themselves - the list, the canvas, the inspector -
                // are the owner's browser drive; what is pinned here is that the page and the action resolve for the Host.
                var listPage = await GateDrive.GetAsync(client, cookies, Host, "/admin/pipelines", output, "admin/pipelines");
                Assert.Equal(HttpStatusCode.OK, listPage.Status);
                Assert.Matches("<title>[^<]*Pipelines[^<]*</title>", listPage.Body);
                Assert.DoesNotContain("Installer", listPage.Body, StringComparison.Ordinal);
                var moduleId = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                    (await sp.GetRequiredService<IPageModuleRepository>().GetPageModulesAsync(alias.SiteId)).Single(pm => pm.PageId == page!.PageId).ModuleId);
                var editorPage = await GateDrive.GetAsync(client, cookies, Host, $"/admin/pipelines/{Constants.ModuleDelimiter}/{moduleId}/Editor?definition={created.DefinitionId}", output, "the editor action");
                Assert.Equal(HttpStatusCode.OK, editorPage.Status);
                Assert.DoesNotContain("Installer", editorPage.Body, StringComparison.Ordinal);

                // A draft cannot be started; publishing makes it runnable.
                var notPublished = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"/api/Pipelines/{created.DefinitionId}/runs", new StartPipelineRequest(), output, "start a draft");
                Assert.Equal(HttpStatusCode.NotFound, notPublished.Status);
                var published = await PostAsync<PublishResult>(app, client, cookies, hostId, $"/api/Pipelines/{created.DefinitionId}/publish", new { }, "publish");
                Assert.True(published.Succeeded, string.Join("; ", published.Errors));
                Assert.Contains(await GetAsync<List<PipelineView>>(client, cookies, "/api/Pipelines", "list after publish"), p => p.DefinitionId == created.DefinitionId && p.IsPublished);

                // Start = dispatch; observe by polling the run.
                var started = await PostAsync<PipelineRunStarted>(app, client, cookies, hostId, $"/api/Pipelines/{created.DefinitionId}/runs", new StartPipelineRequest { CorrelationId = "http-run", Input = new Dictionary<string, string> { ["who"] = "Ada" } }, "start");
                Assert.False(string.IsNullOrEmpty(started.RunId));
                // Read at once, as the editor does: a run just started is stored when its first burst of work ends - until then "not
                // yet" (204), never "no such run" (404, which a WebAssembly page logged as a failed request on every Run).
                Assert.Contains((await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{started.RunId}", output, "the run just started")).Status, new[] { HttpStatusCode.NoContent, HttpStatusCode.OK });
                var run = await UntilFinishedAsync(client, cookies, started.RunId);
                Assert.Equal(("Finished", "Finished", "http-run", created.DefinitionId), (run.Status, run.SubStatus, run.CorrelationId, run.DefinitionId));
                var log = await GetAsync<List<PipelineRunLogLine>>(client, cookies, $"/api/Pipelines/runs/{started.RunId}/log", "log");
                Assert.NotEmpty(log);
                // The run executed the graph that was saved: the WriteLine step is in its log.
                Assert.Contains(log, line => line.StepType == "Stomme.WriteLine" && line.StepName == "Say hello");
                // ...and the families ran through it: the records passed validation (the Passed port) and the Log step's entry is in the run log.
                Assert.Contains(log, line => line.StepType == "Stomme.LogMessage" && line.EventName == "Warning" && line.Message == "validated over http");
                Assert.Contains(log, line => line.StepType == "Stomme.ValidateRecords" && line.EventName == "Completed");
                // ...and the notification step reached the dispatcher from the worker (no Failed entry) and queued a request on the site.
                Assert.DoesNotContain(log, line => line.StepType == "Stomme.SendNotification" && line.EventName == "Failed");
                Assert.Contains(log, line => line.StepType == "Stomme.SendNotification" && line.EventName == "Completed");
                Assert.Contains(await GetAsync<List<NotificationRequestSummary>>(client, cookies, "/api/Notifications/log", "the notification log"), r => r.TemplateKey == "pipelines.surface");
                Assert.Contains(await GetAsync<List<PipelineRunView>>(client, cookies, $"/api/Pipelines/runs?definitionId={created.DefinitionId}", "runs of the pipeline"), r => r.Id == started.RunId);
                Assert.Equal(HttpStatusCode.NotFound, (await GateDrive.GetAsync(client, cookies, Host, "/api/Pipelines/runs/no-such-run", output, "unknown run")).Status);

                // BL-229: one run as n8n's execution view has it - its pipeline's name and what it was started with (the Host is
                // an author), every run listed with its pipeline's name; Retry starts the PUBLISHED version again with the same
                // input and correlation, a run it does not know is 404.
                var shown = await GetAsync<PipelineRunView>(client, cookies, $"/api/Pipelines/runs/{started.RunId}", "the run");
                Assert.Equal("Nightly report", shown.PipelineName);
                Assert.Contains("\"who\": \"Ada\"", shown.InputJson);
                Assert.Contains(await GetAsync<List<PipelineRunView>>(client, cookies, "/api/Pipelines/runs", "every run"), r => r.Id == started.RunId && r.PipelineName == "Nightly report");
                var again = await PostAsync<PipelineRunStarted>(app, client, cookies, hostId, $"/api/Pipelines/runs/{started.RunId}/retry", new { }, "retry");
                Assert.NotEqual(started.RunId, again.RunId);
                var rerun = await UntilFinishedAsync(client, cookies, again.RunId);
                Assert.Equal(("Finished", "http-run", created.DefinitionId), (rerun.SubStatus, rerun.CorrelationId, rerun.DefinitionId));
                Assert.Contains("\"who\": \"Ada\"", (await GetAsync<PipelineRunView>(client, cookies, $"/api/Pipelines/runs/{again.RunId}", "the run again")).InputJson);
                Assert.Equal(HttpStatusCode.NotFound, (await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, "/api/Pipelines/runs/no-such-run/retry", new { }, output, "retry an unknown run")).Status);

                // The pipelines list with each one's newest run, when asked - the retry's run here - and without, nothing.
                Assert.Equal(again.RunId, (await GetAsync<List<PipelineView>>(client, cookies, "/api/Pipelines?lastRuns=true", "the list with last runs")).Single(p => p.DefinitionId == created.DefinitionId).LastRun?.Id);
                Assert.Null((await GetAsync<List<PipelineView>>(client, cookies, "/api/Pipelines", "the plain list")).Single(p => p.DefinitionId == created.DefinitionId).LastRun);

                // n8n's execution filters: by state - both runs finished, neither failed - and a state the list does not know refused.
                var finished = await GetAsync<List<PipelineRunView>>(client, cookies, $"/api/Pipelines/runs?definitionId={created.DefinitionId}&state=finished", "the finished runs");
                Assert.Equal(new[] { again.RunId, started.RunId }.OrderBy(id => id), finished.Select(r => r.Id).OrderBy(id => id));
                Assert.DoesNotContain(await GetAsync<List<PipelineRunView>>(client, cookies, "/api/Pipelines/runs?state=failed", "the failed runs"), r => r.DefinitionId == created.DefinitionId);
                var unknownState = await GateDrive.GetAsync(client, cookies, Host, "/api/Pipelines/runs?state=sideways", output, "a state runs are not listed by");
                Assert.Equal(HttpStatusCode.BadRequest, unknownState.Status);
                Assert.Contains("is not a state runs are listed by", unknownState.Body);

                // Retract, delete the run, delete the pipeline.
                Assert.Equal(HttpStatusCode.NoContent, (await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"/api/Pipelines/{created.DefinitionId}/retract", null, output, "retract")).Status);
                Assert.Contains(await GetAsync<List<PipelineView>>(client, cookies, "/api/Pipelines", "list after retract"), p => p.DefinitionId == created.DefinitionId && !p.IsPublished);
                var nothingPublished = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"/api/Pipelines/runs/{started.RunId}/retry", new { }, output, "retry with nothing published");
                Assert.Equal(HttpStatusCode.BadRequest, nothingPublished.Status);
                Assert.Contains("no published version", nothingPublished.Body);
                Assert.Equal(HttpStatusCode.NoContent, (await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Delete, $"/api/Pipelines/runs/{started.RunId}", null, output, "delete the run")).Status);
                Assert.Equal(HttpStatusCode.NotFound, (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{started.RunId}", output, "the deleted run")).Status);
                Assert.Equal(HttpStatusCode.NoContent, (await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Delete, $"/api/Pipelines/{created.DefinitionId}", null, output, "delete the pipeline")).Status);
                Assert.Equal(HttpStatusCode.NotFound, (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{created.DefinitionId}", output, "the deleted pipeline")).Status);
                Assert.Equal(HttpStatusCode.NotFound, (await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Delete, $"/api/Pipelines/{created.DefinitionId}", null, output, "delete it again")).Status);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        // BL-229: a sub-pipeline's run (Execute Pipeline) names the run that started it, and that run lists the runs it started -
        // n8n's links between a workflow's execution and its sub-workflows'.
        [Fact]
        public async Task A_sub_pipelines_run_and_the_run_that_started_it_name_each_other()
        {
            using var scratch = await ScratchAsync(enabled: true);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);

                async Task<string> PublishedAsync(string name, PipelineGraph graph)
                {
                    var pipeline = await PostAsync<PipelineView>(app, client, cookies, hostId, "/api/Pipelines", new CreatePipelineRequest { Name = name }, "create " + name);
                    await PutAsync<PipelineView>(app, client, cookies, hostId, $"/api/Pipelines/{pipeline.DefinitionId}/graph", graph, "save " + name);
                    Assert.True((await PostAsync<PublishResult>(app, client, cookies, hostId, $"/api/Pipelines/{pipeline.DefinitionId}/publish", new { }, "publish " + name)).Succeeded);
                    return pipeline.DefinitionId;
                }

                var child = await PublishedAsync("Send one report", new PipelineGraph { Steps = { new PipelineGraphStep { Id = "say", Type = "Stomme.WriteLine", Inputs = { ["Text"] = "a report" } } } });
                var parent = await PublishedAsync("Send every report", new PipelineGraph
                {
                    Steps = { new PipelineGraphStep { Id = "each", Type = "Stomme.ExecutePipeline", Name = "Send one", Inputs = { ["PipelineDefinitionId"] = child } } },
                });

                var started = await PostAsync<PipelineRunStarted>(app, client, cookies, hostId, $"/api/Pipelines/{parent}/runs", new StartPipelineRequest(), "start the parent");
                var parentRun = await UntilFinishedAsync(client, cookies, started.RunId);
                Assert.Equal("Finished", parentRun.SubStatus);
                var childRun = Assert.Single(parentRun.ChildRuns);
                Assert.Equal(("Send one report", child), (childRun.PipelineName, childRun.DefinitionId));

                var childView = await UntilFinishedAsync(client, cookies, childRun.Id);
                Assert.Equal((started.RunId, "Send every report"), (childView.ParentRunId, childView.ParentPipelineName));
                Assert.Empty(childView.ChildRuns);
                Assert.Null(parentRun.ParentRunId);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task A_Registered_user_is_refused_per_key_and_anonymous_is_not_OK()
        {
            using var scratch = await ScratchAsync(enabled: true);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                // A plain Registered user: AddUser grants that role itself.
                await GateDrive.AddUserAsync(app, Host, "bl95-user", "Bl95!User$2026", "bl95-user@gate.test");
                using var userClient = GateDrive.NewClient(app, Host);
                var userCookies = await GateDrive.LoginAsync(app, userClient, Host, "bl95-user", "Bl95!User$2026", output);
                var userId = await GateDrive.UserIdAsync(app, Host, "bl95-user");
                Assert.Equal(HttpStatusCode.Forbidden, (await GateDrive.GetAsync(userClient, userCookies, Host, "/api/Pipelines", output, "a registered user lists")).Status);
                Assert.Equal(HttpStatusCode.Forbidden, (await GateDrive.GetAsync(userClient, userCookies, Host, "/api/Pipelines/runs", output, "a registered user lists runs")).Status);
                Assert.Equal(HttpStatusCode.Forbidden, (await GateDrive.SendJsonAsync(app, userClient, userCookies, Host, userId, HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "x" }, output, "a registered user creates")).Status);
                Assert.Equal(HttpStatusCode.Forbidden, (await GateDrive.SendJsonAsync(app, userClient, userCookies, Host, userId, HttpMethod.Post, "/api/Pipelines/any/runs", new StartPipelineRequest(), output, "a registered user starts")).Status);
                Assert.Equal(HttpStatusCode.Forbidden, (await GateDrive.SendJsonAsync(app, userClient, userCookies, Host, userId, HttpMethod.Post, "/api/Pipelines/runs/any/retry", new { }, output, "a registered user retries")).Status);

                using var anonymous = GateDrive.NewClient(app, Host);
                Assert.NotEqual(HttpStatusCode.OK, (await GateDrive.GetAsync(anonymous, null, Host, "/api/Pipelines", output, "anonymous lists")).Status);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task With_the_switch_off_the_routes_do_not_exist_and_no_page_is_placed()
        {
            using var scratch = await ScratchAsync(enabled: false);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                Assert.Equal(HttpStatusCode.NotFound, (await GateDrive.GetAsync(client, cookies, Host, "/api/Pipelines", output, "the host lists with the switch off")).Status);
                var page = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                    (await sp.GetRequiredService<IPageRepository>().GetPagesAsync(alias.SiteId)).SingleOrDefault(p => p.Path == "admin/pipelines"));
                Assert.Null(page);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private Task<ScratchGateInstall> ScratchAsync(bool enabled, JsonObject? pipelines = null)
        {
            pipelines ??= new JsonObject();
            pipelines["Enabled"] = enabled;
            return GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false,
                extraSettings: new JsonObject { ["Pipelines"] = pipelines });
        }

        private async Task<PipelineRunView> UntilRunAsync(HttpClient client, System.Net.CookieContainer cookies, string definitionId, string label)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var runs = await GetAsync<List<PipelineRunView>>(client, cookies, $"/api/Pipelines/runs?definitionId={definitionId}", label);
                if (runs.Count > 0)
                {
                    return await UntilFinishedAsync(client, cookies, runs[0].Id);
                }

                await Task.Delay(100);
            }

            throw new Xunit.Sdk.XunitException($"No run of {definitionId} appeared for '{label}'.");
        }

        /// <summary>
        /// BL-95 increment (e), the triggers over real HTTP: a webhook pipeline started by the PUBLIC receiver (HMAC over
        /// the raw body under the site's secret; 401 without it; 202 for a name nothing listens under; 404 with the
        /// receiver's switch off), an Event pipeline started by the site's event endpoint through the bus and the reverse
        /// bridge on the bus worker, and a schedule pipeline whose trigger is indexed on the site with the task composed.
        /// </summary>
        [Fact]
        public async Task Triggers_start_runs_from_the_webhook_receiver_and_the_event_endpoint_and_a_schedule_is_indexed()
        {
            const string secret = "surface-webhook-secret";
            using var scratch = await ScratchAsync(enabled: true, new JsonObject { ["Webhooks"] = new JsonObject { ["Enabled"] = true, ["Secret"] = secret } });
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);

                // The trigger steps are in the catalog.
                var steps = await GetAsync<List<StepCatalogEntry>>(client, cookies, "/api/Pipelines/steps", "step catalog");
                Assert.Contains(steps, s => s.Type == "Stomme.Schedule" && s.Category == "Triggers");
                Assert.Contains(steps, s => s.Type == "Stomme.Webhook" && s.Category == "Triggers");
                Assert.Contains(steps, s => s.Type == "Stomme.Event");

                // A webhook pipeline: Webhook(orders) -> Log.
                var webhook = await PostAsync<PipelineView>(app, client, cookies, hostId, "/api/Pipelines", new CreatePipelineRequest { Name = "Orders webhook" }, "create webhook pipeline");
                var badName = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Put, $"/api/Pipelines/{webhook.DefinitionId}/graph", new PipelineGraph { Steps = { new PipelineGraphStep { Id = "w", Type = "Stomme.Webhook", Inputs = { ["WebhookName"] = "orders/created" } } } }, output, "a webhook name that cannot be a URL segment");
                Assert.Equal(HttpStatusCode.BadRequest, badName.Status);
                await PutAsync<PipelineView>(app, client, cookies, hostId, $"/api/Pipelines/{webhook.DefinitionId}/graph", new PipelineGraph
                {
                    Steps =
                    {
                        new PipelineGraphStep { Id = "w", Type = "Stomme.Webhook", Name = "Orders", Inputs = { ["WebhookName"] = "orders" } },
                        new PipelineGraphStep { Id = "log", Type = "Stomme.LogMessage", Name = "Log", Inputs = { ["Message"] = "order received", ["Level"] = "Information" } },
                    },
                    Connections = { new PipelineGraphConnection { SourceId = "w", TargetId = "log" } },
                }, "save the webhook graph");
                Assert.True((await PostAsync<PublishResult>(app, client, cookies, hostId, $"/api/Pipelines/{webhook.DefinitionId}/publish", new { }, "publish webhook")).Succeeded);

                // The receiver: anonymous, signed. A bad signature is 401; an unknown name is 202 all the same.
                var body = System.Text.Encoding.UTF8.GetBytes("{\"order\":42}");
                using var anonymous = GateDrive.NewClient(app, Host);
                Assert.Equal(HttpStatusCode.Unauthorized, (await PostRawAsync(anonymous, "/api/Pipelines/webhooks/orders", body, "sha256=" + new string('0', 64))).StatusCode);
                Assert.Equal(HttpStatusCode.Unauthorized, (await PostRawAsync(anonymous, "/api/Pipelines/webhooks/orders", body, null)).StatusCode);
                Assert.Equal(HttpStatusCode.Accepted, (await PostRawAsync(anonymous, "/api/Pipelines/webhooks/nobody-listens", body, Stomme.Modules.Pipelines.Triggers.PipelineWebhooks.ComputeSignature(secret, body))).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await PostRawAsync(anonymous, "/api/Pipelines/webhooks/not a name", body, Stomme.Modules.Pipelines.Triggers.PipelineWebhooks.ComputeSignature(secret, body))).StatusCode);
                Assert.Equal(HttpStatusCode.Accepted, (await PostRawAsync(anonymous, "/api/Pipelines/webhooks/Orders", body, Stomme.Modules.Pipelines.Triggers.PipelineWebhooks.ComputeSignature(secret, body))).StatusCode);
                var webhookRun = await UntilRunAsync(client, cookies, webhook.DefinitionId, "the webhook's run");
                Assert.Equal("Finished", webhookRun.Status);
                var webhookLog = await GetAsync<List<PipelineRunLogLine>>(client, cookies, $"/api/Pipelines/runs/{webhookRun.Id}/log", "webhook run log");
                Assert.Contains(webhookLog, line => line.StepType == "Stomme.Webhook" && line.EventName == "Completed");
                Assert.Contains(webhookLog, line => line.StepType == "Stomme.LogMessage" && line.Message == "order received");

                // An event pipeline: Event(order.paid) -> Log, raised through the site's endpoint (per key) and the bus.
                var evented = await PostAsync<PipelineView>(app, client, cookies, hostId, "/api/Pipelines", new CreatePipelineRequest { Name = "On order paid" }, "create event pipeline");
                await PutAsync<PipelineView>(app, client, cookies, hostId, $"/api/Pipelines/{evented.DefinitionId}/graph", new PipelineGraph
                {
                    Steps =
                    {
                        new PipelineGraphStep { Id = "e", Type = "Stomme.Event", Name = "Order paid", Inputs = { ["EventName"] = "order.paid" } },
                        new PipelineGraphStep { Id = "log", Type = "Stomme.LogMessage", Name = "Log", Inputs = { ["Message"] = "paid", ["Level"] = "Information" } },
                    },
                    Connections = { new PipelineGraphConnection { SourceId = "e", TargetId = "log" } },
                }, "save the event graph");
                Assert.True((await PostAsync<PublishResult>(app, client, cookies, hostId, $"/api/Pipelines/{evented.DefinitionId}/publish", new { }, "publish event pipeline")).Succeeded);
                var raised = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, "/api/Pipelines/events/order.paid?correlationId=order-42", new { order = 42 }, output, "raise the event");
                Assert.Equal(HttpStatusCode.Accepted, raised.Status);
                var eventRun = await UntilRunAsync(client, cookies, evented.DefinitionId, "the event's run");
                Assert.Equal(("Finished", "order-42"), (eventRun.Status, eventRun.CorrelationId));

                // A schedule pipeline: its trigger is indexed on the site, and the schedules task is composed per tenant.
                var scheduled = await PostAsync<PipelineView>(app, client, cookies, hostId, "/api/Pipelines", new CreatePipelineRequest { Name = "Nightly" }, "create scheduled pipeline");
                var badCron = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Put, $"/api/Pipelines/{scheduled.DefinitionId}/graph", new PipelineGraph { Steps = { new PipelineGraphStep { Id = "s", Type = "Stomme.Schedule", Inputs = { ["Cron"] = "every night" } } } }, output, "a cron that does not parse");
                Assert.Equal(HttpStatusCode.BadRequest, badCron.Status);
                await PutAsync<PipelineView>(app, client, cookies, hostId, $"/api/Pipelines/{scheduled.DefinitionId}/graph", new PipelineGraph
                {
                    Steps =
                    {
                        new PipelineGraphStep { Id = "s", Type = "Stomme.Schedule", Name = "Nightly", Inputs = { ["Cron"] = "0 2 * * *" } },
                        new PipelineGraphStep { Id = "log", Type = "Stomme.LogMessage", Name = "Log", Inputs = { ["Message"] = "nightly", ["Level"] = "Information" } },
                    },
                    Connections = { new PipelineGraphConnection { SourceId = "s", TargetId = "log" } },
                }, "save the schedule graph");
                Assert.True((await PostAsync<PublishResult>(app, client, cookies, hostId, $"/api/Pipelines/{scheduled.DefinitionId}/publish", new { }, "publish scheduled")).Succeeded);
                var indexed = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var bridge = sp.GetRequiredService<Stomme.Modules.Pipelines.Tenancy.IEngineTenantBridge>();
                    using (bridge.Enter(await bridge.ResolveAsync(alias.TenantId, alias.SiteId)))
                    {
                        var triggers = await sp.GetRequiredService<Stomme.Engine.Pipelines.Runtime.ITriggerStore>().FindManyAsync(new Stomme.Engine.Pipelines.Runtime.Filters.TriggerFilter { PipelineDefinitionId = scheduled.DefinitionId });
                        return triggers.Select(trigger => (trigger.Name, Stomme.Modules.Pipelines.Triggers.ScheduledPipelinesTask.ReadCron(trigger))).ToList();
                    }
                });
                Assert.Equal(new[] { ("Stomme.Schedule", "0 2 * * *") }, indexed);
                Assert.Contains(app.Services.CreateScope().ServiceProvider.GetServices<Stomme.Engine.Common.IRecurringTask>(), task => task is Stomme.Modules.Pipelines.Triggers.ScheduledPipelinesTask);
            }
            finally
            {
                await app.DisposeAsync();
            }

            // With the receiver's switch off the route does not exist, whatever the signature.
            using var plain = await ScratchAsync(enabled: true);
            var plainApp = await GateDrive.InstallAsync(plain, output);
            try
            {
                using var anonymous = GateDrive.NewClient(plainApp, Host);
                var body = System.Text.Encoding.UTF8.GetBytes("{}");
                using var off = await PostRawAsync(anonymous, "/api/Pipelines/webhooks/orders", body, Stomme.Modules.Pipelines.Triggers.PipelineWebhooks.ComputeSignature(secret, body));
                Assert.True(off.StatusCode == HttpStatusCode.NotFound, $"{(int)off.StatusCode} {off.StatusCode}: {await off.Content.ReadAsStringAsync()}");
            }
            finally
            {
                await plainApp.DisposeAsync();
            }
        }

        private static async Task<HttpResponseMessage> PostRawAsync(HttpClient client, string path, byte[] body, string? signature)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri("http://" + Host), path));
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            if (signature is not null)
            {
                request.Headers.TryAddWithoutValidation(Stomme.Modules.Pipelines.Triggers.PipelineWebhooks.SignatureHeaderName, signature);
            }

            return await client.SendAsync(request);
        }

        private async Task<T> GetAsync<T>(HttpClient client, System.Net.CookieContainer cookies, string path, string label)
        {
            var answer = await GateDrive.GetAsync(client, cookies, Host, path, output, label);
            Assert.True(answer.Status == HttpStatusCode.OK, $"[{label}] GET {path} -> {(int)answer.Status}: {GateDrive.Truncate(answer.Body)}");
            return JsonSerializer.Deserialize<T>(answer.Body, Json)!;
        }

        private async Task<T> PostAsync<T>(Microsoft.AspNetCore.Builder.WebApplication app, HttpClient client, System.Net.CookieContainer cookies, int actor, string path, object body, string label)
        {
            var answer = await GateDrive.SendJsonAsync(app, client, cookies, Host, actor, HttpMethod.Post, path, body, output, label);
            Assert.True(answer.Status == HttpStatusCode.OK, $"[{label}] POST {path} -> {(int)answer.Status}: {GateDrive.Truncate(answer.Body)}");
            return JsonSerializer.Deserialize<T>(answer.Body, Json)!;
        }

        private async Task<T> PutAsync<T>(Microsoft.AspNetCore.Builder.WebApplication app, HttpClient client, System.Net.CookieContainer cookies, int actor, string path, object body, string label)
        {
            var answer = await GateDrive.SendJsonAsync(app, client, cookies, Host, actor, HttpMethod.Put, path, body, output, label);
            Assert.True(answer.Status == HttpStatusCode.OK, $"[{label}] PUT {path} -> {(int)answer.Status}: {GateDrive.Truncate(answer.Body)}");
            return JsonSerializer.Deserialize<T>(answer.Body, Json)!;
        }

        private async Task<PipelineRunView> UntilFinishedAsync(HttpClient client, System.Net.CookieContainer cookies, string runId)
        {
            PipelineRunView? run = null;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                var answer = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}", output, "poll the run");
                if (answer.Status == HttpStatusCode.OK)
                {
                    run = JsonSerializer.Deserialize<PipelineRunView>(answer.Body, Json);
                    if (run?.IsFinished == true)
                    {
                        return run;
                    }
                }

                await Task.Delay(250);
            }

            throw new Xunit.Sdk.XunitException($"the run {runId} did not finish within 30 s (last: {run?.Status}/{run?.SubStatus})");
        }
    }
}
